using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

/// <summary>
/// Places reflection probes where they will actually be seen.
/// </summary>
/// <remarks>
/// Without probes, every metallic surface in the game reflects the sky and
/// nothing else - so a stormtrooper standing in a corridor has a bright
/// outdoor sky in his pauldrons, and a speeder parked against a wall reflects
/// open air. It is one of the loudest tells that a scene is not lit by its
/// surroundings.
///
/// Probes are placed at the map's command posts rather than on a grid. That is
/// not a shortcut: command posts are where the level designers put the fighting,
/// which is where players spend their time and where reflections are worth
/// paying for. A grid over a square kilometre would spend the entire budget on
/// empty terrain.
///
/// Baked once on map load, never updated per frame. A realtime probe per
/// command post would cost six cubemap faces each and there is nothing in these
/// scenes moving enough to justify it.
/// </remarks>
public sealed class BFReflectionProbeManager : MonoBehaviour
{
    public static BFReflectionProbeManager Instance { get; private set; }

    readonly List<GameObject> Probes = new List<GameObject>();

    /// <summary>How far a probe's influence reaches, in metres.</summary>
    const float InfluenceRadius = 45f;

    /// <summary>Height above the post the probe sits at - roughly eye level.</summary>
    const float ProbeHeight = 3f;

    /// <summary>Probes actually placed for this map.</summary>
    public int Placed => Probes.Count;

    /// <summary>
    /// Whether placement has finished for this map.
    /// </summary>
    /// <remarks>
    /// Placement runs a frame per probe, so anything reading Placed during the
    /// OnMapLoaded dispatch sees zero. The render budget report did exactly
    /// that and reported no probes on every map.
    /// </remarks>
    public bool PlacementComplete { get; private set; } = true;

    void Awake() => Instance = this;

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
        Clear();
    }

    void Start()
    {
        if (PhxGame.Instance != null)
        {
            PhxGame.Instance.OnMapLoaded += Rebuild;
        }
    }

    public void Clear()
    {
        for (int i = 0; i < Probes.Count; ++i)
        {
            if (Probes[i] != null) Destroy(Probes[i]);
        }
        Probes.Clear();
    }

    void Rebuild()
    {
        Clear();

        int budget = BFPresentationQuality.ReflectionProbeBudget;
        if (budget <= 0)
        {
            PlacementComplete = true;
            return;
        }

        PlacementComplete = false;
        StopAllCoroutines();
        StartCoroutine(PlaceOverFrames(budget));
    }

    /// <summary>
    /// Place and render the probes a frame apart.
    /// </summary>
    /// <remarks>
    /// Each probe is six cubemap faces. Rendering ten of them in the frame the
    /// map finishes loading is sixty scene renders in one frame - which is not
    /// a frame, it is a stall, and it lands exactly where a player is already
    /// waiting. One per frame turns it into a fraction of a second nobody
    /// notices, and probes that have not rendered yet simply fall back to the
    /// sky, which is what the scene looked like anyway.
    /// </remarks>
    System.Collections.IEnumerator PlaceOverFrames(int budget)
    {
        // A frame for the scene to settle: probes rendered before the map's
        // own lights and materials are in place capture the wrong room.
        yield return null;

        PhxScene scene = PhxGame.GetScene();
        PhxCommandpost[] posts = scene?.GetCommandPosts();
        if (posts == null || posts.Length == 0)
        {
            // Still "complete" - a map with no command posts gets no probes,
            // and anything waiting on this must not wait forever.
            PlacementComplete = true;
            yield break;
        }

        int placed = 0;
        for (int i = 0; i < posts.Length && placed < budget; ++i)
        {
            if (posts[i] == null) continue;

            Place(posts[i].transform.position + Vector3.up * ProbeHeight);
            ++placed;
            yield return null;
        }

        PlacementComplete = true;
        Debug.Log($"[BFPresentation] {placed} reflection probe(s) placed at command posts.");
    }

    void Place(Vector3 position)
    {
        var obj = new GameObject("BFReflectionProbe");
        obj.transform.SetParent(transform, false);
        obj.transform.position = position;

        ReflectionProbe probe = obj.AddComponent<ReflectionProbe>();
        probe.mode = ReflectionProbeMode.Realtime;
        probe.refreshMode = ReflectionProbeRefreshMode.ViaScripting;
        probe.timeSlicingMode = ReflectionProbeTimeSlicingMode.IndividualFaces;
        probe.size = Vector3.one * (InfluenceRadius * 2f);
        probe.nearClipPlane = 0.3f;
        probe.farClipPlane = 400f;
        probe.resolution = BFPresentationQuality.Tier >= BFQualityTier.High ? 256 : 128;

        HDAdditionalReflectionData data = obj.AddComponent<HDAdditionalReflectionData>();
        data.influenceVolume.shape = InfluenceShape.Sphere;
        data.influenceVolume.sphereRadius = InfluenceRadius;

        // One render, then done. These scenes have no moving reflection
        // sources worth re-rendering six cubemap faces for.
        probe.RenderProbe();

        Probes.Add(obj);
    }
}

/// <summary>
/// Gives characters and props light from their surroundings rather than from
/// a global average.
/// </summary>
/// <remarks>
/// The complaint this addresses is specific: characters that look like they
/// were lit separately and composited in. That happens when every skinned
/// renderer takes the same scene-wide ambient probe, so a soldier in a dark
/// hangar is lit like a soldier on a snowfield.
///
/// Unity's light probe interpolation solves it, but only for renderers that
/// are actually configured to use it - and imported models are not, because
/// the importer builds them from raw mesh data with default renderer settings.
/// So this walks what the map produced and turns probe lighting on, including
/// for skinned meshes, where it matters most.
///
/// It does not bake probes: a runtime-assembled scene has no bake step. What
/// it does is make sure the probes that do exist, and the ambient the lighting
/// director sets, actually reach the things standing in them.
/// </remarks>
public sealed class BFLightProbeManager : MonoBehaviour
{
    public static BFLightProbeManager Instance { get; private set; }

    /// <summary>Renderers configured since load. Diagnostics.</summary>
    public int ConfiguredRenderers { get; private set; }

    void Awake() => Instance = this;

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    void Start()
    {
        if (PhxGame.Instance != null)
        {
            PhxGame.Instance.OnMapLoaded += ConfigureScene;
        }
    }

    void ConfigureScene()
    {
        StopAllCoroutines();
        StartCoroutine(ConfigureOverFrames());
    }

    /// <summary>Renderers configured per frame while walking the scene.</summary>
    /// <remarks>
    /// A conquest map has thousands, and touching all of them in one frame at
    /// the end of a load is a visible hitch on top of a load that is already
    /// long. Spread out, a renderer configured a few frames late is lit by the
    /// scene ambient in the meantime, which is what it would have had anyway.
    /// </remarks>
    const int ConfigureBatchSize = 400;

    System.Collections.IEnumerator ConfigureOverFrames()
    {
        ConfiguredRenderers = 0;
        yield return null;

        Renderer[] renderers = FindObjectsOfType<Renderer>();
        for (int i = 0; i < renderers.Length; ++i)
        {
            Configure(renderers[i]);

            if ((i + 1) % ConfigureBatchSize == 0) yield return null;
        }

        Debug.Log($"[BFPresentation] Environmental lighting configured on {ConfiguredRenderers} renderer(s).");
    }

    /// <summary>
    /// Configure one renderer. Public so newly spawned units - which arrive
    /// long after the map loaded - get the same treatment.
    /// </summary>
    public void Configure(Renderer renderer)
    {
        if (renderer == null) return;

        // Blended probes on anything that moves; a single probe is enough for
        // static scenery and costs less to resolve.
        bool dynamic = renderer is SkinnedMeshRenderer || !renderer.gameObject.isStatic;

        renderer.lightProbeUsage = dynamic
            ? LightProbeUsage.BlendProbes
            : LightProbeUsage.BlendProbes;

        renderer.reflectionProbeUsage = ReflectionProbeUsage.BlendProbes;

        // Never promote a renderer the importer switched off.
        //
        // This is a budget: it exists to take casters away, not to hand them
        // out. Promoting was actively destructive, because the importer turns
        // shadow casting off for a reason - the skydome is built with
        // shadowSensitive false, and this pass then walked every renderer in
        // the scene, saw an enormous object, and switched it back on. A dome
        // enclosing the whole level became a shadow caster, and the sun put
        // the inside of it across the map as one huge shadow.
        //
        // ShouldCastShadows only ever had a minimum size, so "big" always read
        // as "worth casting" - the one case where the object is backdrop
        // rather than scenery is exactly the case it got wrong.
        if (renderer.shadowCastingMode == ShadowCastingMode.Off)
        {
            ++ConfiguredRenderers;
            return;
        }

        // Characters must both receive and cast; a soldier that casts no
        // shadow reads as pasted onto the scene however well he is lit.
        if (dynamic)
        {
            renderer.shadowCastingMode = ShadowCastingMode.On;
            renderer.receiveShadows = true;
        }
        else
        {
            // Static scenery below a size threshold stops casting.
            //
            // A SWBF2 model is split one renderer per bone, so a level's
            // shadow-caster count is dominated by small fittings - handrails,
            // panels, brackets - whose shadow is a few pixels but which are
            // re-rendered for every cascade and every shadowed light. On the
            // measured Coruscant scene that is 273 casters against 322
            // renderers total.
            renderer.shadowCastingMode = ShouldCastShadows(renderer)
                ? ShadowCastingMode.On
                : ShadowCastingMode.Off;
        }

        ++ConfiguredRenderers;
    }

    static bool ShouldCastShadows(Renderer renderer)
    {
        float minRadius = BFPresentationQuality.MinShadowCasterRadius;
        if (minRadius <= 0f) return true;

        // Bounds rather than triangle count: what matters for a shadow is how
        // much of the frame it covers, and a large flat wall with two
        // triangles casts the shadow that actually reads.
        return renderer.bounds.extents.magnitude >= minRadius;
    }

    /// <summary>Configure everything under an object - one spawned unit.</summary>
    public static void ConfigureHierarchy(GameObject root)
    {
        if (root == null || Instance == null) return;

        Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < renderers.Length; ++i)
        {
            Instance.Configure(renderers[i]);
        }
    }
}
