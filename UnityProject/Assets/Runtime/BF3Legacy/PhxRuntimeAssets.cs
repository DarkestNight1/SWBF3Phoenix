using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

/// <summary>
/// Helpers for creating lights and materials at runtime that actually work
/// under HDRP and survive a player build.
///
/// Two traps this exists to avoid:
///
///  1. HDRP renders lights through HDAdditionalLightData. A bare
///     `gameObject.AddComponent&lt;Light&gt;()` has no such component, so the
///     light may not be rendered at all - and HDRP uses physical units
///     (Lumen for point/spot, Lux for directional), so raw builtin-pipeline
///     intensity values read as either invisible or blinding.
///
///  2. `Shader.Find` only resolves shaders that survived build stripping.
///     A shader referenced by nothing in any scene (e.g. "Sprites/Default"
///     used purely from script) can be missing in a player build, which
///     yields a null material and magenta/invisible geometry.
/// </summary>
public static class PhxRuntimeAssets
{
    // Candidates tried in order for simple unlit/line rendering
    static readonly string[] UnlitShaderCandidates =
    {
        "HDRP/Unlit",
        "Sprites/Default",
        "Unlit/Color",
        "Universal Render Pipeline/Unlit",
    };

    static Shader CachedUnlit;
    static bool UnlitLookupDone;

    /// <summary>
    /// A shader usable for LineRenderers / simple emissive geometry, or null
    /// if none of the candidates survived stripping.
    /// </summary>
    public static Shader GetUnlitShader()
    {
        if (UnlitLookupDone) return CachedUnlit;
        UnlitLookupDone = true;

        foreach (string name in UnlitShaderCandidates)
        {
            Shader s = Shader.Find(name);
            if (s != null)
            {
                CachedUnlit = s;
                return CachedUnlit;
            }
        }

        Debug.LogWarning("[BF3Legacy] No unlit shader available at runtime " +
                         "(build stripping?). Line effects will use the default material.");
        return null;
    }

    /// <summary>Material for a LineRenderer; never returns null-shaded.</summary>
    public static Material CreateLineMaterial(Color color)
    {
        Shader shader = GetUnlitShader();
        Material mat = shader != null ? new Material(shader) : new Material(Shader.Find("Diffuse"));
        if (mat.shader == null)
        {
            // last resort: borrow a primitive's material
            GameObject probe = GameObject.CreatePrimitive(PrimitiveType.Quad);
            mat = new Material(probe.GetComponent<Renderer>().sharedMaterial);
            Object.Destroy(probe);
        }
        SetColor(mat, color);
        return mat;
    }

    /// <summary>Sets colour across builtin/HDRP property names.</summary>
    public static void SetColor(Material mat, Color color)
    {
        if (mat == null) return;
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
        if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
        if (mat.HasProperty("_UnlitColor")) mat.SetColor("_UnlitColor", color);
        mat.color = color;
    }

    /// <summary>Tint a renderer's instanced material, HDRP-safe.</summary>
    public static void Tint(GameObject go, Color color)
    {
        Renderer r = go != null ? go.GetComponent<Renderer>() : null;
        if (r != null) SetColor(r.material, color);
    }

    /// <summary>
    /// Create a point light that renders under HDRP.
    /// <paramref name="intensityLumen"/> is in Lumen (HDRP's physical unit for
    /// punctual lights) - roughly: a household bulb ~800, a floodlight ~10000.
    /// </summary>
    public static Light CreatePointLight(GameObject host, Color color, float range,
                                         float intensityLumen, bool castShadows = false)
    {
        Light light = host.AddComponent<Light>();
        light.type = LightType.Point;
        light.color = color;
        light.range = range;
        light.shadows = castShadows ? LightShadows.Soft : LightShadows.None;

        ConfigureHD(light, intensityLumen, LightUnit.Lumen, castShadows);
        return light;
    }

    /// <summary>
    /// Create a directional light that renders under HDRP.
    /// <paramref name="intensityLux"/> is in Lux - full daylight sun is
    /// ~100000, an overcast day ~10000, moonlight &lt; 1.
    /// </summary>
    public static Light CreateDirectionalLight(GameObject host, Color color,
                                               float intensityLux, bool castShadows = true)
    {
        Light light = host.AddComponent<Light>();
        light.type = LightType.Directional;
        light.color = color;
        light.shadows = castShadows ? LightShadows.Soft : LightShadows.None;

        ConfigureHD(light, intensityLux, LightUnit.Lux, castShadows);
        return light;
    }

    /// <summary>
    /// Change a light's intensity after creation. Writing Light.intensity
    /// directly does NOT work as expected under HDRP (it keeps its own
    /// physical-unit value), so fades must go through this.
    /// </summary>
    public static void SetIntensity(Light light, float intensity, bool directional = false)
    {
        if (light == null) return;
        try
        {
            HDAdditionalLightData hd = light.gameObject.GetComponent<HDAdditionalLightData>();
            if (hd != null)
            {
                hd.SetIntensity(intensity, directional ? LightUnit.Lux : LightUnit.Lumen);
                return;
            }
        }
        catch { /* fall through to builtin */ }

        light.intensity = directional ? Mathf.Clamp(intensity / 50000f, 0f, 8f) : intensity;
    }

    /// <summary>
    /// Multiply an existing light's intensity, reading whichever value the
    /// active pipeline actually uses. Used for time-of-day relighting of
    /// lights that were created by the level importer, not by us.
    /// </summary>
    public static void ScaleIntensity(Light light, float factor)
    {
        if (light == null) return;
        try
        {
            HDAdditionalLightData hd = light.gameObject.GetComponent<HDAdditionalLightData>();
            if (hd != null)
            {
                hd.SetIntensity(hd.intensity * factor);
                return;
            }
        }
        catch { /* fall through */ }

        light.intensity *= factor;
    }

    static void ConfigureHD(Light light, float intensity, LightUnit unit, bool castShadows)
    {
        try
        {
            HDAdditionalLightData hd = light.gameObject.GetComponent<HDAdditionalLightData>();
            if (hd == null)
            {
                hd = light.gameObject.AddComponent<HDAdditionalLightData>();
            }

            hd.SetIntensity(intensity, unit);
            hd.EnableShadows(castShadows);
            hd.affectsVolumetric = true;
        }
        catch (System.Exception e)
        {
            // Non-HDRP pipeline or an API change: fall back to the builtin
            // intensity so the light is at least visible.
            light.intensity = unit == LightUnit.Lux ? Mathf.Clamp(intensity / 50000f, 0f, 8f) : 3f;
            Debug.LogWarning($"[BF3Legacy] HDRP light setup unavailable ({e.Message}); " +
                             "using builtin intensity.");
        }
    }
}
