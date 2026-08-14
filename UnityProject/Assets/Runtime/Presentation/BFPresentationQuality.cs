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

    /// <summary>
    /// Screen-space global illumination.
    /// </summary>
    /// <remarks>
    /// High rather than Ultra. Every interior profile and Coruscant ask for
    /// SSGI, but the config default is High, so at an Ultra threshold the only
    /// maps written to use it could never reach it - and the pipeline asset had
    /// SSGI unsupported anyway, so nothing noticed.
    ///
    /// The threshold moved rather than letting those profiles promote
    /// themselves past the tier: the tier is a ceiling, and a map that could
    /// raise one would break the rule the whole per-map design rests on.
    ///
    /// Cost is bounded by the per-map quality level rather than by the gate -
    /// interiors run it at Low, which is half resolution.
    /// </remarks>
    public static bool ScreenSpaceGlobalIllumination => tier >= BFQualityTier.High;

    /// <summary>
    /// PCSS blocker search and filter sample counts for the sun.
    /// </summary>
    /// <remarks>
    /// The pipeline filters shadows with PCSS, whose penumbra width comes from
    /// the light's angular diameter - which every planet profile already
    /// authors, and which did nothing at all under the previous PCF filtering.
    /// Kamino's 3 degree sun and Tatooine's 0.35 degree one now differ in
    /// shadow softness the way they always described.
    ///
    /// Sample counts are the cost dial: penumbra width is the map's statement
    /// and must not be touched to buy frame time, since it also drives the
    /// light's maximum smoothness and would change specular highlights as a
    /// side effect.
    /// </remarks>
    public static int SunShadowBlockerSamples
    {
        get
        {
            switch (tier)
            {
                case BFQualityTier.Low: return 8;
                case BFQualityTier.Medium: return 8;
                case BFQualityTier.Ultra: return 24;
                default: return 16;
            }
        }
    }

    public static int SunShadowFilterSamples
    {
        get
        {
            switch (tier)
            {
                case BFQualityTier.Low: return 8;
                case BFQualityTier.Medium: return 16;
                case BFQualityTier.Ultra: return 32;
                default: return 24;
            }
        }
    }
    public static bool Decals => tier >= BFQualityTier.Low;
    public static bool ImpactLights => tier >= BFQualityTier.Medium;
    public static bool TerrainDeformation => tier >= BFQualityTier.Medium;
    public static bool PlanarWaterReflections => tier >= BFQualityTier.High;

    /// <summary>
    /// Whether stock textures get derived normal/occlusion maps.
    /// </summary>
    /// <remarks>
    /// Has its own switch rather than riding the tier alone. Its cost is paid
    /// at load, not per frame, so it does not trade off against the other
    /// features the way the rest of the tier does - and it is the only part of
    /// the layer that changes how the original art reads, which is a judgement
    /// a player may reasonably want to make separately from performance.
    /// </remarks>
    public static bool DerivedMaterialMaps =>
        tier >= BFQualityTier.Medium && PhxBF3.Config.DerivedMaterialMaps;

    // --------------------------------------------------------------- budgets

    /// <summary>Live decals. Oldest is recycled when the budget is spent.</summary>
    public static int DecalBudget
    {
        get
        {
            switch (tier)
            {
                // Each live decal is an HDRP projector in the decal atlas and
                // in the per-pixel decal loop, so the budget is a real render
                // cost rather than just memory.
                case BFQualityTier.Low: return 0;
                case BFQualityTier.Medium: return 64;
                case BFQualityTier.High: return 128;
                default: return 320;
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

    /// <summary>
    /// Shadow map side length for the one shadow-casting directional light.
    /// </summary>
    /// <remarks>
    /// Applied to the sun only. The previous behaviour raised every
    /// directional in the scene to 4096, which on a map with more than one
    /// directional meant several full-resolution cascade atlases rendered and
    /// then discarded, because HDRP only ever uses one.
    /// </remarks>
    public static int SunShadowResolution
    {
        get
        {
            switch (tier)
            {
                case BFQualityTier.Low: return 1024;
                case BFQualityTier.Medium: return 2048;
                case BFQualityTier.High: return 2048;
                default: return 4096;
            }
        }
    }

    /// <summary>
    /// How many point/spot lights may cast shadows at once.
    /// </summary>
    /// <remarks>
    /// The most expensive number in this file. A shadowed point light is six
    /// shadow renders, each walking every shadow caster in the scene - so six
    /// such lights on a map with 273 casters is over sixteen hundred draw
    /// submissions per frame for shadows alone, on a scene whose actual
    /// geometry is 130k triangles. The budget goes to the nearest lights that
    /// asked for it; the rest keep their light and lose their shadow.
    /// </remarks>
    public static int ShadowCastingPunctualBudget
    {
        get
        {
            switch (tier)
            {
                case BFQualityTier.Low: return 0;
                case BFQualityTier.Medium: return 1;
                case BFQualityTier.High: return 2;
                default: return 4;
            }
        }
    }

    /// <summary>
    /// Bounding-sphere radius below which a renderer stops casting shadows.
    /// </summary>
    /// <remarks>
    /// Measured: 273 of 322 renderers on Coruscant cast shadows, and a SWBF2
    /// model is split one renderer per bone - so most of those are small
    /// fittings whose shadow is a few pixels, re-rendered per cascade and per
    /// shadowed light. Dropping them costs almost nothing visible and removes
    /// them from every shadow pass at once.
    /// </remarks>
    public static float MinShadowCasterRadius
    {
        get
        {
            switch (tier)
            {
                case BFQualityTier.Low: return 2.5f;
                case BFQualityTier.Medium: return 1.5f;
                case BFQualityTier.High: return 0.8f;
                default: return 0f;
            }
        }
    }

    /// <summary>
    /// Cascade splits for directional shadows. Each cascade is another render
    /// of everything inside it.
    /// </summary>
    public static int ShadowCascades
    {
        get
        {
            switch (tier)
            {
                case BFQualityTier.Low: return 2;
                case BFQualityTier.Medium: return 2;
                case BFQualityTier.High: return 3;
                default: return 4;
            }
        }
    }

    /// <summary>
    /// Multiplier on the profile's shadow distance.
    /// </summary>
    /// <remarks>
    /// Shadow distance is the single most expensive number in the renderer on
    /// maps this size: everything inside it is drawn again per cascade. The
    /// profile says how far shadows matter artistically; this says how much of
    /// that the machine can afford.
    /// </remarks>
    public static float ShadowDistanceScale
    {
        get
        {
            switch (tier)
            {
                case BFQualityTier.Low: return 0.25f;
                case BFQualityTier.Medium: return 0.5f;
                case BFQualityTier.High: return 0.75f;
                default: return 1f;
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
