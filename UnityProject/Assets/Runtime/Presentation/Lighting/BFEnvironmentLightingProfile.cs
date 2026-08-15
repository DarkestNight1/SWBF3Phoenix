using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Everything the presentation layer needs to make one environment feel
/// physically like itself.
/// </summary>
/// <remarks>
/// This is the anchor of Phase 1. A single global lighting preset cannot be
/// right for Hoth, Mustafar and a Star Destroyer hangar at once: what reads as
/// correct exposure on a snowfield is blown out over lava, and the fog that
/// makes Endor feel like a forest makes Tatooine look like a swamp.
///
/// A profile is *not* a replacement for the map's authored data. The world's
/// own <c>.sky</c> chunk (fog colour and range, sun angle and colour, dome
/// ambient) is parsed by the importer into <see cref="SWBFSkyProperties"/> and
/// remains the source of truth for the map's artistic intent. A profile says
/// how to *interpret* that intent with a modern renderer - how thick the
/// volumetrics are, how far contact shadows reach, what the sky actually is
/// physically - and supplies values the 2005 format never had a way to
/// express.
///
/// Profiles are code rather than assets on purpose: they need to ship with the
/// build, be diffable in review, and be resolvable from a map name at runtime
/// with no import step.
/// </remarks>
public sealed class BFEnvironmentLightingProfile
{
    public string Name = "Default";

    // ------------------------------------------------------------------ sun

    /// <summary>
    /// Sun brightness as a multiple of whatever the importer gave the map's
    /// own directional light. 1 leaves it exactly as the original fork
    /// rendered it; 0 means "do not touch the intensity at all".
    /// </summary>
    /// <remarks>
    /// Relative, not absolute, and that distinction was worth a bug. These
    /// profiles first carried physical lux values - Hoth 18000, Mustafar 4000 -
    /// against an importer that gives every directional light 400000 lux
    /// (<c>WorldLoader.ImportLights</c>). Nothing else in the scene was
    /// rescaled with them, so the sun dropped by three to a hundred times
    /// while the map's point and spot lights stayed at their imported 10000
    /// lumens and the ambient stayed where the .lgt put it. Auto-exposure
    /// reopened, local lights blew out, and everything the sun was responsible
    /// for went dark.
    ///
    /// The absolute values were not wrong physically. They were wrong as an
    /// intervention: the imported scene has no physical calibration to be
    /// absolute against, so the only safe thing a profile can say is "this
    /// place is brighter or dimmer than the reference", and the reference has
    /// to be what the map already had.
    /// </remarks>
    public float SunIntensityScale = 1f;

    /// <summary>
    /// The importer's directional intensity, for anything that needs to reason
    /// about the resulting absolute value. Not used to set anything.
    /// </summary>
    public const float ReferenceSunLux = 400000f;

    public Color SunColor = new Color(1f, 0.96f, 0.90f);

    /// <summary>Elevation and azimuth in degrees; used when the map authors none.</summary>
    public Vector2 SunAngle = new Vector2(50f, 30f);

    /// <summary>Angular diameter of the sun disc, which sets shadow softness.</summary>
    public float SunAngularDiameter = 0.5f;

    /// <summary>
    /// Whether the map's authored sun angle should win over
    /// <see cref="SunAngle"/>. True for almost everything - the level designers
    /// placed the sun for a reason - and false only where the authored value is
    /// meaningless (interiors, space).
    /// </summary>
    public bool PreferAuthoredSunAngle = true;

    // ------------------------------------------------------------------ sky

    public enum BFSkyKind
    {
        /// <summary>Physically based sky: real atmosphere, correct horizon.</summary>
        PhysicallyBased,
        /// <summary>Flat gradient, for interiors and anywhere with no visible sky.</summary>
        Gradient,
        /// <summary>Effectively black, for space.</summary>
        Space,
        /// <summary>
        /// The map paints its own sky on the dome. The procedural atmosphere
        /// stands down to a flat gradient matching the authored fog colour, so
        /// it neither shows through the dome nor lights the map for a planet
        /// the player cannot see. Chosen by BFSkydomeReconciler at load, not
        /// authored in a profile.
        /// </summary>
        AuthoredDome,
    }

    public BFSkyKind Sky = BFSkyKind.PhysicallyBased;

    /// <summary>Ground albedo the sky bounces off - snow is bright, lava is not.</summary>
    public Color PlanetaryGroundTint = new Color(0.25f, 0.24f, 0.22f);

    /// <summary>Atmospheric density multiplier. Thin air = crisp distance.</summary>
    public float AtmosphereDensity = 1f;

    /// <summary>Rayleigh tint - what colour the sky scatters toward.</summary>
    public Color AtmosphereTint = new Color(0.45f, 0.6f, 1f);

    /// <summary>Mie scattering, which is haze and light shafts around the sun.</summary>
    public float AerosolDensity = 0.012f;

    // -------------------------------------------------------- clouds

    /// <summary>
    /// Cloud cover, 0 for a clear sky.
    /// </summary>
    /// <remarks>
    /// Only consulted on <see cref="BFSkyKind.PhysicallyBased"/> maps. An
    /// interior has no sky to put clouds in, a space map has no atmosphere to
    /// hold them, and on an authored dome the artist already painted whatever
    /// weather the map is supposed to have - drawing volumetric clouds over
    /// that paints a second, disagreeing sky on top of the first.
    /// </remarks>
    [Range(0f, 1f)]
    public float CloudCoverage;

    /// <summary>Base of the cloud layer in metres above the camera.</summary>
    public float CloudAltitude = 2000f;

    /// <summary>Thickness of the cloud layer in metres.</summary>
    public float CloudThickness = 3000f;

    /// <summary>How much of the sun the clouds are allowed to take away.</summary>
    /// <remarks>
    /// Clouds shadow the world beneath them, which is correct and also the
    /// fastest way to make a map unplayably dark. Capped rather than trusted.
    /// </remarks>
    [Range(0f, 1f)]
    public float CloudShadowOpacity = 0.35f;

    // ------------------------------------------------------------ fog / vol

    /// <summary>Enable volumetric (light-scattering) fog rather than flat fog.</summary>
    public bool Volumetrics = true;

    /// <summary>
    /// Add atmosphere even where the map authored none.
    /// </summary>
    /// <remarks>
    /// Off by default, and that is the important part. Forcing fog onto every
    /// map darkened the ones that never had any: volumetric fog absorbs as
    /// well as scatters, so a clear level gained a grey veil and lost contrast
    /// for nothing. Only environments that are physically soupy - a swamp, a
    /// storm, an ash cloud - turn this on; everywhere else keeps fog exactly
    /// when the .sky chunk asked for it.
    /// </remarks>
    public bool ForceAtmosphere;

    /// <summary>How far light travels before being fully scattered, in metres.</summary>
    public float FogMeanFreePath = 400f;

    public Color FogTint = Color.white;

    /// <summary>Where fog stops thickening with altitude.</summary>
    public float FogMaximumHeight = 350f;

    /// <summary>Forward scattering, 0 isotropic. Snow and dust scatter forward.</summary>
    public float FogAnisotropy = 0.3f;

    /// <summary>Volumetric light contribution; costly, so it is per-map.</summary>
    public float VolumetricLightingMultiplier = 1f;

    /// <summary>
    /// Respect the map's authored fog range instead of <see cref="FogMeanFreePath"/>.
    /// </summary>
    public bool PreferAuthoredFog = true;

    // -------------------------------------------------------------- ambient

    /// <summary>Indirect light multiplier from the sky onto the world.</summary>
    public float AmbientIntensity = 1f;

    /// <summary>Extra bounce for maps whose bright ground should fill shadows.</summary>
    public float IndirectDiffuseIntensity = 1f;
    public float IndirectSpecularIntensity = 1f;

    // ------------------------------------------------------------- exposure

    /// <summary>Stops of exposure compensation on top of auto-exposure.</summary>
    public float ExposureCompensation;

    /// <summary>Auto-exposure clamp, in EV. Wide range = more adaptation.</summary>
    public Vector2 ExposureLimits = new Vector2(-2f, 14f);

    // -------------------------------------------------------------- shadows

    /// <summary>Distance beyond which cascaded shadows stop, in metres.</summary>
    public float ShadowDistance = 500f;

    /// <summary>
    /// Where the shadow cascades divide, as fractions of
    /// <see cref="ShadowDistance"/>.
    /// </summary>
    /// <remarks>
    /// The most valuable per-map shadow knob, and until now the only one never
    /// written: <see cref="BFLightingDirector"/> set the cascade *count* and
    /// left the splits at HDRP's defaults on every map in the game.
    ///
    /// Count decides how many shadow maps are rendered; splits decide where the
    /// resolution goes, and that is what should differ between maps. A forest
    /// floor, a canopy walkway and an interior all want the first cascade tight
    /// around the player because everything worth shadowing is within a few
    /// metres. An open snowfield or a flat desert wants it pushed out, because
    /// there is nothing close by to shadow and the detail that matters is at
    /// range.
    ///
    /// Only the first three are used - a fourth cascade always ends at
    /// ShadowDistance - and HDRP expects them ascending. Defaults here match
    /// HDRP's own so an unauthored map behaves exactly as before.
    /// </remarks>
    public Vector3 CascadeSplits = new Vector3(0.05f, 0.15f, 0.3f);

    /// <summary>
    /// Width of the blend between cascades, as a fraction of each cascade.
    /// </summary>
    /// <remarks>
    /// Wider hides the transition at the cost of rendering into two cascades
    /// across the seam. Worth more on maps with large flat ground where a hard
    /// cascade edge draws a visible line across the terrain.
    /// </remarks>
    public float CascadeBorder = 0.2f;

    public float ContactShadowLength = 0.6f;
    public float ContactShadowOpacity = 0.8f;

    /// <summary>Micro-shadowing from normal maps; strongest on rough surfaces.</summary>
    public float MicroShadowOpacity = 0.6f;

    // -------------------------------------------------- reflections and GI

    /// <summary>Screen-space GI, where the quality tier allows it.</summary>
    public bool ScreenSpaceGlobalIllumination;

    public bool ScreenSpaceReflections = true;

    /// <summary>Smoothness below which SSR stops contributing.</summary>
    /// <remarks>
    /// Must sit clear of <see cref="BFMaterialInterpreter.SmoothnessDetailBand"/>.
    /// That band varies smoothness either side of the authored value to give
    /// flat 2005 textures some relief; a cutoff inside it is crossed by the
    /// variation, so reflections switch on and off across what is physically
    /// one surface. At 0.6 - the old default - a floor authored anywhere near
    /// 0.6 reflected in patches. Above the band a surface either reflects or
    /// does not, as one surface, and only genuinely polished floors and hulls
    /// qualify.
    /// </remarks>
    public float ReflectionMinSmoothness = 0.8f;

    /// <summary>Ambient occlusion strength; greebled interiors want more.</summary>
    public float AmbientOcclusionIntensity = 1.2f;

    // ------------------------------------------------------------- weather

    /// <summary>Surface wetness this environment sits at with no rain, 0..1.</summary>
    public float BaseWetness;

    /// <summary>Snow coverage this environment sits at with no snowfall, 0..1.</summary>
    public float BaseSnowCoverage;

    /// <summary>Dominant terrain surface, used when nothing else identifies it.</summary>
    public BFSurfaceType DominantSurface = BFSurfaceType.Rock;

    // ---------------------------------------------------------------- cost

    // What a map is allowed to SPEND, as distinct from what it should look
    // like. Everything above describes an environment; everything here
    // describes its budget.
    //
    // All nullable, and null means "the tier decides" - which is what every
    // map did before these existed, so an unset field cannot change behaviour.
    // A map may only ever lower a cost below what the tier allows, never raise
    // one: the tier is a ceiling and the profile is a request. That rule is
    // enforced in BFPresentationQuality and checked in the render budget
    // report, which warns if a profile ever violates it.

    /// <summary>Caps how many point/spot lights may cast shadows here.</summary>
    public int? MaxShadowCastingPunctual;

    /// <summary>Caps reflection probes placed for this map.</summary>
    public int? MaxReflectionProbes;

    /// <summary>Caps live decals.</summary>
    public int? MaxDecals;

    /// <summary>Caps the sun's shadow map resolution.</summary>
    public int? SunShadowResolutionCap;

    /// <summary>Caps shadow cascade count.</summary>
    public int? ShadowCascadeCap;

    /// <summary>
    /// Raises the size below which a static renderer stops casting shadows.
    /// </summary>
    /// <remarks>
    /// A floor, not a cap - bigger is cheaper here, so this is combined with
    /// Max rather than Min. Interiors want it at zero, because greebles are
    /// what interiors are made of; open exteriors can discard small casters
    /// without anyone noticing.
    /// </remarks>
    public float? MinShadowCasterRadiusFloor;

    // There is deliberately no per-map punctual shadow resolution. The one
    // BFLocalLightPolicy uses is already 256, chosen because that shadow exists
    // to stop light crossing a wall rather than to resolve detail - so a map
    // could only ever raise it, which the rule forbids.

    /// <summary>
    /// Lowest fraction of native resolution dynamic scaling may fall to, 0..1.
    /// </summary>
    /// <remarks>
    /// Per map because content decides how well it upscales. Foliage and
    /// alpha-tested detail break down early; clean interior and space geometry
    /// survives much lower.
    /// </remarks>
    public float? DynamicResolutionFloor;

    // ================================================================ built-ins

    /// <summary>
    /// The stock planets, keyed by the three-letter prefix every SWBF2 mission
    /// script name starts with (geo1c_con -> "geo").
    /// </summary>
    /// <remarks>
    /// Keyed on the script prefix rather than a display name because that is
    /// what the runtime actually has: <see cref="PhxEnvironment.GetWorldName"/>
    /// returns the mission script name. Mods keep working - an unrecognised
    /// prefix falls through to <see cref="Default"/>, which is the current
    /// behaviour for every map.
    /// </remarks>
    static readonly Dictionary<string, BFEnvironmentLightingProfile> Planets =
        new Dictionary<string, BFEnvironmentLightingProfile>(System.StringComparer.OrdinalIgnoreCase)
    {
        // Hoth: a snowfield under a thin, cold, overcast sky. Very bright
        // ground, so exposure has to be pulled down or the whole frame clips;
        // heavy forward-scattering fog is what makes distance read as snow haze.
        { "hot", new BFEnvironmentLightingProfile
            {
                Name = "Hoth",
                CascadeSplits = new Vector3(0.10f, 0.28f, 0.55f),
                CascadeBorder = 0.25f,
                SunIntensityScale = 0.90f,
                SunColor = new Color(0.86f, 0.92f, 1f),
                SunAngle = new Vector2(22f, 200f),
                SunAngularDiameter = 1.2f,          // diffuse overcast light
                PlanetaryGroundTint = new Color(0.85f, 0.88f, 0.92f),
                AtmosphereTint = new Color(0.55f, 0.66f, 0.85f),
                AtmosphereDensity = 0.7f,
                AerosolDensity = 0.03f,
                FogMeanFreePath = 220f,
                FogTint = new Color(0.88f, 0.92f, 1f),
                FogAnisotropy = 0.55f,
                FogMaximumHeight = 220f,
                VolumetricLightingMultiplier = 1.4f,
                AmbientIntensity = 1.5f,
                IndirectDiffuseIntensity = 1.6f,    // snow bounce fills shadows
                ExposureCompensation = -0.35f,
                ContactShadowLength = 0.5f,
                AmbientOcclusionIntensity = 0.9f,
                BaseSnowCoverage = 1f,
                DominantSurface = BFSurfaceType.Snow,
            }
        },

        // Geonosis: thin dry air, high sun, dust everywhere. Long visibility
        // with a warm haze rather than true fog.
        { "geo", new BFEnvironmentLightingProfile
            {
                Name = "Geonosis",
                CascadeSplits = new Vector3(0.06f, 0.18f, 0.40f),
                CascadeBorder = 0.20f,
                SunIntensityScale = 1.15f,
                SunColor = new Color(1f, 0.88f, 0.72f),
                SunAngle = new Vector2(62f, 140f),
                PlanetaryGroundTint = new Color(0.45f, 0.30f, 0.20f),
                AtmosphereTint = new Color(0.62f, 0.55f, 0.45f),
                AtmosphereDensity = 0.55f,
                AerosolDensity = 0.022f,
                FogMeanFreePath = 900f,
                FogTint = new Color(1f, 0.87f, 0.70f),
                FogAnisotropy = 0.4f,
                AmbientIntensity = 1.1f,
                ExposureCompensation = -0.15f,
                ShadowDistance = 700f,
                DominantSurface = BFSurfaceType.Sand,
            }
        },

        // Endor: a forest floor under canopy. Very little direct light reaches
        // the ground, so the sun is dim and green-filtered and ambient carries
        // the scene; volumetrics make the shafts that get through visible.
        { "end", new BFEnvironmentLightingProfile
            {
                Name = "Endor",
                CascadeSplits = new Vector3(0.03f, 0.09f, 0.22f),
                CascadeBorder = 0.15f,
                ForceAtmosphere = true,
                SunIntensityScale = 0.70f,
                SunColor = new Color(0.92f, 1f, 0.80f),
                SunAngle = new Vector2(58f, 60f),
                PlanetaryGroundTint = new Color(0.18f, 0.22f, 0.12f),
                AtmosphereTint = new Color(0.42f, 0.58f, 0.42f),
                AerosolDensity = 0.035f,
                FogMeanFreePath = 160f,
                FogTint = new Color(0.75f, 0.85f, 0.70f),
                FogAnisotropy = 0.5f,
                FogMaximumHeight = 120f,
                VolumetricLightingMultiplier = 1.8f,   // god rays through canopy
                AmbientIntensity = 1.2f,
                ExposureCompensation = 0.25f,
                AmbientOcclusionIntensity = 1.5f,
                BaseWetness = 0.25f,
                DominantSurface = BFSurfaceType.Grass,
            }
        },

        // Tatooine: twin suns, blinding, almost no atmosphere to scatter.
        { "tat", new BFEnvironmentLightingProfile
            {
                Name = "Tatooine",
                CascadeSplits = new Vector3(0.12f, 0.32f, 0.60f),
                CascadeBorder = 0.30f,
                SunIntensityScale = 1.25f,
                SunColor = new Color(1f, 0.94f, 0.80f),
                SunAngle = new Vector2(70f, 20f),
                SunAngularDiameter = 0.35f,            // hard-edged shadows
                PlanetaryGroundTint = new Color(0.62f, 0.50f, 0.34f),
                AtmosphereTint = new Color(0.68f, 0.62f, 0.48f),
                AtmosphereDensity = 0.4f,
                AerosolDensity = 0.01f,
                FogMeanFreePath = 1600f,
                FogTint = new Color(1f, 0.93f, 0.78f),
                AmbientIntensity = 1.3f,
                IndirectDiffuseIntensity = 1.4f,       // sand bounce
                ExposureCompensation = -0.30f,
                ShadowDistance = 800f,
                DominantSurface = BFSurfaceType.Sand,
            }
        },

        // Mustafar: the light source is the ground. Sun is nearly irrelevant;
        // ambient and emissive lava carry it, and the air is thick with ash.
        { "mus", new BFEnvironmentLightingProfile
            {
                Name = "Mustafar",
                CascadeSplits = new Vector3(0.05f, 0.15f, 0.34f),
                CascadeBorder = 0.20f,
                ForceAtmosphere = true,
                SunIntensityScale = 0.50f,
                SunColor = new Color(1f, 0.55f, 0.30f),
                SunAngle = new Vector2(12f, 300f),
                SunAngularDiameter = 2f,
                PlanetaryGroundTint = new Color(0.35f, 0.10f, 0.04f),
                AtmosphereTint = new Color(0.55f, 0.22f, 0.10f),
                AtmosphereDensity = 1.6f,
                AerosolDensity = 0.08f,
                FogMeanFreePath = 130f,
                FogTint = new Color(0.85f, 0.35f, 0.15f),
                FogAnisotropy = 0.25f,
                FogMaximumHeight = 500f,
                VolumetricLightingMultiplier = 2f,
                AmbientIntensity = 0.7f,
                ExposureCompensation = 0.15f,
                ShadowDistance = 300f,
                AmbientOcclusionIntensity = 1.4f,
                DominantSurface = BFSurfaceType.Lava,
            }
        },

        // Kamino: permanent storm over open ocean. Everything is wet, the sky
        // is a flat bright overcast, and there are no hard shadows at all.
        { "kam", new BFEnvironmentLightingProfile
            {
                Name = "Kamino",
                CascadeSplits = new Vector3(0.06f, 0.16f, 0.36f),
                CascadeBorder = 0.22f,
                ForceAtmosphere = true,
                SunIntensityScale = 0.70f,
                SunColor = new Color(0.88f, 0.93f, 1f),
                SunAngle = new Vector2(35f, 250f),
                SunAngularDiameter = 3f,               // no direct sun at all
                PlanetaryGroundTint = new Color(0.30f, 0.35f, 0.40f),
                AtmosphereTint = new Color(0.5f, 0.58f, 0.68f),
                AtmosphereDensity = 1.3f,
                AerosolDensity = 0.05f,
                FogMeanFreePath = 180f,
                FogTint = new Color(0.78f, 0.84f, 0.90f),
                FogMaximumHeight = 600f,
                VolumetricLightingMultiplier = 1.6f,
                AmbientIntensity = 1.6f,
                ExposureCompensation = -0.10f,
                ContactShadowLength = 0.35f,
                ReflectionMinSmoothness = 0.35f,       // wet everything reflects
                BaseWetness = 1f,
                DominantSurface = BFSurfaceType.Metal,
            }
        },

        // Coruscant: a city floor in permanent shade between towers, lit by a
        // hazy sky and a great deal of artificial light.
        { "cor", new BFEnvironmentLightingProfile
            {
                // The stock cor1 is the RUINS of the Jedi Temple after Order
                // 66 - a dark stone interior lit by shafts through tall
                // windows, not the daylit city the name suggests. The
                // community's "Jedi Temple Daytime" mods exist precisely
                // because the shipped map is dark, so brightening it here
                // would be undoing the map's own art direction rather than
                // modernising it.
                //
                // Treated accordingly: the sun contributes through windows
                // rather than lighting the scene, ambient and the map's own
                // fixtures carry it, and the surfaces are stone.
                Name = "Coruscant",
                CascadeSplits = new Vector3(0.04f, 0.12f, 0.28f),
                CascadeBorder = 0.15f,
                Sky = BFSkyKind.Gradient,
                SunIntensityScale = 0.45f,
                SunColor = new Color(1f, 0.93f, 0.82f),
                SunAngle = new Vector2(48f, 110f),
                SunAngularDiameter = 1.5f,
                PlanetaryGroundTint = new Color(0.26f, 0.24f, 0.22f),
                AtmosphereTint = new Color(0.42f, 0.46f, 0.55f),
                AerosolDensity = 0.028f,
                ForceAtmosphere = true,
                FogMeanFreePath = 220f,
                FogTint = new Color(0.72f, 0.74f, 0.80f),
                FogMaximumHeight = 60f,
                VolumetricLightingMultiplier = 1.6f,   // shafts through windows
                AmbientIntensity = 1.35f,
                IndirectDiffuseIntensity = 1.3f,
                ExposureCompensation = 0.25f,
                ShadowDistance = 180f,
                ScreenSpaceGlobalIllumination = true,
                ReflectionMinSmoothness = 0.6f,        // stone, not polished metal
                AmbientOcclusionIntensity = 1.5f,
                DominantSurface = BFSurfaceType.Rock,
            }
        },

        // Naboo: clear temperate daylight over grass and water.
        { "nab", new BFEnvironmentLightingProfile
            {
                Name = "Naboo",
                CascadeSplits = new Vector3(0.10f, 0.26f, 0.52f),
                CascadeBorder = 0.28f,
                SunIntensityScale = 1.10f,
                SunColor = new Color(1f, 0.97f, 0.92f),
                SunAngle = new Vector2(55f, 75f),
                PlanetaryGroundTint = new Color(0.24f, 0.30f, 0.18f),
                AtmosphereTint = new Color(0.45f, 0.62f, 1f),
                FogMeanFreePath = 800f,
                AmbientIntensity = 1.1f,
                BaseWetness = 0.1f,
                DominantSurface = BFSurfaceType.Grass,
            }
        },

        // Kashyyyk: humid, wet, heavy canopy over water.
        { "kas", new BFEnvironmentLightingProfile
            {
                Name = "Kashyyyk",
                CascadeSplits = new Vector3(0.04f, 0.11f, 0.26f),
                CascadeBorder = 0.18f,
                ForceAtmosphere = true,
                SunIntensityScale = 0.85f,
                SunColor = new Color(1f, 0.97f, 0.85f),
                SunAngle = new Vector2(52f, 95f),
                PlanetaryGroundTint = new Color(0.22f, 0.26f, 0.16f),
                AtmosphereTint = new Color(0.48f, 0.62f, 0.75f),
                AerosolDensity = 0.04f,
                FogMeanFreePath = 260f,
                FogTint = new Color(0.80f, 0.88f, 0.85f),
                VolumetricLightingMultiplier = 1.5f,
                AmbientIntensity = 1.2f,
                BaseWetness = 0.5f,
                DominantSurface = BFSurfaceType.Grass,
            }
        },

        // Felucia: dense, humid, colour-saturated undergrowth.
        { "fel", new BFEnvironmentLightingProfile
            {
                Name = "Felucia",
                CascadeSplits = new Vector3(0.03f, 0.09f, 0.22f),
                CascadeBorder = 0.15f,
                ForceAtmosphere = true,
                SunIntensityScale = 0.75f,
                SunColor = new Color(1f, 0.90f, 0.95f),
                SunAngle = new Vector2(60f, 140f),
                PlanetaryGroundTint = new Color(0.30f, 0.20f, 0.28f),
                AtmosphereTint = new Color(0.6f, 0.45f, 0.6f),
                AerosolDensity = 0.05f,
                FogMeanFreePath = 140f,
                FogTint = new Color(0.85f, 0.72f, 0.85f),
                VolumetricLightingMultiplier = 1.9f,
                AmbientIntensity = 1.3f,
                BaseWetness = 0.4f,
                DominantSurface = BFSurfaceType.Grass,
            }
        },

        // Mygeeto: crystalline city under a cold overcast; bright like Hoth
        // but hard-edged rather than soft.
        { "myg", new BFEnvironmentLightingProfile
            {
                Name = "Mygeeto",
                CascadeSplits = new Vector3(0.05f, 0.15f, 0.34f),
                CascadeBorder = 0.22f,
                SunIntensityScale = 0.90f,
                SunColor = new Color(0.90f, 0.94f, 1f),
                SunAngle = new Vector2(30f, 210f),
                PlanetaryGroundTint = new Color(0.70f, 0.74f, 0.80f),
                AtmosphereTint = new Color(0.55f, 0.65f, 0.80f),
                FogMeanFreePath = 300f,
                FogTint = new Color(0.86f, 0.90f, 0.96f),
                AmbientIntensity = 1.4f,
                IndirectDiffuseIntensity = 1.4f,
                ExposureCompensation = -0.30f,
                ReflectionMinSmoothness = 0.5f,
                BaseSnowCoverage = 0.6f,
                DominantSurface = BFSurfaceType.Concrete,
            }
        },

        // Utapau: deep sinkhole, rock everywhere, light from directly above.
        { "uta", new BFEnvironmentLightingProfile
            {
                Name = "Utapau",
                CascadeSplits = new Vector3(0.05f, 0.14f, 0.30f),
                CascadeBorder = 0.20f,
                SunIntensityScale = 1.00f,
                SunColor = new Color(1f, 0.94f, 0.85f),
                SunAngle = new Vector2(78f, 90f),
                PlanetaryGroundTint = new Color(0.42f, 0.36f, 0.30f),
                AtmosphereTint = new Color(0.55f, 0.58f, 0.62f),
                FogMeanFreePath = 450f,
                AmbientIntensity = 0.9f,
                AmbientOcclusionIntensity = 1.6f,      // deep shafts
                ShadowDistance = 600f,
                DominantSurface = BFSurfaceType.Rock,
            }
        },

        // Yavin: warm jungle daylight.
        { "yav", new BFEnvironmentLightingProfile
            {
                Name = "Yavin 4",
                CascadeSplits = new Vector3(0.04f, 0.12f, 0.28f),
                CascadeBorder = 0.18f,
                SunIntensityScale = 1.05f,
                SunColor = new Color(1f, 0.95f, 0.82f),
                SunAngle = new Vector2(50f, 45f),
                PlanetaryGroundTint = new Color(0.22f, 0.28f, 0.16f),
                AerosolDensity = 0.03f,
                FogMeanFreePath = 350f,
                AmbientIntensity = 1.15f,
                BaseWetness = 0.2f,
                DominantSurface = BFSurfaceType.Grass,
            }
        },

        // Dagobah: swamp, permanent mist, almost no direct light.
        { "dag", new BFEnvironmentLightingProfile
            {
                Name = "Dagobah",
                CascadeSplits = new Vector3(0.05f, 0.14f, 0.32f),
                CascadeBorder = 0.15f,
                ForceAtmosphere = true,
                SunIntensityScale = 0.55f,
                SunColor = new Color(0.88f, 0.95f, 0.85f),
                SunAngle = new Vector2(40f, 160f),
                SunAngularDiameter = 2.5f,
                PlanetaryGroundTint = new Color(0.16f, 0.18f, 0.13f),
                AtmosphereTint = new Color(0.4f, 0.48f, 0.42f),
                AtmosphereDensity = 1.4f,
                AerosolDensity = 0.07f,
                FogMeanFreePath = 90f,
                FogTint = new Color(0.68f, 0.75f, 0.68f),
                FogMaximumHeight = 90f,
                VolumetricLightingMultiplier = 2f,
                AmbientIntensity = 1.3f,
                ExposureCompensation = 0.35f,
                ShadowDistance = 200f,
                BaseWetness = 0.9f,
                DominantSurface = BFSurfaceType.Mud,
            }
        },

        // Rhen Var. The one stock planet that had no profile at all and fell
        // through to Default - a lighting-only omission, since the terrain
        // classifier already knew it as rock.
        //
        // Measured: terrain runs -66.5 to 16.6 m, and the baked lighting has
        // real contrast (mean luma 0.54, the second highest of any map). Stone
        // ruins under a cold overcast sky, so the sun is weak and blue-shifted
        // and the ambient carries more of the scene than usual. Snow-covered
        // ground, but nothing like Hoth's whiteout - the interest here is the
        // architecture, so the cascades sit between Hoth's far bias and a
        // forest's near one.
        { "rhn", new BFEnvironmentLightingProfile
            {
                Name = "Rhen Var",
                CascadeSplits = new Vector3(0.07f, 0.20f, 0.42f),
                CascadeBorder = 0.22f,
                SunIntensityScale = 0.8f,
                SunColor = new Color(0.88f, 0.92f, 1f),
                SunAngularDiameter = 1.6f,
                PlanetaryGroundTint = new Color(0.55f, 0.57f, 0.62f),
                AtmosphereTint = new Color(0.5f, 0.62f, 0.95f),
                AmbientIntensity = 1.35f,
                FogMeanFreePath = 320f,
                FogTint = new Color(0.82f, 0.87f, 0.95f),
                ShadowDistance = 450f,
                BaseSnowCoverage = 0.7f,
                DominantSurface = BFSurfaceType.Snow,
            }
        },

        // Death Star / Polis Massa / Tantive: interiors. No sky, no sun worth
        // the name, everything lit by fixtures the map already places.
        { "dea", Interior("Death Star") },
        { "pol", Interior("Polis Massa") },
        { "tan", Interior("Tantive IV") },

        // Space maps: no atmosphere at all, extreme contrast.
        { "spa", Space("Space") },
    };

    static BFEnvironmentLightingProfile Interior(string name) => new BFEnvironmentLightingProfile
    {
        Name = name,
        Sky = BFSkyKind.Gradient,
        ForceAtmosphere = true,
        SunIntensityScale = 0f,
        PreferAuthoredSunAngle = false,
        PreferAuthoredFog = false,
        Volumetrics = true,
        FogMeanFreePath = 120f,
        FogTint = new Color(0.7f, 0.72f, 0.78f),
        FogMaximumHeight = 60f,
        VolumetricLightingMultiplier = 1.3f,
        AmbientIntensity = 0.5f,
        ExposureCompensation = 0.2f,
        ShadowDistance = 120f,

        // Interiors: everything worth shadowing is within a corridor's width,
        // so the near cascade is tight and the far one barely matters.
        CascadeSplits = new Vector3(0.06f, 0.18f, 0.42f),
        CascadeBorder = 0.12f,
        ContactShadowLength = 0.8f,           // interiors live on contact detail
        ScreenSpaceGlobalIllumination = true,
        ReflectionMinSmoothness = 0.4f,
        AmbientOcclusionIntensity = 1.7f,
        DominantSurface = BFSurfaceType.Metal,

        // Punctual shadows are deliberately NOT capped here. An interior has
        // no sun, so its own fixtures are the lighting, and their shadows are
        // the thing worth paying for. The tier's budget is the right number.
        //
        // What an interior can give back is everything to do with the sun it
        // does not have, and the distance it does not see.
        SunShadowResolutionCap = 1024,
        ShadowCascadeCap = 2,

        // Was 0.70 to leave SSGI some headroom. Raised to match the config
        // floor after softness was reported on exactly these maps: an interior
        // is where the player is closest to walls and to their own character,
        // so it is the worst place in the game to be upscaling from 70%.
        // SSGI gives its headroom back through its own quality setting instead.
        DynamicResolutionFloor = 0.80f,
    };

    static BFEnvironmentLightingProfile Space(string name) => new BFEnvironmentLightingProfile
    {
        Name = name,
        Sky = BFSkyKind.Space,
        SunIntensityScale = 1f,
        SunColor = Color.white,
        SunAngularDiameter = 0.2f,            // pinpoint star, razor shadows
        PreferAuthoredSunAngle = false,
        PreferAuthoredFog = false,
        Volumetrics = false,                  // vacuum does not scatter
        FogMeanFreePath = 100000f,
        AmbientIntensity = 0.15f,
        IndirectDiffuseIntensity = 0.3f,
        ExposureCompensation = -0.20f,
        ExposureLimits = new Vector2(-4f, 16f),
        ShadowDistance = 900f,

        // Space: a capital ship hull is enormous and entirely far-field, and
        // there is no ground plane to catch a near shadow at all.
        CascadeSplits = new Vector3(0.15f, 0.38f, 0.66f),
        CascadeBorder = 0.30f,
        ContactShadowLength = 0.5f,
        AmbientOcclusionIntensity = 1.1f,
        DominantSurface = BFSurfaceType.Metal,

        // Vacuum: nothing to bounce off, nothing nearby to shadow, and very
        // few surfaces for a probe to capture. Almost all of the screen-space
        // and shadow budget is wasted here, so it is given back.
        MaxShadowCastingPunctual = 0,
        MaxReflectionProbes = 2,
        MinShadowCasterRadiusFloor = 1.5f,

        // Hard-edged hulls against black upscale better than anything else in
        // the game.
        DynamicResolutionFloor = 0.55f,
    };

    /// <summary>Used by any map with no profile of its own.</summary>
    public static readonly BFEnvironmentLightingProfile Default = new BFEnvironmentLightingProfile();

    /// <summary>
    /// Profile for a mission script name (e.g. "hot1c_con"), or
    /// <see cref="Default"/>. Never null.
    /// </summary>
    public static BFEnvironmentLightingProfile Resolve(string worldName)
    {
        if (string.IsNullOrEmpty(worldName) || worldName.Length < 3) return Default;

        return Planets.TryGetValue(worldName.Substring(0, 3), out BFEnvironmentLightingProfile profile)
            ? profile
            : Default;
    }

    /// <summary>Register or replace a profile, for mods and for testing.</summary>
    public static void Register(string scriptPrefix, BFEnvironmentLightingProfile profile)
    {
        if (string.IsNullOrEmpty(scriptPrefix) || profile == null) return;
        Planets[scriptPrefix] = profile;
    }
}
