using System;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Trades resolution for frame time when a map cannot afford its own settings.
/// </summary>
/// <remarks>
/// This is the safety valve the rest of the presentation layer is designed
/// around. Per-map budgets decide what a map is allowed to spend; when a map
/// spends it and still misses, something has to give, and giving up pixels is
/// far less visible than giving up shadows, volumetrics or reflections.
///
/// The pipeline asset had dynamic resolution disabled while the config asked
/// for it and the camera opted into it, and nothing ever registered a scaler -
/// so the feature was requested in three places and implemented in none.
///
/// Deliberately dull. A resolution that moves constantly reads worse than one
/// that is simply low, so this samples a median rather than a mean, waits half
/// a second between decisions, drops faster than it climbs, and stays put for
/// two seconds after a map load while shaders compile and probes render.
/// </remarks>
public sealed class BFDynamicResolution : MonoBehaviour
{
    public static BFDynamicResolution Instance { get; private set; }

    // How often a decision may be made. Each change invalidates TAA history,
    // costing a frame or two of softening, so this is the floor that keeps the
    // image from shimmering.
    const float DecisionInterval = 0.5f;

    // Asymmetric on purpose: dropping is invisible, climbing is a visible
    // pulse, so climb in smaller steps than you fall.
    const float StepDown = 5f;
    const float StepUp = 2.5f;

    // Nothing measured in the first seconds of a map is about the map.
    const float SettleAfterLoad = 2f;

    const int SampleCount = 30;

    static float Current = 100f;
    static float MapFloor = 0f;          // 0 = use the asset/config floor only

    readonly float[] Samples = new float[SampleCount];
    int SampleHead;
    int SampleFilled;

    float NextDecisionAt;
    float SettleUntil;

    // Stats for the render budget report, accumulated across the map.
    double GpuMsSum;
    int GpuMsCount;
    float StatsStartedAt;
    readonly System.Collections.Generic.List<float> GpuHistory = new System.Collections.Generic.List<float>(4096);
    readonly System.Collections.Generic.List<float> ScaleHistory = new System.Collections.Generic.List<float>(4096);

    FrameTiming[] Timings = new FrameTiming[1];
    bool WarnedNoTimings;
    string TimingSource = "none";

    /// <summary>The percentage the scaler last asked for, for diagnostics.</summary>
    public static float CurrentPercent => Current;

    /// <summary>This map's floor, or 0 if it uses the global one.</summary>
    public static float MapFloorPercent => MapFloor;

    /// <summary>
    /// Set the lowest percentage this map may fall to, or 0 for the default.
    /// </summary>
    /// <remarks>
    /// Foliage maps break down under upscaling far sooner than clean interiors
    /// or space, so the floor belongs to the map rather than to the tier.
    /// </remarks>
    public static void SetMapFloorPercent(float percent)
    {
        MapFloor = Mathf.Clamp(percent, 0f, 100f);
    }

    void OnEnable()
    {
        Instance = this;
        if (PhxGame.Instance != null) PhxGame.Instance.OnMapLoaded += OnMapLoaded;
    }

    void OnDisable()
    {
        if (Instance == this) Instance = null;
        if (PhxGame.Instance != null) PhxGame.Instance.OnMapLoaded -= OnMapLoaded;
    }

    void Start()
    {
        if (!PhxBF3.Config.UseDynamicResolution)
        {
            enabled = false;
            return;
        }

        // A fixed render scale means the player would rather have a constant
        // image than a moving one. Honour it and stay out of the way.
        float fixedScale = PhxBF3.Config.RenderScale;
        if (fixedScale > 0f && !Mathf.Approximately(fixedScale, 1f))
        {
            Current = Mathf.Clamp(fixedScale * 100f, 10f, 100f);
            DynamicResolutionHandler.SetDynamicResScaler(
                Scale, DynamicResScalePolicyType.ReturnsPercentage);
            Debug.Log($"[BFPresentation] Render scale pinned at {Current:F0}% by config; " +
                      "automatic scaling is off.");
            enabled = false;
            return;
        }

        Current = 100f;

        // Percentage, not the min/max lerp factor. The lerp policy hides the
        // resolution behind a 0..1 against asset-level bounds, which makes both
        // the per-map floor and the report awkward to express.
        DynamicResolutionHandler.SetDynamicResScaler(
            Scale, DynamicResScalePolicyType.ReturnsPercentage);

        StatsStartedAt = Time.unscaledTime;
    }

    void OnMapLoaded()
    {
        SettleUntil = Time.unscaledTime + SettleAfterLoad;
        NextDecisionAt = SettleUntil;
        SampleFilled = 0;
        SampleHead = 0;
        Current = 100f;

        GpuHistory.Clear();
        ScaleHistory.Clear();
        GpuMsSum = 0;
        GpuMsCount = 0;
        StatsStartedAt = Time.unscaledTime;
    }

    /// <summary>The registered scaler. Must not allocate - it runs per frame.</summary>
    static float Scale() => Current;

    void Update()
    {
        float frameMs = MeasureFrameMs();
        if (frameMs <= 0f) return;

        Samples[SampleHead] = frameMs;
        SampleHead = (SampleHead + 1) % SampleCount;
        if (SampleFilled < SampleCount) ++SampleFilled;

        GpuHistory.Add(frameMs);
        ScaleHistory.Add(Current);
        GpuMsSum += frameMs;
        ++GpuMsCount;

        float now = Time.unscaledTime;
        if (now < SettleUntil || now < NextDecisionAt) return;
        if (SampleFilled < SampleCount) return;

        NextDecisionAt = now + DecisionInterval;

        // Median, not mean. One hitching frame - a chunk of geometry streaming
        // in, a shader compiling - must not be allowed to drop the resolution
        // for everyone.
        float median = Median(Samples, SampleFilled);
        float target = TargetFrameMs();

        float min = Mathf.Max(MapFloor > 0f ? MapFloor : 0f,
                              PhxBF3.Config.MinDynamicResolutionPercent);
        min = Mathf.Clamp(min, 10f, 100f);

        if (median > target)
        {
            Current = Mathf.Max(min, Current - StepDown);
        }
        else if (median < target * 0.8f)
        {
            Current = Mathf.Min(100f, Current + StepUp);
        }
    }

    /// <summary>
    /// The frame budget, minus headroom so the scaler aims under the refresh
    /// interval rather than exactly at it.
    /// </summary>
    static float TargetFrameMs()
    {
        int refresh = Screen.currentResolution.refreshRate;
        if (refresh < 50 || refresh > 240) refresh = 60;

        float budget = 1000f / refresh;
        return budget * (1f - Mathf.Clamp01(PhxBF3.Config.DynamicResolutionHeadroom));
    }

    /// <summary>
    /// GPU time for the last frame, in milliseconds.
    /// </summary>
    /// <remarks>
    /// Wall-clock frame time is useless here: PhxResolutionManager turns vsync
    /// on, which quantises it to the refresh interval and destroys exactly the
    /// headroom signal this needs. FrameTimingManager reports what the GPU
    /// actually spent.
    ///
    /// Some driver and graphics-API combinations return no timings at all. The
    /// fallback is logged once, because a controller silently running on the
    /// wrong signal looks like a controller that works.
    /// </remarks>
    float MeasureFrameMs()
    {
        FrameTimingManager.CaptureFrameTimings();
        uint got = FrameTimingManager.GetLatestTimings(1, Timings);

        if (got > 0 && Timings[0].gpuFrameTime > 0.0)
        {
            TimingSource = "FrameTimingManager";
            return (float)Timings[0].gpuFrameTime;
        }

        if (!WarnedNoTimings)
        {
            WarnedNoTimings = true;
            Debug.LogWarning("[BFPresentation] FrameTimingManager reported no GPU timings; " +
                             "dynamic resolution is falling back to wall-clock frame time, " +
                             "which vsync quantises. Scaling will be less accurate.");
        }

        TimingSource = "wallClock";
        return Time.unscaledDeltaTime * 1000f;
    }

    static float Median(float[] buffer, int count)
    {
        // Small, fixed-size and called twice a second, so a copy and a sort is
        // cheaper than anything cleverer would be to maintain.
        var copy = new float[count];
        Array.Copy(buffer, copy, count);
        Array.Sort(copy);
        return copy[count / 2];
    }

    /// <summary>What the map actually cost, for the render budget report.</summary>
    public BFRenderMeasuredStats Snapshot()
    {
        var stats = new BFRenderMeasuredStats
        {
            sampleSeconds = Time.unscaledTime - StatsStartedAt,
            timingSource = TimingSource,
        };

        if (GpuHistory.Count > 0)
        {
            var sorted = GpuHistory.ToArray();
            Array.Sort(sorted);
            stats.gpuFrameMsMedian = sorted[sorted.Length / 2];
            stats.gpuFrameMsP95 = sorted[Mathf.Clamp((int)(sorted.Length * 0.95f), 0, sorted.Length - 1)];
        }

        if (ScaleHistory.Count > 0)
        {
            var sorted = ScaleHistory.ToArray();
            Array.Sort(sorted);
            stats.resolutionPercentMedian = sorted[sorted.Length / 2];
        }

        return stats;
    }
}
