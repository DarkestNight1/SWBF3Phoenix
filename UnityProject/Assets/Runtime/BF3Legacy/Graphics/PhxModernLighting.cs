using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

/// <summary>
/// Modernizes the HDRP lighting stack at runtime without touching imported
/// level data: creates a global Volume with contemporary defaults -
/// ACES tonemapping, automatic exposure, ambient occlusion, screen space
/// reflections, contact shadows, volumetric fog and restrained bloom.
///
/// The original SWBF2 levels were authored for a gamma-space 2005 renderer;
/// these overrides pull the presentation towards a modern physically-based
/// look while keeping the original sun/ambient colors from the loaded maps.
///
/// Attached to the persistent BF3Legacy host by PhxBF3.Bootstrap() when
/// Config.ModernLighting is enabled.
/// </summary>
public class PhxModernLighting : MonoBehaviour
{
    Volume Volume;
    VolumeProfile Profile;

    /// <summary>
    /// Bounds on automatic exposure, in EV100.
    /// </summary>
    /// <remarks>
    /// A backstop spanning night interior to open desert, not a look. The
    /// per-map band that actually shapes the image is set by
    /// BFLightingDirector from the map's own sun. Anything narrower here would
    /// clip one end of the game: direct sun sits near EV 15 and a lit interior
    /// near EV 8, so a single tight window blows out one or blackens the other.
    /// </remarks>
    const float ExposureLimitMin = 5f;
    const float ExposureLimitMax = 16f;

    // No SSR constant here. Screen-space reflections moved to
    // BFLightingDirector when ownership was split - see
    // BFEnvironmentLightingProfile.ReflectionMinSmoothness, which carries the
    // same reasoning about clearing the smoothness detail band. Leaving a
    // second copy of the threshold behind is how the two drift apart.

    void Start()
    {
        Profile = ScriptableObject.CreateInstance<VolumeProfile>();
        Profile.name = "BF3LegacyLighting";

        // --- Tonemapping: ACES filmic, the modern standard ---
        Tonemapping tonemap = Profile.Add<Tonemapping>(true);
        tonemap.mode.Override(TonemappingMode.ACES);

        // --- Eye adaption, deliberately on a short leash ---
        //
        // This was a sixteen-stop automatic range (-2 to 14). SWBF2 renders at
        // a fixed exposure, so nothing in a stock map is authored expecting the
        // camera to re-meter: an interior is meant to read as darker than the
        // plaza outside it, and a bright surface is meant to stay bright.
        //
        // With the wide range the opposite happened. Stepping into shade let
        // exposure climb until the pale stone blew out to flat white while the
        // shadowed geometry stayed black - the crushed-blacks-against-clipped-
        // highlights look, produced by the camera rather than by the lighting.
        //
        // Ownership is split by parameter, not by component: this sets the
        // mode, the adaptation speeds, the metering and a deliberately wide
        // pair of limits, and BFLightingDirector narrows the limits per map
        // from that map's own key light. Nothing is written twice, so the
        // wide values here are only ever seen on a map with no sun to derive
        // a band from.
        Exposure exposure = Profile.Add<Exposure>(true);
        exposure.mode.Override(ExposureMode.Automatic);
        exposure.limitMin.Override(ExposureLimitMin);
        exposure.limitMax.Override(ExposureLimitMax);

        // Slow, and slower still going the other way: a fast adaptation is
        // exactly what makes the change visible as a change.
        exposure.adaptationSpeedDarkToLight.Override(1.2f);
        exposure.adaptationSpeedLightToDark.Override(0.8f);

        // Metering the centre rather than the whole frame. A third-person
        // camera puts a lot of sky or a lot of ground at the edges depending
        // on where the player is looking, and full-frame metering turns that
        // into a brightness swing every time they move the mouse.
        exposure.meteringMode.Override(MeteringMode.CenterWeighted);

        // Auto-exposure meters the whole frame, so a level that is mostly bright
        // ground (Mygeeto snow, Polis Massa interiors) drags the average up and
        // clips to white. Bias it per-install rather than guessing a constant
        // that would wreck the dark maps.
        exposure.compensation.Override(PhxBF3.Config.ExposureCompensation);

        // Ambient occlusion, contact shadows, micro shadows and screen-space
        // reflections are NOT set here.
        //
        // BFLightingDirector sets all four, per map, from the map's own
        // lighting profile, on a volume at priority 120. Setting them here as
        // well made every one of them a two-owner parameter: a value tuned in
        // one file was silently half-replaced by the other, depending on which
        // parameters each happened to override, and neither file read as
        // wrong on its own. That is what made the look untunable - a change
        // would land, be partially masked, and the result would look like the
        // change had simply not worked.
        //
        // This volume is now the static baseline - tonemapping, bloom, colour
        // grading, the things that do not vary by map - and the director owns
        // everything that does.

        // --- Restrained modern bloom (the 2005 renderer over-bloomed heavily) ---
        Bloom bloom = Profile.Add<Bloom>(true);
        bloom.intensity.Override(Mathf.Max(0f, PhxBF3.Config.BloomIntensity));
        bloom.scatter.Override(0.6f);

        // NOTE: deliberately no Fog override here. Fog is inherently per-map -
        // a fixed mean free path is meaningless across an open battlefield, a
        // ship interior and a vacuum space map alike, and HDRP tints volumetric
        // fog from the sky, so forcing it globally recolours every scene. Maps
        // that author their own fog now keep it.

        // Contrast/saturation are percentages in HDRP (ClampedFloatParameter
        // 0..±100, neutral at 0), so these are deliberately small nudges.
        ColorAdjustments color = Profile.Add<ColorAdjustments>(true);
        color.contrast.Override(8f);
        color.saturation.Override(5f);

        Volume = gameObject.AddComponent<Volume>();
        Volume.isGlobal = true;

        // Kept at 100 deliberately. Lowering this to 0.5 so map-authored
        // volumes would win turned the scene into an unusable white blowout:
        // the imported 2005 levels do not author usable HDRP exposure, so
        // something has to supply tonemapping and exposure or the image clips.
        Volume.priority = 100f;
        Volume.profile = Profile;

        UpgradeShadowQuality();

        Debug.Log("[BF3Legacy] Modern lighting volume active (ACES, SSAO, SSR, volumetrics)");
    }

    /// <summary>
    /// Raise the shadow resolution of the light that actually casts them.
    /// </summary>
    /// <remarks>
    /// This used to set 4096 on every directional light in the scene. HDRP
    /// only ever uses one shadow-casting directional - it reports "Cascade
    /// Shadow atlasing has failed" and drops the rest - so on a map whose .lgt
    /// declares more than one, that was several full-resolution cascade
    /// atlases rendered and thrown away every frame.
    ///
    /// When the presentation layer is running it owns shadow policy entirely
    /// (BFLightingDirector picks the sun, demotes the others, and sets
    /// resolution, cascade count and distance from the quality tier), so this
    /// stands down rather than fighting it. It still does the job when that
    /// layer is disabled.
    /// </remarks>
    void UpgradeShadowQuality()
    {
        if (BFPresentation.IsActive) return;

        HDAdditionalLightData[] lights = FindObjectsOfType<HDAdditionalLightData>();
        foreach (HDAdditionalLightData light in lights)
        {
            Light l = light.GetComponent<Light>();
            if (l != null && l.type == LightType.Directional && l.shadows != LightShadows.None)
            {
                light.SetShadowResolution(2048);
            }
        }
    }

    void OnDestroy()
    {
        if (Profile != null)
        {
            ScriptableObject.Destroy(Profile);
        }
    }
}
