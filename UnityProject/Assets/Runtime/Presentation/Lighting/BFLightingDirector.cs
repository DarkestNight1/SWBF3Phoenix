using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

/// <summary>
/// Applies the map's <see cref="BFEnvironmentLightingProfile"/> to HDRP.
/// </summary>
/// <remarks>
/// Sits at volume priority 120, above <c>PhxModernLighting</c> (100, generic
/// modernization) and <c>PhxMapAtmosphere</c> (110, the map's own authored
/// fog and sun). The order is deliberate and is the whole "stock data, modern
/// interpretation" principle expressed as a number: the authored values decide
/// *what* the environment is, and this decides *how it is rendered*.
///
/// Where the two overlap - fog distance, sun angle - the profile defers to the
/// authored value unless it declares otherwise, which only the environments
/// with no meaningful authored sky do (interiors, space).
/// </remarks>
public class BFLightingDirector : MonoBehaviour
{
    Volume Volume;
    VolumeProfile Profile;

    Fog Fog;
    Exposure Exposure;
    AmbientOcclusion AmbientOcclusion;
    ScreenSpaceReflection Reflections;
    ContactShadows ContactShadows;
    MicroShadowing MicroShadows;
    IndirectLightingController IndirectLighting;
    PhysicallyBasedSky PhysicalSky;
    GradientSky GradientSky;
    VisualEnvironment VisualEnvironment;
    GlobalIllumination GlobalIllumination;

    Light Sun;
    HDAdditionalLightData SunData;

    /// <summary>The profile in force, for anything that needs to read it.</summary>
    public static BFEnvironmentLightingProfile Active { get; private set; } =
        BFEnvironmentLightingProfile.Default;

    void Start()
    {
        Profile = ScriptableObject.CreateInstance<VolumeProfile>();
        Profile.name = "BFEnvironmentLighting";

        VisualEnvironment = Profile.Add<VisualEnvironment>(true);
        PhysicalSky = Profile.Add<PhysicallyBasedSky>(true);
        GradientSky = Profile.Add<GradientSky>(true);
        Fog = Profile.Add<Fog>(true);
        Exposure = Profile.Add<Exposure>(true);
        AmbientOcclusion = Profile.Add<AmbientOcclusion>(true);
        Reflections = Profile.Add<ScreenSpaceReflection>(true);
        ContactShadows = Profile.Add<ContactShadows>(true);
        MicroShadows = Profile.Add<MicroShadowing>(true);
        IndirectLighting = Profile.Add<IndirectLightingController>(true);
        GlobalIllumination = Profile.Add<GlobalIllumination>(true);

        Volume = gameObject.AddComponent<Volume>();
        Volume.isGlobal = true;
        Volume.priority = 120f;
        Volume.profile = Profile;

        if (PhxGame.Instance != null)
        {
            PhxGame.Instance.OnMapLoaded += Apply;
        }
    }

    void OnDestroy()
    {
        if (PhxGame.Instance != null)
        {
            PhxGame.Instance.OnMapLoaded -= Apply;
        }
    }

    void Apply()
    {
        string worldName = PhxGame.GetEnvironment()?.GetWorldName() ?? "";
        Active = BFEnvironmentLightingProfile.Resolve(worldName);

        // The map's dominant surface seeds everything that has to answer
        // "what am I standing on" before it has anything better to go on.
        BFSurfaceQuery.MapDefault = Active.DominantSurface;

        ApplySky(Active);
        ApplyFog(Active);
        ApplyExposure(Active);
        ApplyShadows(Active);
        ApplyIndirect(Active);
        ApplySun(Active);

        Debug.Log($"[BFPresentation] Lighting profile '{Active.Name}' applied for '{worldName}' " +
                  $"(sun {Active.SunIntensity:F0} lux, fog {(Active.PreferAuthoredFog ? "authored" : Active.FogMeanFreePath.ToString("F0") + "m")}, " +
                  $"dominant surface {Active.DominantSurface}).");
    }

    void ApplySky(BFEnvironmentLightingProfile p)
    {
        switch (p.Sky)
        {
            case BFEnvironmentLightingProfile.BFSkyKind.PhysicallyBased:
                VisualEnvironment.skyType.Override((int)SkyType.PhysicallyBased);
                PhysicalSky.groundTint.Override(p.PlanetaryGroundTint);
                PhysicalSky.airDensityR.Override(Mathf.Clamp01(0.04f * p.AtmosphereDensity));
                PhysicalSky.airDensityG.Override(Mathf.Clamp01(0.09f * p.AtmosphereDensity));
                PhysicalSky.airDensityB.Override(Mathf.Clamp01(0.22f * p.AtmosphereDensity));
                PhysicalSky.airTint.Override(p.AtmosphereTint);
                PhysicalSky.aerosolDensity.Override(Mathf.Clamp01(p.AerosolDensity));
                break;

            case BFEnvironmentLightingProfile.BFSkyKind.Gradient:
                // An interior has no sky worth simulating; what matters is that
                // the ambient probe is a sane neutral rather than a physical
                // atmosphere leaking blue light into a corridor.
                VisualEnvironment.skyType.Override((int)SkyType.Gradient);
                GradientSky.top.Override(p.AtmosphereTint * 0.6f);
                GradientSky.middle.Override(p.AtmosphereTint * 0.4f);
                GradientSky.bottom.Override(p.PlanetaryGroundTint * 0.5f);
                break;

            case BFEnvironmentLightingProfile.BFSkyKind.Space:
                VisualEnvironment.skyType.Override((int)SkyType.Gradient);
                GradientSky.top.Override(new Color(0.01f, 0.012f, 0.02f));
                GradientSky.middle.Override(new Color(0.008f, 0.01f, 0.018f));
                GradientSky.bottom.Override(new Color(0.005f, 0.006f, 0.012f));
                break;
        }
    }

    void ApplyFog(BFEnvironmentLightingProfile p)
    {
        // Space genuinely has no fog; forcing any is worse than none.
        if (!p.Volumetrics && p.Sky == BFEnvironmentLightingProfile.BFSkyKind.Space)
        {
            Fog.enabled.Override(false);
            return;
        }

        bool authored = p.PreferAuthoredFog &&
                        SWBFSkyProperties.HasSkyInfo &&
                        SWBFSkyProperties.FogRange.y > SWBFSkyProperties.FogRange.x &&
                        SWBFSkyProperties.FogRange.y > 0f;

        Fog.enabled.Override(true);
        Fog.colorMode.Override(FogColorMode.SkyColor);
        Fog.meanFreePath.Override(authored
            ? Mathf.Max(20f, SWBFSkyProperties.FogRange.y)
            : p.FogMeanFreePath);
        Fog.tint.Override(p.FogTint);
        Fog.baseHeight.Override(0f);
        Fog.maximumHeight.Override(p.FogMaximumHeight);

        Fog.enableVolumetricFog.Override(p.Volumetrics && BFPresentationQuality.Volumetrics);
        Fog.anisotropy.Override(p.FogAnisotropy);

        // The profile's volumetric multiplier is expressed on the fog's own
        // albedo rather than a scattering-intensity parameter: HDRP 10.7's Fog
        // has no multiple-scattering control, and brightening the albedo is
        // what actually makes light shafts read more strongly through it.
        float scatter = Mathf.Clamp(p.VolumetricLightingMultiplier, 0.25f, 2f);
        Fog.albedo.Override(p.FogTint * Mathf.Clamp01(0.5f * scatter));
        Fog.globalLightProbeDimmer.Override(1f);
    }

    void ApplyExposure(BFEnvironmentLightingProfile p)
    {
        Exposure.mode.Override(ExposureMode.Automatic);
        Exposure.limitMin.Override(p.ExposureLimits.x);
        Exposure.limitMax.Override(p.ExposureLimits.y);

        // The per-map value and the user's own bias compose: a player who has
        // dialled the whole game brighter should still see Hoth pulled down
        // relative to Dagobah.
        Exposure.compensation.Override(p.ExposureCompensation + PhxBF3.Config.ExposureCompensation);
    }

    void ApplyShadows(BFEnvironmentLightingProfile p)
    {
        ContactShadows.enable.Override(BFPresentationQuality.ContactShadows);
        ContactShadows.length.Override(p.ContactShadowLength);
        ContactShadows.opacity.Override(p.ContactShadowOpacity);

        MicroShadows.enable.Override(true);
        MicroShadows.opacity.Override(p.MicroShadowOpacity);

        AmbientOcclusion.intensity.Override(p.AmbientOcclusionIntensity);
    }

    void ApplyIndirect(BFEnvironmentLightingProfile p)
    {
        IndirectLighting.indirectDiffuseLightingMultiplier.Override(p.IndirectDiffuseIntensity);
        IndirectLighting.reflectionLightingMultiplier.Override(p.IndirectSpecularIntensity);

        Reflections.enabled.Override(p.ScreenSpaceReflections && BFPresentationQuality.ScreenSpaceReflections);
        Reflections.minSmoothness = p.ReflectionMinSmoothness;

        GlobalIllumination.enable.Override(p.ScreenSpaceGlobalIllumination &&
                                           BFPresentationQuality.ScreenSpaceGlobalIllumination);
    }

    /// <summary>
    /// Retune the map's own sun rather than adding one.
    /// </summary>
    /// <remarks>
    /// The importer creates the directional lights the map authored, and one of
    /// them is the sun. Replacing it would discard the level designer's
    /// placement; this keeps the light and its direction and corrects only the
    /// things the 2005 format could not express - physical intensity, angular
    /// diameter (and therefore shadow softness), and colour temperature
    /// handling.
    /// </remarks>
    void ApplySun(BFEnvironmentLightingProfile p)
    {
        if (p.SunIntensity <= 0f) return;

        if (Sun == null || !Sun.isActiveAndEnabled)
        {
            Sun = FindBrightestDirectional();
        }
        if (Sun == null) return;

        SunData = Sun.GetComponent<HDAdditionalLightData>();
        if (SunData == null) return;

        SunData.EnableColorTemperature(false);
        SunData.intensity = p.SunIntensity;
        SunData.angularDiameter = p.SunAngularDiameter;
        SunData.EnableShadows(true);
        SunData.shadowUpdateMode = ShadowUpdateMode.EveryFrame;
        SunData.shadowNearPlane = 0.1f;

        Sun.color = p.SunColor;
        Sun.shadows = LightShadows.Soft;

        // The authored angle is the designer's; only override it where the map
        // cannot have meant one (interiors, space).
        if (!p.PreferAuthoredSunAngle || !SWBFSkyProperties.HasSunInfo)
        {
            Sun.transform.rotation = Quaternion.Euler(p.SunAngle.x, p.SunAngle.y, 0f);
        }
    }

    static Light FindBrightestDirectional()
    {
        Light[] lights = FindObjectsOfType<Light>();
        Light best = null;
        float bestIntensity = -1f;

        for (int i = 0; i < lights.Length; ++i)
        {
            if (lights[i].type != LightType.Directional) continue;
            if (lights[i].intensity <= bestIntensity) continue;

            bestIntensity = lights[i].intensity;
            best = lights[i];
        }
        return best;
    }
}
