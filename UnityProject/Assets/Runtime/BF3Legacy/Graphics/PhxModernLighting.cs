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

    void Start()
    {
        Profile = ScriptableObject.CreateInstance<VolumeProfile>();
        Profile.name = "BF3LegacyLighting";

        // --- Tonemapping: ACES filmic, the modern standard ---
        Tonemapping tonemap = Profile.Add<Tonemapping>(true);
        tonemap.mode.Override(TonemappingMode.ACES);

        // --- Automatic eye adaption ---
        Exposure exposure = Profile.Add<Exposure>(true);
        exposure.mode.Override(ExposureMode.Automatic);
        exposure.limitMin.Override(-2f);
        exposure.limitMax.Override(14f);
        exposure.adaptationSpeedDarkToLight.Override(3f);
        exposure.adaptationSpeedLightToDark.Override(1.5f);

        // Auto-exposure meters the whole frame, so a level that is mostly bright
        // ground (Mygeeto snow, Polis Massa interiors) drags the average up and
        // clips to white. Bias it per-install rather than guessing a constant
        // that would wreck the dark maps.
        exposure.compensation.Override(PhxBF3.Config.ExposureCompensation);

        // --- Ambient occlusion: grounds objects, key for greebled SW surfaces ---
        AmbientOcclusion ao = Profile.Add<AmbientOcclusion>(true);
        ao.intensity.Override(1.2f);
        ao.radius.Override(1.5f);

        // --- Screen space reflections for polished floors/ship hulls ---
        ScreenSpaceReflection ssr = Profile.Add<ScreenSpaceReflection>(true);
        ssr.enabled.Override(true);
        // minSmoothness/smoothnessFadeStart are plain quality-aware properties on
        // ScreenSpaceReflection (HDRP 10.7), not VolumeParameter<T> - Add<T>(true)
        // above already marks every parameter overridden, so a direct assignment
        // is the correct (and only) way to set this one.
        ssr.minSmoothness = 0.6f;

        // --- Contact shadows: small-scale grounding detail ---
        ContactShadows contactShadows = Profile.Add<ContactShadows>(true);
        contactShadows.enable.Override(true);
        contactShadows.length.Override(0.6f);
        contactShadows.opacity.Override(0.8f);

        // --- Micro shadows from normal maps ---
        MicroShadowing microShadows = Profile.Add<MicroShadowing>(true);
        microShadows.enable.Override(true);
        microShadows.opacity.Override(0.6f);

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

    void UpgradeShadowQuality()
    {
        // Push directional shadow distance out - 2005 maps used very short
        // shadow ranges; modern GPUs can afford full-map shadows.
        HDAdditionalLightData[] lights = FindObjectsOfType<HDAdditionalLightData>();
        foreach (HDAdditionalLightData light in lights)
        {
            Light l = light.GetComponent<Light>();
            if (l != null && l.type == LightType.Directional)
            {
                light.SetShadowResolution(4096);
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
