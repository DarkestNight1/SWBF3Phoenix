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

    /// <summary>Sun intensity in lux. Overcast ~10k, clear midday ~100k.</summary>
    public float SunIntensity = 32000f;

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

    // ------------------------------------------------------------ fog / vol

    /// <summary>Enable volumetric (light-scattering) fog rather than flat fog.</summary>
    public bool Volumetrics = true;

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

    public float ContactShadowLength = 0.6f;
    public float ContactShadowOpacity = 0.8f;

    /// <summary>Micro-shadowing from normal maps; strongest on rough surfaces.</summary>
    public float MicroShadowOpacity = 0.6f;

    // -------------------------------------------------- reflections and GI

    /// <summary>Screen-space GI, where the quality tier allows it.</summary>
    public bool ScreenSpaceGlobalIllumination;

    public bool ScreenSpaceReflections = true;

    /// <summary>Smoothness below which SSR stops contributing.</summary>
    public float ReflectionMinSmoothness = 0.6f;

    /// <summary>Ambient occlusion strength; greebled interiors want more.</summary>
    public float AmbientOcclusionIntensity = 1.2f;

    // ------------------------------------------------------------- weather

    /// <summary>Surface wetness this environment sits at with no rain, 0..1.</summary>
    public float BaseWetness;

    /// <summary>Snow coverage this environment sits at with no snowfall, 0..1.</summary>
    public float BaseSnowCoverage;

    /// <summary>Dominant terrain surface, used when nothing else identifies it.</summary>
    public BFSurfaceType DominantSurface = BFSurfaceType.Rock;

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
                SunIntensity = 18000f,
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
                ExposureCompensation = -1.1f,
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
                SunIntensity = 90000f,
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
                ExposureCompensation = -0.4f,
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
                SunIntensity = 12000f,
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
                ExposureCompensation = 0.5f,
                AmbientOcclusionIntensity = 1.5f,
                BaseWetness = 0.25f,
                DominantSurface = BFSurfaceType.Grass,
            }
        },

        // Tatooine: twin suns, blinding, almost no atmosphere to scatter.
        { "tat", new BFEnvironmentLightingProfile
            {
                Name = "Tatooine",
                SunIntensity = 115000f,
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
                ExposureCompensation = -0.8f,
                ShadowDistance = 800f,
                DominantSurface = BFSurfaceType.Sand,
            }
        },

        // Mustafar: the light source is the ground. Sun is nearly irrelevant;
        // ambient and emissive lava carry it, and the air is thick with ash.
        { "mus", new BFEnvironmentLightingProfile
            {
                Name = "Mustafar",
                SunIntensity = 4000f,
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
                ExposureCompensation = 0.3f,
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
                SunIntensity = 9000f,
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
                ExposureCompensation = -0.3f,
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
                Name = "Coruscant",
                SunIntensity = 26000f,
                SunColor = new Color(1f, 0.95f, 0.88f),
                SunAngle = new Vector2(48f, 110f),
                PlanetaryGroundTint = new Color(0.32f, 0.32f, 0.34f),
                AtmosphereTint = new Color(0.5f, 0.58f, 0.72f),
                AerosolDensity = 0.028f,
                FogMeanFreePath = 500f,
                FogTint = new Color(0.82f, 0.86f, 0.94f),
                FogMaximumHeight = 800f,
                AmbientIntensity = 1.2f,
                ExposureCompensation = -0.2f,
                ShadowDistance = 600f,
                ScreenSpaceGlobalIllumination = true,
                ReflectionMinSmoothness = 0.45f,
                AmbientOcclusionIntensity = 1.4f,
                DominantSurface = BFSurfaceType.Concrete,
            }
        },

        // Naboo: clear temperate daylight over grass and water.
        { "nab", new BFEnvironmentLightingProfile
            {
                Name = "Naboo",
                SunIntensity = 75000f,
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
                SunIntensity = 30000f,
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
                SunIntensity = 20000f,
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
                SunIntensity = 22000f,
                SunColor = new Color(0.90f, 0.94f, 1f),
                SunAngle = new Vector2(30f, 210f),
                PlanetaryGroundTint = new Color(0.70f, 0.74f, 0.80f),
                AtmosphereTint = new Color(0.55f, 0.65f, 0.80f),
                FogMeanFreePath = 300f,
                FogTint = new Color(0.86f, 0.90f, 0.96f),
                AmbientIntensity = 1.4f,
                IndirectDiffuseIntensity = 1.4f,
                ExposureCompensation = -0.9f,
                ReflectionMinSmoothness = 0.5f,
                BaseSnowCoverage = 0.6f,
                DominantSurface = BFSurfaceType.Concrete,
            }
        },

        // Utapau: deep sinkhole, rock everywhere, light from directly above.
        { "uta", new BFEnvironmentLightingProfile
            {
                Name = "Utapau",
                SunIntensity = 55000f,
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
                SunIntensity = 60000f,
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
                SunIntensity = 7000f,
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
                ExposureCompensation = 0.8f,
                ShadowDistance = 200f,
                BaseWetness = 0.9f,
                DominantSurface = BFSurfaceType.Mud,
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
        SunIntensity = 0f,
        PreferAuthoredSunAngle = false,
        PreferAuthoredFog = false,
        Volumetrics = true,
        FogMeanFreePath = 120f,
        FogTint = new Color(0.7f, 0.72f, 0.78f),
        FogMaximumHeight = 60f,
        VolumetricLightingMultiplier = 1.3f,
        AmbientIntensity = 0.5f,
        ExposureCompensation = 0.4f,
        ShadowDistance = 120f,
        ContactShadowLength = 0.8f,           // interiors live on contact detail
        ScreenSpaceGlobalIllumination = true,
        ReflectionMinSmoothness = 0.4f,
        AmbientOcclusionIntensity = 1.7f,
        DominantSurface = BFSurfaceType.Metal,
    };

    static BFEnvironmentLightingProfile Space(string name) => new BFEnvironmentLightingProfile
    {
        Name = name,
        Sky = BFSkyKind.Space,
        SunIntensity = 100000f,
        SunColor = Color.white,
        SunAngularDiameter = 0.2f,            // pinpoint star, razor shadows
        PreferAuthoredSunAngle = false,
        PreferAuthoredFog = false,
        Volumetrics = false,                  // vacuum does not scatter
        FogMeanFreePath = 100000f,
        AmbientIntensity = 0.15f,
        IndirectDiffuseIntensity = 0.3f,
        ExposureCompensation = -0.5f,
        ExposureLimits = new Vector2(-4f, 16f),
        ShadowDistance = 900f,
        ContactShadowLength = 0.5f,
        AmbientOcclusionIntensity = 1.1f,
        DominantSurface = BFSurfaceType.Metal,
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
