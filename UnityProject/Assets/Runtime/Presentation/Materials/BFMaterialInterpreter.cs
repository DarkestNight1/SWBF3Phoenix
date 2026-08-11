using UnityEngine;
using LibSWBF2.Enums;

/// <summary>
/// Translates an authored SWBF2 material into an HDRP one.
/// </summary>
/// <remarks>
/// The rule this exists to enforce, learned the hard way: <b>the source
/// material decides, derived data only fills channels the source never had.</b>
///
/// The first attempt at modernising materials inverted that. It derived
/// metallic and smoothness from the statistics of the diffuse texture and wrote
/// them into an HDRP mask map - which HDRP then prefers over the scalar
/// <c>_Metallic</c> and <c>_Smoothness</c> the loader had already set from the
/// authored MATL. So the real data (a Specular flag, a specular exponent, a
/// specular colour) was silently overwritten by a guess, and on a level whose
/// textures are named for what they are - "floor", "panel", "wall" - the guess
/// classified most of the map as metal and turned Coruscant into chrome.
///
/// What the source actually tells us, and what each thing maps to:
///
/// <list type="bullet">
/// <item><c>Specular</c> flag - whether this surface has a highlight at all.
/// Absent means matte, and that is a decision, not an omission.</item>
/// <item><c>SpecularExponent</c> - Blinn-Phong power, convertible to
/// smoothness by the standard roughness relation.</item>
/// <item><c>SpecularColor</c> - how strong that highlight is.</item>
/// <item><c>Glossmap</c> - the alpha channel varies gloss across the
/// surface.</item>
/// <item><c>EnvMap</c> - the artist marked this surface as reflecting its
/// surroundings. This, and only this, is the source's notion of metal.</item>
/// <item><c>Glow</c>, <c>Transparent</c>, <c>Additive</c>, <c>Doublesided</c>,
/// <c>Hardedged</c> - surface behaviour the loader already handles.</item>
/// </list>
///
/// Derived maps then contribute occlusion, fine relief, and a narrow
/// smoothness variation <i>around</i> the authored value - never replacing it.
/// </remarks>
public static class BFMaterialInterpreter
{
    /// <summary>
    /// How far derived detail may push smoothness either side of the authored
    /// value, as a fraction of the full range.
    /// </summary>
    /// <remarks>
    /// Narrow on purpose. This is the difference between "the artist said
    /// semi-gloss and the panel lines read slightly crisper" and "the texture's
    /// contrast decided how shiny the wall is". The first is modernisation;
    /// the second is what produced a mirror-finish hangar.
    /// </remarks>
    public const float SmoothnessDetailBand = 0.12f;

    /// <summary>
    /// Metallic given to a surface the artist flagged as reflecting its
    /// environment.
    /// </summary>
    /// <remarks>
    /// Well below 1. EnvMap in the source means "show a reflection here", not
    /// "this is a conductor with no diffuse response", and full metallic
    /// removes the diffuse colour the artist painted - which on hand-painted
    /// 2005 art is the entire asset.
    /// </remarks>
    public const float EnvMapMetallic = 0.35f;

    /// <summary>
    /// Apply the authored material's own description to an HDRP material.
    /// </summary>
    /// <returns>The authored smoothness, so callers can band detail around it.</returns>
    public static float ApplyAuthoredResponse(Material material, BFMaterialDefinition definition)
    {
        if (material == null || definition == null) return 0f;

        EMaterialFlags flags = definition.Flags;

        float metallic = flags.HasFlag(EMaterialFlags.EnvMap) ? EnvMapMetallic : 0f;
        material.SetFloat("_Metallic", metallic);

        float smoothness = AuthoredSmoothness(definition);
        material.SetFloat("_Smoothness", smoothness);

        return smoothness;
    }

    /// <summary>Smoothness the source material asks for, 0..1.</summary>
    public static float AuthoredSmoothness(BFMaterialDefinition definition)
    {
        if (definition == null) return 0f;

        EMaterialFlags flags = definition.Flags;

        // No specular flag is the artist marking this matte. An unflagged
        // surface that nonetheless reflects the environment gets a little,
        // because EnvMap without Specular is still a reflective intent.
        if (!flags.HasFlag(EMaterialFlags.Specular))
        {
            return flags.HasFlag(EMaterialFlags.EnvMap) ? 0.35f : 0f;
        }

        float smoothness = SmoothnessFromExponent(definition.SpecularExponent);

        // Specular colour is the highlight's strength; HDRP's metallic
        // workflow has no separate specular-tint channel, so its brightness
        // folds into smoothness.
        LibSWBF2.Types.Vector3 spec = definition.SpecularColor;
        float brightness = Mathf.Clamp01(Mathf.Max(spec.X, Mathf.Max(spec.Y, spec.Z)));

        // A specular-flagged material with a black specular colour is far more
        // likely to be data the munger never filled in than a deliberate
        // "flagged but invisible", so it keeps a floor rather than going matte.
        if (brightness <= 0.01f) brightness = 0.5f;

        return Mathf.Clamp01(smoothness * brightness);
    }

    /// <summary>
    /// Blinn-Phong specular exponent to smoothness, by the standard roughness
    /// relation <c>roughness = sqrt(2 / (exponent + 2))</c>.
    /// </summary>
    static float SmoothnessFromExponent(uint exponent)
    {
        if (exponent == 0) return 0.25f;

        float roughness = Mathf.Sqrt(2f / (exponent + 2f));
        return Mathf.Clamp01(1f - roughness);
    }

    /// <summary>
    /// Band the mask map's smoothness channel around the authored value.
    /// </summary>
    /// <remarks>
    /// HDRP ignores <c>_Smoothness</c> once a mask map is bound and uses the
    /// mask's alpha remapped through <c>_SmoothnessRemapMin/Max</c> instead.
    /// Setting that remap to a narrow band centred on the authored smoothness
    /// is what keeps the source in charge while still letting derived detail
    /// vary across the surface - and it is the piece whose absence let a
    /// derived map take over the material entirely.
    /// </remarks>
    public static void BandMaskSmoothness(Material material, float authoredSmoothness)
    {
        if (material == null) return;

        float min = Mathf.Clamp01(authoredSmoothness - SmoothnessDetailBand);
        float max = Mathf.Clamp01(authoredSmoothness + SmoothnessDetailBand);

        if (material.HasProperty("_SmoothnessRemapMin")) material.SetFloat("_SmoothnessRemapMin", min);
        if (material.HasProperty("_SmoothnessRemapMax")) material.SetFloat("_SmoothnessRemapMax", max);

        // Metallic is authored outright, so its remap collapses to a point -
        // the mask's red channel must not be able to reintroduce a guess.
        float metallic = material.HasProperty("_Metallic") ? material.GetFloat("_Metallic") : 0f;
        if (material.HasProperty("_MetallicRemapMin")) material.SetFloat("_MetallicRemapMin", metallic);
        if (material.HasProperty("_MetallicRemapMax")) material.SetFloat("_MetallicRemapMax", metallic);
    }
}
