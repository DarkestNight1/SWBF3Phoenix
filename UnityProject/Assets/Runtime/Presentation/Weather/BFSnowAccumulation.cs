using UnityEngine;

/// <summary>
/// How much snow has settled, and where it can settle at all.
/// </summary>
/// <remarks>
/// Snow does not coat a scene uniformly - that is the tell that makes fake
/// snow look like paint. It gathers on upward-facing surfaces, thins on
/// slopes, misses anything sheltered from above, and never lands on a hot or
/// vertical surface at all. All four of those are cheap to express as a shader
/// mask driven by the surface normal and an exposure term, so that is what
/// this publishes.
///
/// Coverage is global and per-map: Hoth starts at full coverage whether or not
/// it is currently snowing, because Hoth is already covered. Snowfall raises
/// it, melt lowers it, and the surface's own receptivity gates it - so lava
/// stays bare in a blizzard.
/// </remarks>
public sealed class BFSnowAccumulation : MonoBehaviour
{
    public static BFSnowAccumulation Instance { get; private set; }

    /// <summary>Settled snow, 0 none to 1 full coverage.</summary>
    public float Coverage { get; private set; }

    public float TargetCoverage { get; private set; }

    /// <summary>Seconds of heavy snowfall to go from bare to covered.</summary>
    public float AccumulateSeconds = 180f;

    /// <summary>Seconds to melt away completely.</summary>
    public float MeltSeconds = 300f;

    /// <summary>
    /// How steep a surface can be and still hold snow, in degrees from up.
    /// Beyond this it slides off, which is why cliff faces stay dark on a
    /// white map.
    /// </summary>
    public float MaxSlopeAngle = 62f;

    /// <summary>Altitude above which coverage increases - snow line.</summary>
    public float SnowLineHeight = -10000f;

    static readonly int CoverageId = Shader.PropertyToID("_BFSnowCoverage");
    static readonly int SlopeId = Shader.PropertyToID("_BFSnowMaxSlopeCos");
    static readonly int SnowLineId = Shader.PropertyToID("_BFSnowLine");
    static readonly int TintId = Shader.PropertyToID("_BFSnowTint");

    void Awake() => Instance = this;

    void OnDestroy()
    {
        if (PhxGame.Instance != null) PhxGame.Instance.OnMapLoaded -= ApplyMapBaseline;
        if (Instance == this) Instance = null;
        Shader.SetGlobalFloat(CoverageId, 0f);
    }

    void Start()
    {
        if (PhxGame.Instance != null)
        {
            PhxGame.Instance.OnMapLoaded += ApplyMapBaseline;
        }
    }

    void ApplyMapBaseline()
    {
        float baseline = BFLightingDirector.Active.BaseSnowCoverage;
        Coverage = baseline;
        TargetCoverage = baseline;
        Publish();
    }

    /// <summary>Snowfall intensity, 0..1, from the weather system.</summary>
    public void SetSnowfall(float intensity)
    {
        float baseline = BFLightingDirector.Active.BaseSnowCoverage;
        TargetCoverage = Mathf.Max(baseline, Mathf.Clamp01(intensity));
    }

    void Update()
    {
        if (Mathf.Approximately(Coverage, TargetCoverage)) return;

        float rate = TargetCoverage > Coverage
            ? 1f / Mathf.Max(1f, AccumulateSeconds)
            : 1f / Mathf.Max(1f, MeltSeconds);

        Coverage = Mathf.MoveTowards(Coverage, TargetCoverage, rate * Time.deltaTime);
        Publish();
    }

    void Publish()
    {
        Shader.SetGlobalFloat(CoverageId, Coverage);

        // As a cosine so the shader compares against dot(normal, up) directly
        // rather than doing an acos per pixel.
        Shader.SetGlobalFloat(SlopeId, Mathf.Cos(MaxSlopeAngle * Mathf.Deg2Rad));
        Shader.SetGlobalFloat(SnowLineId, SnowLineHeight);
        Shader.SetGlobalColor(TintId, BFSurfaceProfile.Get(BFSurfaceType.Snow).DebrisColor);
    }

    /// <summary>
    /// Coverage on a specific surface at a specific orientation - what a
    /// shader would compute, for code that needs the same answer.
    /// </summary>
    public static float At(Vector3 normal, float height, BFSurfaceType surface)
    {
        if (Instance == null) return 0f;

        float receptivity = BFSurfaceProfile.Get(surface).SnowReceptivity;
        if (receptivity <= 0f) return 0f;

        float slope = Mathf.Clamp01(
            (Vector3.Dot(normal.normalized, Vector3.up) - Mathf.Cos(Instance.MaxSlopeAngle * Mathf.Deg2Rad)) /
            Mathf.Max(0.001f, 1f - Mathf.Cos(Instance.MaxSlopeAngle * Mathf.Deg2Rad)));

        float altitude = Instance.SnowLineHeight > -9999f
            ? Mathf.Clamp01((height - Instance.SnowLineHeight) / 50f)
            : 1f;

        return Instance.Coverage * receptivity * slope * altitude;
    }
}
