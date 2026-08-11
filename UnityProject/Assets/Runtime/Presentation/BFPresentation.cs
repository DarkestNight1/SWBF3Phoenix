using UnityEngine;

/// <summary>
/// The presentation layer's single entry point.
/// </summary>
/// <remarks>
/// Everything in Phase 1 hangs off one host object created here, for the same
/// reason the BF3 Legacy systems do: these are process-wide services that must
/// outlive any individual map, and a map load must be able to rebuild them
/// without hunting them down in a scene.
///
/// The layering it establishes is the whole architecture:
///
/// <code>
/// original files -> libswbf2 -> source data -> presentation -> HDRP
/// </code>
///
/// Nothing here reaches back up that chain. The presentation layer reads the
/// source database and the imported scene, and writes only to renderer state -
/// materials it created, volumes it owns, render textures it allocated. No
/// gameplay value, no odf property and no source record is modified to make
/// something look better.
/// </remarks>
public static class BFPresentation
{
    static GameObject Host;

    public static bool IsActive => Host != null;

    /// <summary>
    /// Stand the layer up. Idempotent - a second call is a no-op, which
    /// matters because the bootstrap can be reached from several entry points.
    /// </summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    public static void Bootstrap()
    {
        if (Host != null) return;

        // Off entirely unless the fork's modern rendering is on. A user who
        // has turned that off wants the original presentation, and that has to
        // include none of this.
        if (!PhxBF3.Config.ModernLighting) return;

        Host = new GameObject("BFPresentation");
        Object.DontDestroyOnLoad(Host);

        BFPresentationQuality.Tier = (BFQualityTier)Mathf.Clamp(
            PhxBF3.Config.PresentationQuality, (int)BFQualityTier.Low, (int)BFQualityTier.Ultra);
        BFPresentationQuality.Apply();

        // Order matters only in that the lighting director resolves the map
        // profile that the weather systems read their baselines from, and it
        // does that on OnMapLoaded rather than here - so component order here
        // is just grouping.
        Host.AddComponent<BFLightingDirector>();
        Host.AddComponent<BFLightBudget>();
        Host.AddComponent<BFReflectionProbeManager>();
        Host.AddComponent<BFLightProbeManager>();
        Host.AddComponent<BFImpactLightPool>();
        Host.AddComponent<BFDecalSystem>();
        Host.AddComponent<BFTerrainInteractionSystem>();
        Host.AddComponent<BFWaterSystem>();
        Host.AddComponent<BFWetnessSystem>();
        Host.AddComponent<BFSnowAccumulation>();
        Host.AddComponent<BFPresentationMapHook>();
        Host.AddComponent<BFRenderBudgetReport>();

        Debug.Log($"[BFPresentation] Active at quality tier {BFPresentationQuality.Tier}.");
    }

    /// <summary>Tear down state that belongs to the previous map.</summary>
    public static void ResetForMapChange()
    {
        BFSurfaceQuery.Reset();
        BFSurfaceInteractionSystem.Reset();
        BFTerrainSurfaceMap.Reset();
        BFWorldQuery.Reset();
        BFDecalSystem.Clear();
        BFImpactLightPool.Clear();
        BFMaterialEnhancer.Reset();
    }
}

/// <summary>
/// Hooks the presentation layer to the map lifecycle and keeps the
/// interaction system's bookkeeping trimmed.
/// </summary>
/// <remarks>
/// Separate from the static bootstrap because it needs frames: per-map state
/// has to be dropped as a new map comes up, and the interaction system's
/// cooldown table needs occasional pruning that nothing else has a reason to
/// drive.
///
/// The per-map reset is NOT driven from here. It is called at the top of
/// <see cref="PhxScene.Import"/>, because parts of what it clears - the
/// terrain surface map above all - are rebuilt *during* import, well before
/// any load-completed event fires. Clearing on OnMapLoaded would wipe the data
/// the import had just produced.
/// </remarks>
public sealed class BFPresentationMapHook : MonoBehaviour
{
    float PruneTimer;

    void Update()
    {
        PruneTimer -= Time.deltaTime;
        if (PruneTimer > 0f) return;

        PruneTimer = 5f;
        BFSurfaceInteractionSystem.Prune();
    }
}
