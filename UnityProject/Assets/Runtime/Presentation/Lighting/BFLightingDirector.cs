using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

/// <summary>
/// Applies the map's <see cref="BFEnvironmentLightingProfile"/> to HDRP.
/// </summary>
/// <remarks>
/// Sits at volume priority 120, above <c>PhxModernLighting</c> (100).
///
/// <para><b>Ownership rules.</b> Four separate regressions in this layer had
/// one shape: a system read the source data correctly, and a later
/// "enhancement" overwrote it with a generic rule. Fog colour, sun colour, sun
/// shadows and skydome shadow casting were each lost that way. Each system
/// looked reasonable alone; together they discarded the map. Volume priority
/// does not save you either - a direct property write on a Light or Renderer
/// is not arbitrated by priority at all, so two writers resolve by whichever
/// component happened to be constructed first.</para>
///
/// <list type="number">
/// <item><b>One writer per parameter.</b> Fog, sun colour, sun angle, sun
/// shadows, exposure, AO, contact shadows, micro shadows and SSR are written
/// here and nowhere else. <c>PhxModernLighting</c> owns the static stack -
/// tonemapping, bloom, colour grading. <c>PhxMapAtmosphere</c> owns nothing
/// and only reports.</item>
///
/// <item><b>Source data outranks profile data.</b> Where a .lgt or .sky states
/// a value it wins, and the profile fills only what the map left unsaid. A
/// profile is a fallback and an interpretation, never a correction.</item>
///
/// <item><b>Enhancement passes may only reduce, never promote.</b> A budget
/// takes shadow casters away; it does not hand them out. This is what
/// <c>BFProbeManagers</c> violated when it re-enabled shadow casting on the
/// skydome because the dome was large - the one case where "big" means
/// backdrop rather than scenery.</item>
/// </list>
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

    /// <summary>
    /// Stops of adaptation allowed either side of the map's own exposure.
    /// </summary>
    /// <remarks>
    /// Enough that moving between shade and open ground is comfortable rather
    /// than a hard clip, small enough that the difference between them is
    /// still visible - which is the whole point of the level being lit that
    /// way. SWBF2 itself had no adaptation at all.
    /// </remarks>
    /// <remarks>
    /// Asymmetric, because the sun is the brightest thing in the scene and
    /// almost nothing in frame is as bright as it. A band centred on the sun's
    /// own EV exposes correctly for a surface facing it directly and leaves
    /// everything else underexposed - which on Kashyyyk, where the fight
    /// happens under a canopy several stops down from open sky, means a black
    /// forest floor. Room to open up is what a shaded scene needs; room to
    /// stop down further than the sun is what nothing needs.
    /// </remarks>
    const float ExposureAdaptationStops = 1.5f;
    const float ExposureOpenUpStops = 4f;

    /// <summary>
    /// Ceiling on fog albedo. Below 1 so fog always absorbs something.
    /// </summary>
    const float MaxFogAlbedo = 0.75f;

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
                  $"ambient x{Active.AmbientIntensity:F2}, " +
                  $"exposure EV {Exposure.limitMin.value:F1}..{Exposure.limitMax.value:F1} " +
                  $"({Active.ExposureCompensation:+0.00;-0.00;0} bias), " +
                  $"fog {(Fog.enabled.value ? Fog.meanFreePath.value.ToString("F0") + "m" : "off")}, " +
                  $"dominant surface {Active.DominantSurface}.");
    }

    void ApplySky(BFEnvironmentLightingProfile p)
    {
        // The map may have painted its own sky. Measured at load rather than
        // authored, because whether a dome is scenery or the actual sky is a
        // property of the content, not of a profile someone wrote by hand.
        BFEnvironmentLightingProfile.BFSkyKind kind =
            BFSkydomeReconciler.Evaluate(p) ?? p.Sky;

        switch (kind)
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

            case BFEnvironmentLightingProfile.BFSkyKind.AuthoredDome:
                // The dome is what the player sees, so this gradient exists
                // only to supply ambient and to fill any gap the dome geometry
                // leaves. Keyed off the map's own fog colour, which the artist
                // chose to match the horizon they painted - so the ambient
                // agrees with the sky instead of simulating a different one.
                Color domeAmbient = BFSkydomeReconciler.GetDomeAmbient(p);
                VisualEnvironment.skyType.Override((int)SkyType.Gradient);
                GradientSky.top.Override(domeAmbient * 0.9f);
                GradientSky.middle.Override(domeAmbient);
                GradientSky.bottom.Override(Color.Lerp(domeAmbient, p.PlanetaryGroundTint, 0.6f));
                break;
        }

        Debug.Log($"[BFLightingDirector] Sky: {kind} - {BFSkydomeReconciler.LastDecision}");
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

        // The map's own fog colour, when it has one.
        //
        // Every .sky states a FogColor, and it is one of the strongest
        // identifiers a level has - Kashyyyk's green haze, Kamino's grey
        // squall, Geonosis' dust. Overriding colorMode to SkyColor discarded
        // all of it and replaced every map's fog with a tint of its own sky,
        // which is why fogged maps all read the same washed-out way. Worse,
        // PhxMapAtmosphere had already read the authored colour and logged it,
        // so the console said the right thing while a higher-priority volume
        // quietly replaced it.
        bool authoredColor = SWBFSkyProperties.HasSkyInfo;
        if (authoredColor)
        {
            Fog.colorMode.Override(FogColorMode.ConstantColor);
            Fog.color.Override(SWBFSkyProperties.FogColor);
        }
        else
        {
            Fog.colorMode.Override(FogColorMode.SkyColor);
        }

        // A .sky states fog as a linear near/far pair, which is a different
        // quantity from HDRP's mean free path and cannot be substituted for it.
        //
        // Linear fog is clear until `near` and opaque at `far`. Mean free path
        // is the distance over which transmittance falls to 1/e, accumulating
        // from the camera with no clear zone at all. Passing `far` straight in
        // - Kashyyyk authors (90, 450) - therefore put haze on everything from
        // the lens outward, including the ninety metres the author deliberately
        // left clear, and left the far end thinner than intended besides.
        //
        // The span between the two is the distance over which the author
        // wanted fog to build, so that is what the extinction distance is
        // derived from.
        Fog.meanFreePath.Override(authored && p.PreferAuthoredFog
            ? Mathf.Max(20f, SWBFSkyProperties.FogRange.y - SWBFSkyProperties.FogRange.x)
            : p.FogMeanFreePath);
        // Tint multiplies the fog colour, so it only applies where the colour
        // is ours to choose. Applied on top of an authored colour it would
        // shift the very thing the authored value exists to state.
        Fog.tint.Override(authoredColor ? Color.white : p.FogTint);
        Fog.baseHeight.Override(0f);
        Fog.maximumHeight.Override(p.FogMaximumHeight);

        Fog.enableVolumetricFog.Override(p.Volumetrics && BFPresentationQuality.Volumetrics);
        Fog.anisotropy.Override(p.FogAnisotropy);

        // Albedo is how much of what the fog intercepts it scatters onward
        // rather than absorbs, so it belongs near white - the tint carries the
        // colour. Driving it to half grey, as this first did, turned every
        // fogged map into a light sink and was the other half of the darkness.
        // Capped below 1, because albedo 1 is a medium that scatters
        // everything and absorbs nothing - fog that only ever adds light.
        // Kashyyyk's 1.5x multiplier drove 0.85 * 1.5 past the clamp to
        // exactly white, so its forest haze became a sky-bright emitter
        // filling the frame, and auto-exposure metered that and crushed the
        // forest floor beneath it to black. Real fog takes light out of a
        // scene as well as spreading it around.
        float scatter = Mathf.Clamp(p.VolumetricLightingMultiplier, 0.5f, 2f);
        float albedo = Mathf.Min(0.85f * scatter, MaxFogAlbedo);
        Fog.albedo.Override(Color.Lerp(Color.white, p.FogTint, 0.5f) * albedo);
        Fog.globalLightProbeDimmer.Override(1f);
    }

    /// <summary>
    /// Anchor exposure to how bright this map's own key light actually is.
    /// </summary>
    /// <remarks>
    /// A fixed exposure band cannot serve the whole game. Direct sunlight sits
    /// near EV 15 and a lit interior near EV 8, so any single window either
    /// blows out Tatooine or leaves a Coruscant corridor black - which is what
    /// a hand-picked band did, in both directions on different maps.
    ///
    /// The map already states the answer. The sun's intensity is in lux
    /// straight off the .lgt, and EV100 for a given illuminance is
    /// <c>log2(E / 2.5)</c> - so the level's own key light names the exposure
    /// its author was lighting for. Adaptation is then allowed only a narrow
    /// band either side of that, which is what keeps a doorway darker than the
    /// plaza instead of re-metering until both look the same.
    ///
    /// Falls back to the profile's declared limits when there is no sun, which
    /// is the case for interiors and space maps.
    /// </remarks>
    void ApplyExposure(BFEnvironmentLightingProfile p)
    {
        Exposure.mode.Override(ExposureMode.Automatic);

        float keyLux = SunData != null && SunData.gameObject.activeInHierarchy
            ? SunData.intensity
            : 0f;

        if (keyLux > 1f)
        {
            float ev = Mathf.Log(keyLux / 2.5f, 2f);
            Exposure.limitMin.Override(ev - ExposureOpenUpStops);
            Exposure.limitMax.Override(ev + ExposureAdaptationStops);
        }
        else
        {
            Exposure.limitMin.Override(p.ExposureLimits.x);
            Exposure.limitMax.Override(p.ExposureLimits.y);
        }

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

        // Two separate concerns, deliberately kept apart. ApplyAmbient is
        // allowed to decide it has nothing to do and return; the screen-space
        // stack must be written on every path. They used to be one method, and
        // the early returns at the top of the ambient scaling skipped the SSR
        // and SSGI writes at the bottom.
        ApplyAmbient(p);
        ApplyScreenSpaceLighting(p);
    }

    /// <summary>
    /// Gate the screen-space stack for this map.
    /// </summary>
    /// <remarks>
    /// This must run for every map, and must write every flag it owns on every
    /// path, because Start adds the volume components with all override states
    /// forced on. An unwritten parameter is not "left alone" - it is HDRP's own
    /// default, applied at priority 120 over everything below.
    ///
    /// That is not theoretical. ScreenSpaceReflection.enabled defaults to true,
    /// so while these writes sat below ApplyAmbient's early returns, every map
    /// resolving to the Default profile ran SSR regardless of quality tier -
    /// a Low-tier player was paying for it. GlobalIllumination.enable defaults
    /// to false, so SSGI was forced off on those same maps even where the
    /// profile asked for it.
    ///
    /// The tier is a ceiling and the profile is a request: a feature runs only
    /// when both agree.
    /// </remarks>
    void ApplyScreenSpaceLighting(BFEnvironmentLightingProfile p)
    {
        Reflections.enabled.Override(p.ScreenSpaceReflections &&
                                     BFPresentationQuality.ScreenSpaceReflections);
        Reflections.minSmoothness = p.ReflectionMinSmoothness;

        GlobalIllumination.enable.Override(p.ScreenSpaceGlobalIllumination &&
                                           BFPresentationQuality.ScreenSpaceGlobalIllumination);

        // Quality has to be stated, not inherited. The component's own default
        // is Medium, which in this pipeline means full-resolution SSGI with 64
        // ray steps - at 1440p that is the expensive path, and it would have
        // arrived silently the moment the asset started supporting SSGI at all.
        //
        // Low is half resolution with 32 steps, which is what makes SSGI
        // affordable on the hardware this targets. Ultra is where the full-rate
        // version belongs.
        GlobalIllumination.quality.Override(
            BFPresentationQuality.Tier >= BFQualityTier.Ultra
                ? (int)ScalableSettingLevelParameter.Level.High
                : (int)ScalableSettingLevelParameter.Level.Low);
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

        // Whether the sun casts is the importer's decision, taken from the
        // .lgt's own CastShadow field - only 53 lights in the whole game carry
        // it, so it is a deliberate statement. Forcing it on here, as this did,
        // silently overrode that for every map: the one directional the
        // director happened to pick up always cast, whatever the level said.
        SunData.shadowUpdateMode = ShadowUpdateMode.EveryFrame;
        SunData.shadowNearPlane = 0.1f;

        // Resolution by tier rather than the blanket 4096 PhxModernLighting
        // was applying to every directional light in the scene.
        SunData.SetShadowResolution(BFPresentationQuality.SunShadowResolution);

        // Sample counts for PCSS. The penumbra itself comes from the sun's
        // angular diameter, which every planet profile already authors and
        // which was inert while the pipeline filtered shadows with PCF - so
        // Kamino's overcast 3 degree sun and Tatooine's hard 0.35 degree one
        // produced identical shadow edges. These counts are the cost dial;
        // the angular diameter is the map's statement and is not touched here.
        SunData.SetPCSSParams(BFPresentationQuality.SunShadowBlockerSamples,
                              BFPresentationQuality.SunShadowFilterSamples);

        EnforceSingleShadowCastingSun(Sun);

        // Scale what the importer gave this map, and only when the profile
        // asks. A scale of 0 means "this environment has no meaningful sun" -
        // an interior, a hangar - and there the map's own fixtures are the
        // whole lighting design, so touching the directional at all is wrong.
        // Time of day folds in here, as a grade on the authored sun, because
        // this is the single writer. PhxWeatherSystem only states which time
        // of day its map asked for - it used to rescale every directional
        // light itself, in place and without a baseline, which compounded
        // against whatever this had already applied.
        PhxWeatherSystem.GetTimeOfDayGrade(out float todScale, out Color todTint, out float todWeight);

        if (p.SunIntensityScale > 0f && SunBaselineIntensity > 0f)
        {
            SunData.intensity = SunBaselineIntensity * p.SunIntensityScale * todScale;
        }

        // The map's own sun colour wins, exactly as its fog colour does.
        //
        // This was the third parameter in the stack with two owners, and the
        // worst-behaved of them: BFLightingDirector wrote the profile's colour
        // and PhxMapAtmosphere wrote the .sky's, both as direct property
        // assignments on the Light rather than volume overrides. Volume
        // priority does not arbitrate that - whichever OnMapLoaded handler ran
        // last simply won, and the order depends on which host component was
        // constructed first. The sun's colour was therefore decided by
        // component construction order.
        Color sunColor = SWBFSkyProperties.HasSunInfo &&
                         SWBFSkyProperties.SunColor.maxColorComponent > 0f
            ? SWBFSkyProperties.SunColor
            : p.SunColor;

        Sun.color = todWeight > 0f ? Color.Lerp(sunColor, todTint, todWeight) : sunColor;

        // Filtering quality only, and only where shadows are already on.
        // Assigning Soft unconditionally enables them, which would put back
        // exactly the override removed above - the importer's reading of the
        // .lgt's CastShadow field is the decision, and this is a promotion.
        if (Sun.shadows != LightShadows.None) Sun.shadows = LightShadows.Soft;

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
