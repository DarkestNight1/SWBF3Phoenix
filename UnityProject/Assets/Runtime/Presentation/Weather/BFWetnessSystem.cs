using UnityEngine;

/// <summary>
/// How wet the world is, and what that does to how it reflects light.
/// </summary>
/// <remarks>
/// Wetness is a global scalar plus a per-surface receptivity, published as
/// shader uniforms rather than pushed into individual materials. That matters
/// for two reasons: a map has thousands of materials and touching them all
/// every frame is not viable, and the imported materials are stock data - a
/// global uniform leaves them exactly as they were.
///
/// The chain is the one that actually happens physically:
///
/// <code>
/// rain -> water film -> lower roughness -> more specular -> darker albedo
/// </code>
///
/// including the last step, which is the one usually missed: wet ground is
/// darker as well as shinier, because the film traps light rather than
/// scattering it back out.
/// </remarks>
public sealed class BFWetnessSystem : MonoBehaviour
{
    public static BFWetnessSystem Instance { get; private set; }

    /// <summary>Current wetness, 0 dry to 1 saturated.</summary>
    public float Wetness { get; private set; }

    /// <summary>What wetness is heading toward - rain raises it, sun lowers it.</summary>
    public float TargetWetness { get; private set; }

    /// <summary>Seconds to soak from dry to fully wet in heavy rain.</summary>
    public float SoakSeconds = 25f;

    /// <summary>Seconds to dry out completely once the rain stops.</summary>
    public float DrySeconds = 90f;

    static readonly int WetnessId = Shader.PropertyToID("_BFWetness");
    static readonly int PuddleId = Shader.PropertyToID("_BFPuddleDepth");

    void Awake() => Instance = this;

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
        Shader.SetGlobalFloat(WetnessId, 0f);
        Shader.SetGlobalFloat(PuddleId, 0f);
    }

    void Start()
    {
        if (PhxGame.Instance != null)
        {
            PhxGame.Instance.OnMapLoaded += ApplyMapBaseline;
        }
    }

    /// <summary>
    /// Some environments are simply wet. Kamino is a storm over open ocean and
    /// Dagobah is a swamp; both start soaked whatever the weather is doing.
    /// </summary>
    void ApplyMapBaseline()
    {
        float baseline = BFLightingDirector.Active.BaseWetness;
        Wetness = baseline;
        TargetWetness = baseline;
        Publish();
    }

    /// <summary>Rain intensity, 0..1, from the weather system.</summary>
    public void SetPrecipitation(float intensity)
    {
        float baseline = BFLightingDirector.Active.BaseWetness;
        TargetWetness = Mathf.Max(baseline, Mathf.Clamp01(intensity));
    }

    void Update()
    {
        if (Mathf.Approximately(Wetness, TargetWetness)) return;

        // Soaking is much faster than drying, which is both true and the thing
        // that makes weather read as having consequences.
        float rate = TargetWetness > Wetness
            ? 1f / Mathf.Max(1f, SoakSeconds)
            : 1f / Mathf.Max(1f, DrySeconds);

        Wetness = Mathf.MoveTowards(Wetness, TargetWetness, rate * Time.deltaTime);
        Publish();
    }

    void Publish()
    {
        Shader.SetGlobalFloat(WetnessId, Wetness);

        // Puddles lag the film and only appear once the surface is saturated,
        // which is why a shower does not immediately produce standing water.
        Shader.SetGlobalFloat(PuddleId, Mathf.Clamp01((Wetness - 0.6f) / 0.4f));
    }

    /// <summary>
    /// Effective wetness on a particular surface.
    /// </summary>
    /// <remarks>
    /// Snow does not get shiny in the rain and lava does not get wet at all,
    /// which is what the per-surface receptivity is for. Callers that shade a
    /// known surface should use this rather than the global value.
    /// </remarks>
    public static float For(BFSurfaceType surface)
    {
        if (Instance == null) return 0f;

        return Instance.Wetness * BFSurfaceProfile.Get(surface).WetnessReceptivity;
    }

    /// <summary>Smoothness a surface should have at the current wetness.</summary>
    public static float SmoothnessFor(BFSurfaceType surface)
    {
        BFSurfaceProfile profile = BFSurfaceProfile.Get(surface);
        return Mathf.Lerp(profile.BaseSmoothness, profile.WetSmoothness, For(surface));
    }
}
