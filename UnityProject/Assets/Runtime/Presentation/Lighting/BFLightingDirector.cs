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
    HDShadowSettings ShadowSettings;

    Light Sun;
    HDAdditionalLightData SunData;
    float SunBaselineIntensity;

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
        ShadowSettings = Profile.Add<HDShadowSettings>(true);

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
        ApplyExposure(Active);
        ApplyShadows(Active);
        ApplyIndirect(Active);

        // Sun before fog: the log below reports what both settled at, and the
        // fog pass reads nothing from the sun, so this ordering costs nothing
        // and makes the diagnostic complete.
        ApplySun(Active);
        ApplyFog(Active);

        // Logged with the resulting numbers, not the requested ones: "the map
        // looks dark" is otherwise a very expensive thing to trace back to a
        // multiplier in a table.
        Debug.Log($"[BFPresentation] Lighting profile '{Active.Name}' for '{worldName}': " +
                  $"sun {SunBaselineIntensity:F0} x{Active.SunIntensityScale:F2} = " +
                  $"{(SunData != null ? SunData.intensity : 0f):F0} lux, " +
                  $"ambient x{Active.AmbientIntensity:F2}, EV {Active.ExposureCompensation:+0.00;-0.00;0}, " +
                  $"fog {(Fog.enabled.value ? Fog.meanFreePath.value.ToString("F0") + "m" : "off")}, " +
                  $"dominant surface {Active.DominantSurface}.");
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

    /// <summary>
    /// Atmosphere, but only where the map actually has any.
    /// </summary>
    /// <remarks>
    /// This used to enable fog unconditionally, and it was a large part of why
    /// maps went dark. Volumetric fog absorbs as well as scatters, so a level
    /// that authored no fog gained a grey veil, lost contrast, and lost the
    /// sun's contribution to everything behind it - for no artistic reason,
    /// since the level designers had chosen clear air.
    ///
    /// Fog is now on when the .sky chunk asked for it, or when the environment
    /// is one that is physically soupy whatever the chunk says (a swamp, a
    /// storm, an ash cloud). Everything else renders clear, as it did before.
    /// </remarks>
    void ApplyFog(BFEnvironmentLightingProfile p)
    {
        // Space genuinely has no fog; forcing any is worse than none.
        if (!p.Volumetrics && p.Sky == BFEnvironmentLightingProfile.BFSkyKind.Space)
        {
            Fog.enabled.Override(false);
            return;
        }

        bool authored = SWBFSkyProperties.HasSkyInfo &&
                        SWBFSkyProperties.FogRange.y > SWBFSkyProperties.FogRange.x &&
                        SWBFSkyProperties.FogRange.y > 0f;

        if (!authored && !p.ForceAtmosphere)
        {
            Fog.enabled.Override(false);
            return;
        }

        Fog.enabled.Override(true);
        Fog.colorMode.Override(FogColorMode.SkyColor);
        Fog.meanFreePath.Override(authored && p.PreferAuthoredFog
            ? Mathf.Max(20f, SWBFSkyProperties.FogRange.y)
            : p.FogMeanFreePath);
        Fog.tint.Override(p.FogTint);
        Fog.baseHeight.Override(0f);
        Fog.maximumHeight.Override(p.FogMaximumHeight);

        Fog.enableVolumetricFog.Override(p.Volumetrics && BFPresentationQuality.Volumetrics);
        Fog.anisotropy.Override(p.FogAnisotropy);

        // Albedo is how much of what the fog intercepts it scatters onward
        // rather than absorbs, so it belongs near white - the tint carries the
        // colour. Driving it to half grey, as this first did, turned every
        // fogged map into a light sink and was the other half of the darkness.
        float scatter = Mathf.Clamp(p.VolumetricLightingMultiplier, 0.5f, 2f);
        Fog.albedo.Override(Color.Lerp(Color.white, p.FogTint, 0.5f) * Mathf.Clamp01(0.85f * scatter));
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

        // Cap the cascade range.
        //
        // Nothing was setting this, so directional shadows were rendered to
        // HDRP's default distance across maps that are a kilometre wide - the
        // whole level in the cascade atlas, at whatever resolution, every
        // frame. The profile's ShadowDistance is the distance at which that
        // map's shadows actually stop mattering, and capping to it is one of
        // the largest single savings available here.
        ShadowSettings.maxShadowDistance.Override(
            p.ShadowDistance * BFPresentationQuality.ShadowDistanceScale);
        ShadowSettings.cascadeShadowSplitCount.Override(BFPresentationQuality.ShadowCascades);
    }

    /// <summary>
    /// Leave exactly one directional light casting shadows.
    /// </summary>
    /// <remarks>
    /// HDRP supports one shadow-casting directional light and reports
    /// "Cascade Shadow atlasing has failed" - every frame - when it finds
    /// more. The importer produces more: <c>WorldLoader.ImportLights</c>
    /// enables HD shadows on the first directional it sees, but then sets
    /// <c>Light.shadows = Soft</c> on every light it creates, and it is that
    /// flag the cascade atlas counts. A map with two directionals in its .lgt
    /// therefore spams the error and pays for shadow work HDRP then discards.
    ///
    /// <c>PhxModernLighting</c> compounded it by raising every directional to
    /// a 4096 shadow resolution, so the wasted work was as expensive as it
    /// could be.
    ///
    /// The sun keeps its shadows; every other directional keeps its light and
    /// loses only the shadows it was never going to be allowed to cast.
    /// </remarks>
    void EnforceSingleShadowCastingSun(Light sun)
    {
        Light[] lights = FindObjectsOfType<Light>();
        int demoted = 0;

        for (int i = 0; i < lights.Length; ++i)
        {
            Light light = lights[i];
            if (light.type != LightType.Directional) continue;
            if (ReferenceEquals(light, sun)) continue;
            if (light.shadows == LightShadows.None) continue;

            light.shadows = LightShadows.None;

            HDAdditionalLightData data = light.GetComponent<HDAdditionalLightData>();
            data?.EnableShadows(false);
            ++demoted;
        }

        if (demoted > 0)
        {
            Debug.Log($"[BFPresentation] {demoted} directional light(s) demoted to shadowless - " +
                      "HDRP allows one shadow-casting directional and was reporting a cascade " +
                      "atlas failure every frame.");
        }
    }

    void ApplyIndirect(BFEnvironmentLightingProfile p)
    {
        IndirectLighting.indirectDiffuseLightingMultiplier.Override(p.IndirectDiffuseIntensity);
        IndirectLighting.reflectionLightingMultiplier.Override(p.IndirectSpecularIntensity);

        ApplyAmbient(p);
    }

    /// <summary>
    /// Scale the map's own ambient light.
    /// </summary>
    /// <remarks>
    /// <see cref="BFEnvironmentLightingProfile.AmbientIntensity"/> was a
    /// declared field that nothing read, so every profile that raises ambient
    /// to fill shadows under bright ground - Hoth, Mygeeto, Tatooine - was
    /// asking for something that never happened and kept crushed shadows.
    ///
    /// It has to be done by scaling the colours, not by setting
    /// <c>ambientIntensity</c>: the importer puts the scene in Trilight mode
    /// with the .lgt's own sky and ground colours, and Unity ignores
    /// ambientIntensity outside skybox ambient. Scaling the colours also keeps
    /// the map's authored hue, which is the part that is not ours to change.
    ///
    /// Safe to re-run: the importer rewrites these colours on every map load,
    /// so the scale applies to a fresh baseline rather than compounding.
    /// </remarks>
    void ApplyAmbient(BFEnvironmentLightingProfile p)
    {
        float scale = Mathf.Max(0f, p.AmbientIntensity);
        if (Mathf.Approximately(scale, 1f)) return;

        if (RenderSettings.ambientMode == UnityEngine.Rendering.AmbientMode.Skybox)
        {
            RenderSettings.ambientIntensity = scale;
            return;
        }

        RenderSettings.ambientSkyColor *= scale;
        RenderSettings.ambientEquatorColor *= scale;
        RenderSettings.ambientGroundColor *= scale;
        RenderSettings.ambientLight *= scale;

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
        Light found = FindBrightestDirectional();
        if (found == null) return;

        if (!ReferenceEquals(found, Sun))
        {
            Sun = found;
            SunData = Sun.GetComponent<HDAdditionalLightData>();

            // The importer's value, captured once per sun. Scaling in place on
            // every Apply would compound: two map loads with a 0.9 scale would
            // leave the sun at 0.81, and a dozen at a fifth of where it began.
            SunBaselineIntensity = SunData != null ? SunData.intensity : 0f;
        }
        if (SunData == null) return;

        SunData.EnableColorTemperature(false);
        SunData.angularDiameter = p.SunAngularDiameter;
        SunData.EnableShadows(true);
        SunData.shadowUpdateMode = ShadowUpdateMode.EveryFrame;
        SunData.shadowNearPlane = 0.1f;

        // Resolution by tier rather than the blanket 4096 PhxModernLighting
        // was applying to every directional light in the scene.
        SunData.SetShadowResolution(BFPresentationQuality.SunShadowResolution);

        EnforceSingleShadowCastingSun(Sun);

        // Scale what the importer gave this map, and only when the profile
        // asks. A scale of 0 means "this environment has no meaningful sun" -
        // an interior, a hangar - and there the map's own fixtures are the
        // whole lighting design, so touching the directional at all is wrong.
        if (p.SunIntensityScale > 0f && SunBaselineIntensity > 0f)
        {
            SunData.intensity = SunBaselineIntensity * p.SunIntensityScale;
        }

        Sun.color = p.SunColor;
        Sun.shadows = LightShadows.Soft;

        // The authored angle is the designer's; only override it where the map
        // cannot have meant one (interiors, space).
        if (!p.PreferAuthoredSunAngle || !SWBFSkyProperties.HasSunInfo)
        {
            Sun.transform.rotation = Quaternion.Euler(p.SunAngle.x, p.SunAngle.y, 0f);
        }
    }

    /// <summary>
    /// The map's sun.
    /// </summary>
    /// <remarks>
    /// Prefers the directional light the importer already chose to cast
    /// shadows, because that is a decision made with the .lgt in hand -
    /// the importer picks the brightest authored directional. Comparing
    /// intensities here instead would compare <c>Light.intensity</c>, which
    /// under HDRP is not the value that matters (HDRP keeps its own physical
    /// intensity on HDAdditionalLightData), so it would routinely pick the
    /// wrong light and then fight the importer over which one is the sun.
    /// </remarks>
    static Light FindBrightestDirectional()
    {
        Light[] lights = FindObjectsOfType<Light>();

        Light fallback = null;
        float bestIntensity = -1f;

        for (int i = 0; i < lights.Length; ++i)
        {
            Light light = lights[i];
            if (light.type != LightType.Directional) continue;

            if (light.shadows != LightShadows.None) return light;

            HDAdditionalLightData data = light.GetComponent<HDAdditionalLightData>();
            float intensity = data != null ? data.intensity : light.intensity;
            if (intensity <= bestIntensity) continue;

            bestIntensity = intensity;
            fallback = light;
        }
        return fallback;
    }
}
