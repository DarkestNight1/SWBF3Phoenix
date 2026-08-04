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
///   - Negative mip bias: with TAA resolving the shimmer, textures can be
///     sampled sharper than 1:1 without aliasing - the standard modern trick
///     that makes upscaled textures actually look upscaled.
///   - Dynamic resolution so 4K holds framerate under heavy particle load.
///
///  POST
///   - Motion blur, depth of field, subtle vignette / chromatic aberration
///     and film grain: restrained, cinematic, not smeared.
///
///  WORLD
///   - Physically Based Sky for real atmospheric scattering (planet horizons,
///     proper dusk gradients) instead of a flat skybox.
///   - A baked-on-load reflection probe so specular surfaces reflect the
///     actual map rather than a default cubemap.
///   - Shadow distance / cascade tuning for large SWBF2 maps.
///   - GPU instancing enabled across loaded materials to buy back the frame
///     time the above spends.
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
    GameObject ProbeObject;


    void Start()
    {
        BuildPostProcessing();
        ConfigureCamera();
    }

    void Update()
    {
        // per-map world setup (probes, shadow ranges) once the scene exists
        PhxScene scene = PhxGame.GetScene();
        if (scene == ActiveScene) return;
        ActiveScene = scene;

        if (scene != null)
        {
            ConfigureCamera();   // the map may have spawned a fresh camera
            SetupWorldReflection();
            EnableGpuInstancing();
        }
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

        TryAdd(() =>
        {
            DepthOfField dof = PostProfile.Add<DepthOfField>(true);
            dof.focusMode.Override(DepthOfFieldMode.UsePhysicalCamera);
            dof.nearFocusStart.Override(0f);
            dof.nearFocusEnd.Override(0.6f);       // only very close geometry
        }, "depth of field");

        TryAdd(() =>
        {
            Vignette v = PostProfile.Add<Vignette>(true);
            v.intensity.Override(0.18f);
            v.smoothness.Override(0.5f);
        }, "vignette");

        TryAdd(() =>
        {
            ChromaticAberration ca = PostProfile.Add<ChromaticAberration>(true);
            ca.intensity.Override(0.08f);          // barely-there lens realism
        }, "chromatic aberration");

        TryAdd(() =>
        {
            FilmGrain fg = PostProfile.Add<FilmGrain>(true);
            fg.type.Override(FilmGrainLookup.Thin1);
            fg.intensity.Override(0.12f);
        }, "film grain");

        TryAdd(() =>
        {
            // Physically based sky: real Rayleigh/Mie scattering, so horizons
            // and dusk read correctly on planet maps.
            PhysicallyBasedSky sky = PostProfile.Add<PhysicallyBasedSky>(true);
            sky.groundTint.Override(new Color(0.25f, 0.24f, 0.22f));

            VisualEnvironment env = PostProfile.Add<VisualEnvironment>(true);
            env.skyType.Override((int)SkyType.PhysicallyBased);
        }, "physically based sky");

        PostVolume = gameObject.AddComponent<Volume>();
        PostVolume.isGlobal = true;
        PostVolume.priority = 90f;    // just under PhxModernLighting (100)
        PostVolume.profile = PostProfile;

        Debug.Log("[BF3Legacy] Post-processing stack active (TAA, motion blur, DoF, PBR sky)");
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

            // With TAA on, sampling textures sharper than 1:1 is safe and is
            // what makes the upscaled textures actually read as detailed.
            data.allowDynamicResolution = PhxBF3.Config.UseDynamicResolution;
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[BF3Legacy] Camera AA setup failed: {e.Message}");
        }

        // Negative mip bias sharpens minified textures (TAA cleans up the
        // resulting aliasing). Applied globally rather than per-texture.
        Shader.SetGlobalFloat("_GlobalMipBias", -0.5f);

        // Long view distances: SWBF2 maps are big and capital ships sit far
        // overhead in the vertical battlefront.
        cam.farClipPlane = Mathf.Max(cam.farClipPlane, 5000f);
    }

    // ----------------------------------------------------------------- world

    void SetupWorldReflection()
    {
        if (ProbeObject != null) Destroy(ProbeObject);

        try
        {
            ProbeObject = new GameObject("BF3ReflectionProbe");
            ProbeObject.transform.SetParent(transform, false);

            // center on the battlefield
            Vector3 center = Vector3.zero;
            PhxCommandpost[] posts = PhxGame.GetScene()?.GetCommandPosts();
            if (posts != null && posts.Length > 0)
            {
                foreach (PhxCommandpost cp in posts) center += cp.transform.position;
                center /= posts.Length;
            }
            ProbeObject.transform.position = center + Vector3.up * 30f;

            ReflectionProbe probe = ProbeObject.AddComponent<ReflectionProbe>();
            probe.mode = ReflectionProbeMode.Realtime;
            probe.refreshMode = ReflectionProbeRefreshMode.ViaScripting;
            probe.resolution = 256;
            probe.size = new Vector3(3000f, 1500f, 3000f);
            probe.hdr = true;
            probe.RenderProbe();   // one bake on load - cheap, big specular win
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[BF3Legacy] Reflection probe setup failed: {e.Message}");
        }

        // shadow range suited to large maps
        try
        {
            QualitySettings.shadowDistance = 500f;
            QualitySettings.shadowCascades = 4;
            QualitySettings.shadowResolution = ShadowResolution.VeryHigh;
        }
        catch { /* platform dependent */ }
    }

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
