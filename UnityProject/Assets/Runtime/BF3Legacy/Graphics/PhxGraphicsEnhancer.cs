using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

/// <summary>
/// Graphics fidelity pass that complements PhxModernLighting (which owns the
/// lighting/exposure volume). This one covers everything that is NOT a volume
/// override plus the cinematic post stack:
///
///  CAMERA
///   - Temporal anti-aliasing: the single biggest image-quality win on
///     high-frequency 2005 geometry (railings, antennae, foliage alpha).
///   - Opting the camera into dynamic resolution, which BFDynamicResolution
///     then drives.
///
///  POST
///   - Motion blur, subtle vignette and optional lens artifacts: restrained,
///     cinematic, not smeared.
///
///  WORLD
///   - GPU instancing enabled across loaded materials to buy back the frame
///     time the post stack spends.
///
/// What this deliberately does NOT own: sky and fog (per-map, and
/// BFLightingDirector owns them), reflection probes (BFReflectionProbeManager,
/// which budgets them), and shadow distance/cascades (HDShadowSettings, again
/// via the director). Each of those was tried here first and had to be given
/// up, because a global writer and a per-map writer cannot both be right.
///
/// Everything here is behind PhxBF3Config.GraphicsEnhancements and degrades
/// gracefully: each step is independently try-guarded, because HDRP's API
/// surface shifts between versions and a missing override must not take down
/// the whole renderer.
/// </summary>
public class PhxGraphicsEnhancer : MonoBehaviour
{
    Volume PostVolume;
    VolumeProfile PostProfile;
    PhxScene ActiveScene;


    void Start()
    {
        BuildPostProcessing();
        ConfigureCamera();
    }

    void OnEnable()
    {
        if (PhxGame.Instance != null) PhxGame.Instance.OnMapLoaded += OnMapLoaded;
    }

    void OnDisable()
    {
        if (PhxGame.Instance != null) PhxGame.Instance.OnMapLoaded -= OnMapLoaded;
    }

    /// <summary>
    /// Per-map setup, once the map has actually finished importing.
    /// </summary>
    /// <remarks>
    /// This used to poll PhxGame.GetScene() from Update and fire the moment the
    /// scene object existed, which is well before its contents do. Every other
    /// system here hangs off OnMapLoaded; this one did not, so EnableGpuInstancing
    /// walked a partially imported scene and missed every material that arrived
    /// after it ran.
    /// </remarks>
    void OnMapLoaded()
    {
        ActiveScene = PhxGame.GetScene();

        ConfigureCamera();   // the map may have spawned a fresh camera
        EnableGpuInstancing();
    }

    // ------------------------------------------------------------------ post

    void BuildPostProcessing()
    {
        PostProfile = ScriptableObject.CreateInstance<VolumeProfile>();
        PostProfile.name = "BF3LegacyPost";

        TryAdd(() =>
        {
            MotionBlur mb = PostProfile.Add<MotionBlur>(true);
            mb.intensity.Override(0.35f);          // present, not smeary
            mb.maximumVelocity.Override(200f);
        }, "motion blur");

        // NOTE: no DepthOfField. It was configured with
        // focusMode = UsePhysicalCamera, but nearFocusStart/nearFocusEnd only
        // take effect in Manual mode - so those "only very close geometry"
        // limits did nothing and the physical camera's default aperture blurred
        // the whole scene at distance, on every map. A third-person shooter
        // wants the battlefield sharp, so this is dropped rather than retuned.

        TryAdd(() =>
        {
            Vignette v = PostProfile.Add<Vignette>(true);
            v.intensity.Override(0.18f);
            v.smoothness.Override(0.5f);
        }, "vignette");

        // Lens artifacts, off unless asked for - see Config.LensSimulation.
        // Chromatic aberration in particular is clearly visible as red/cyan
        // fringing wherever a bright edge meets a dark one, which on a map of
        // pale pillars against shadow is most of the frame.
        if (PhxBF3.Config.LensSimulation)
        {
            TryAdd(() =>
            {
                ChromaticAberration ca = PostProfile.Add<ChromaticAberration>(true);
                ca.intensity.Override(0.08f);
            }, "chromatic aberration");

            TryAdd(() =>
            {
                FilmGrain fg = PostProfile.Add<FilmGrain>(true);
                fg.type.Override(FilmGrainLookup.Thin1);
                fg.intensity.Override(0.12f);
            }, "film grain");
        }

        // NOTE: no PhysicallyBasedSky / VisualEnvironment override here.
        //
        // Forcing HDRP's Rayleigh/Mie atmosphere globally replaced whatever sky
        // each map authored and dumped a huge amount of sky light into every
        // scene - blowing out bright-albedo maps (Mygeeto's snow) to pure white
        // while the visible skybox stayed dark. Like the Fog override removed
        // from PhxModernLighting, a sky is inherently per-map: it is meaningless
        // on interiors and space maps, and destructive where the level already
        // ships its own. Maps now keep their imported sky.

        PostVolume = gameObject.AddComponent<Volume>();
        PostVolume.isGlobal = true;
        PostVolume.priority = 90f;    // just under PhxModernLighting (100)
        PostVolume.profile = PostProfile;

        Debug.Log("[BF3Legacy] Post-processing stack active (TAA, motion blur, vignette)");
    }

    static void TryAdd(System.Action action, string what)
    {
        try
        {
            action();
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[BF3Legacy] Could not configure {what}: {e.Message}");
        }
    }

    // ---------------------------------------------------------------- camera

    void ConfigureCamera()
    {
        Camera cam = Camera.main;
        if (cam == null) return;

        HDAdditionalCameraData data = cam.GetComponent<HDAdditionalCameraData>();
        if (data == null) data = cam.gameObject.AddComponent<HDAdditionalCameraData>();

        try
        {
            // TAA: resolves the shimmer that 2005-era alpha-tested geometry
            // (fences, foliage, antennae) produces at high resolution
            data.antialiasing = HDAdditionalCameraData.AntialiasingMode.TemporalAntialiasing;
            data.taaSharpenStrength = 0.6f;
            data.dithering = true;
            data.stopNaNs = true;

            // Both flags, not just the HD one. The pipeline asset asks for
            // hardware dynamic resolution, and HDRP quietly forces the software
            // path when the plain Camera field is false - which it was, so the
            // whole feature ran in a mode nobody chose and nothing reported.
            bool dynamic = PhxBF3.Config.UseDynamicResolution;
            data.allowDynamicResolution = dynamic;
            cam.allowDynamicResolution = dynamic;
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[BF3Legacy] Camera AA setup failed: {e.Message}");
        }

        // A global mip bias used to be set here via "_GlobalMipBias". That is
        // an HDRP 12+ internal and is defined nowhere in 10.7, so the write did
        // nothing for as long as it existed. The sharpening it was meant to
        // provide is now applied per texture at import, in
        // TextureLoader.ImportTexture, which is the 10.7-era equivalent.

        // Long view distances: SWBF2 maps are big and capital ships sit far
        // overhead in the vertical battlefront.
        cam.farClipPlane = Mathf.Max(cam.farClipPlane, 5000f);
    }

    // ----------------------------------------------------------------- world

    // A single realtime 3000x1500x3000 reflection probe used to be built here,
    // and three QualitySettings shadow fields set beside it.
    //
    // Both were removed rather than fixed. BFReflectionProbeManager owns
    // reflection probes, places them per command post against a budget, and
    // reports how many it placed - a second unbudgeted probe competed with
    // those for the pipeline's probe cache without any budget knowing it
    // existed. The shadow fields belong to the built-in pipeline and are
    // ignored under HDRP, which takes both distance and cascade count from the
    // HDShadowSettings volume override that BFLightingDirector writes.

    /// <summary>
    /// Turn on GPU instancing for loaded materials. SWBF2 maps repeat the same
    /// props hundreds of times, so this reclaims a lot of draw calls - paying
    /// for the more expensive post stack above.
    /// </summary>
    void EnableGpuInstancing()
    {
        int count = 0;
        foreach (Renderer r in FindObjectsOfType<Renderer>())
        {
            foreach (Material m in r.sharedMaterials)
            {
                if (m != null && !m.enableInstancing)
                {
                    m.enableInstancing = true;
                    count++;
                }
            }
        }
        if (count > 0)
        {
            Debug.Log($"[BF3Legacy] GPU instancing enabled on {count} materials");
        }
    }

    void OnDestroy()
    {
        if (PostProfile != null) ScriptableObject.Destroy(PostProfile);
    }
}
