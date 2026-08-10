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
        if (budget <= 0) return;

        PhxScene scene = PhxGame.GetScene();
        PhxCommandpost[] posts = scene?.GetCommandPosts();
        if (posts == null || posts.Length == 0) return;

        int placed = 0;
        for (int i = 0; i < posts.Length && placed < budget; ++i)
        {
            if (posts[i] == null) continue;

            Place(posts[i].transform.position + Vector3.up * ProbeHeight);
            ++placed;
        }

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
        ConfiguredRenderers = 0;

        Renderer[] renderers = FindObjectsOfType<Renderer>();
        for (int i = 0; i < renderers.Length; ++i)
        {
            Configure(renderers[i]);
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

        // Characters must both receive and cast; a soldier that casts no
        // shadow reads as pasted onto the scene however well he is lit.
        if (dynamic)
        {
            renderer.shadowCastingMode = ShadowCastingMode.On;
            renderer.receiveShadows = true;
        }

        ++ConfiguredRenderers;
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
