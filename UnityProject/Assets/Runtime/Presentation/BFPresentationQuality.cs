using UnityEngine;

/// <summary>
/// One quality tier for the whole presentation layer, and the budgets that
/// follow from it.
/// </summary>
/// <remarks>
/// Everything in Phase 1 is designed around a hard fact: these are large
/// Battlefront maps with sixty-four units, vehicles, and hundreds of
/// projectiles in the air. Any effect that allocates a GameObject, a light or
/// a render-target write per event will fall over on a full server, and it
/// will do so exactly when the game is most worth watching.
///
/// So every subsystem here takes its limits from one place, and every limit is
/// a count rather than a hope: N decals, N impact lights, N interactions per
/// frame. When the budget is spent, the cheapest thing is dropped rather than
/// the frame rate.
/// </remarks>
public enum BFQualityTier
{
    /// <summary>Everything off but the lighting profile. Runs anywhere.</summary>
    Low = 0,
    Medium = 1,
    High = 2,
    Ultra = 3,
}

public static class BFPresentationQuality
{
    static BFQualityTier tier = BFQualityTier.High;

    public static BFQualityTier Tier
    {
        get => tier;
        set
        {
            if (tier == value) return;
            tier = value;
            Debug.Log($"[BFPresentation] Quality tier set to {tier}.");
        }
    }

    // ---------------------------------------------------------- feature gates

    public static bool Volumetrics => tier >= BFQualityTier.Medium;
    public static bool ContactShadows => tier >= BFQualityTier.Medium;
    public static bool ScreenSpaceReflections => tier >= BFQualityTier.High;
    public static bool ScreenSpaceGlobalIllumination => tier >= BFQualityTier.Ultra;
    public static bool Decals => tier >= BFQualityTier.Low;
    public static bool ImpactLights => tier >= BFQualityTier.Medium;
    public static bool TerrainDeformation => tier >= BFQualityTier.Medium;
    public static bool PlanarWaterReflections => tier >= BFQualityTier.High;
    public static bool DerivedMaterialMaps => tier >= BFQualityTier.Medium;

    // --------------------------------------------------------------- budgets

    /// <summary>Live decals. Oldest is recycled when the budget is spent.</summary>
    public static int DecalBudget
    {
        get
        {
            switch (tier)
            {
                case BFQualityTier.Low: return 0;
                case BFQualityTier.Medium: return 96;
                case BFQualityTier.High: return 256;
                default: return 512;
            }
        }
    }

    /// <summary>
    /// Simultaneous impact lights. Deliberately small: these exist to make a
    /// hit read for a tenth of a second, and every one is a real shadowless
    /// punctual light in the HDRP light loop.
    /// </summary>
    public static int ImpactLightBudget
    {
        get
        {
            switch (tier)
            {
                case BFQualityTier.Low: return 0;
                case BFQualityTier.Medium: return 8;
                case BFQualityTier.High: return 16;
                default: return 32;
            }
        }
    }

    /// <summary>Side length of the terrain deformation render texture.</summary>
    public static int TerrainDeformationResolution
    {
        get
        {
            switch (tier)
            {
                case BFQualityTier.Low: return 0;
                case BFQualityTier.Medium: return 512;
                case BFQualityTier.High: return 1024;
                default: return 2048;
            }
        }
    }

    /// <summary>Reflection probes the presentation layer places per map.</summary>
    public static int ReflectionProbeBudget
    {
        get
        {
            switch (tier)
            {
                case BFQualityTier.Low: return 0;
                case BFQualityTier.Medium: return 4;
                case BFQualityTier.High: return 10;
                default: return 20;
            }
        }
    }

    /// <summary>How far interactions are processed from the camera.</summary>
    public static float InteractionDistance
    {
        get
        {
            switch (tier)
            {
                case BFQualityTier.Low: return 40f;
                case BFQualityTier.Medium: return 90f;
                case BFQualityTier.High: return 140f;
                default: return 220f;
            }
        }
    }

    /// <summary>Push the current tier's derived limits into the systems that use them.</summary>
    public static void Apply()
    {
        BFSurfaceInteractionSystem.MaxInteractionDistance = InteractionDistance;
    }
}
