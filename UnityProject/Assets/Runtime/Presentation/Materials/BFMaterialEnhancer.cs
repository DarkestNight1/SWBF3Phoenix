using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Derives modern PBR maps from the stock diffuse texture, so a 2005 asset
/// responds to light like a physical surface without being replaced.
/// </summary>
/// <remarks>
/// This is the heart of "stock data, modern interpretation". A SWBF2 texture
/// is a single hand-painted diffuse map, with the lighting the artist expected
/// already painted into it. HDRP wants a base colour plus normal, smoothness,
/// metallic and occlusion. Everything the original artist encoded is still
/// there - it is just encoded as brightness variation rather than as separate
/// channels - so the maps can be recovered from it.
///
/// What is derived, and why it works on this particular kind of art:
///
/// <list type="bullet">
/// <item><b>Normal</b> from the luminance gradient. Hand-painted SW panelling
/// puts its detail in light-and-dark linework, which is exactly what a Sobel
/// gradient reads as surface relief.</item>
/// <item><b>Occlusion</b> from local darkness relative to the neighbourhood -
/// the painted-in shadow at the bottom of a recess.</item>
/// <item><b>Smoothness</b> from local contrast: painted metal has tight
/// specular linework, painted cloth and rock do not.</item>
/// <item><b>Metallic</b> from saturation and brightness together, gated by
/// the surface type - a low-saturation bright texture on a droid is metal, the
/// same statistics on snow are not.</item>
/// </list>
///
/// This does NOT alter the original artistic identity: base colour is passed
/// through untouched, and everything derived only changes how light behaves on
/// it. It also never invents geometry or replaces an asset.
///
/// Cost is bounded by caching per source texture and by the quality tier -
/// deriving four maps for every texture in a level is real work, so it is
/// done once, off the main path where possible, and skipped entirely on Low.
/// </remarks>
public static class BFMaterialEnhancer
{
    public sealed class DerivedMaps
    {
        public Texture2D Normal;
        public Texture2D MaskMap;      // HDRP: R metallic, G occlusion, B detail, A smoothness
    }

    static readonly Dictionary<string, DerivedMaps> Cache =
        new Dictionary<string, DerivedMaps>(System.StringComparer.OrdinalIgnoreCase);

    /// <summary>Strength of the derived normal relief.</summary>
    public static float NormalStrength = 1.6f;

    public static int CachedCount => Cache.Count;

    public static void Reset()
    {
        Cache.Clear();
    }

    /// <summary>
    /// Apply derived maps to a material built from <paramref name="source"/>.
    /// </summary>
    /// <param name="surfaceHint">
    /// What the surface is, where the caller knows. Armour, droids and hull
    /// plate want metallic response; rock, snow and cloth must never get it,
    /// and their statistics alone are not enough to tell them apart.
    /// </param>
    public static void Enhance(Material material, Texture2D source, BFSurfaceType surfaceHint)
    {
        if (!BFPresentationQuality.DerivedMaterialMaps) return;
        if (material == null || source == null) return;

        DerivedMaps maps = Derive(source, surfaceHint);
        if (maps == null) return;

        if (maps.Normal != null && material.HasProperty("_NormalMap"))
        {
            material.SetTexture("_NormalMap", maps.Normal);
            material.SetFloat("_NormalScale", NormalStrength);
            material.EnableKeyword("_NORMALMAP");
        }

        if (maps.MaskMap != null && material.HasProperty("_MaskMap"))
        {
            material.SetTexture("_MaskMap", maps.MaskMap);
            material.EnableKeyword("_MASKMAP");
        }
    }

    /// <summary>Derive (and cache) the maps for one source texture.</summary>
    public static DerivedMaps Derive(Texture2D source, BFSurfaceType surfaceHint)
    {
        if (source == null) return null;

        string key = source.name + "|" + surfaceHint;
        if (Cache.TryGetValue(key, out DerivedMaps known)) return known;

        Color32[] pixels;
        try
        {
            pixels = source.GetPixels32();
        }
        catch (UnityException)
        {
            // Compressed or non-readable textures cannot be sampled on the CPU.
            // That is normal for a lot of imported content and is not an error;
            // the material simply keeps its stock response.
            Cache[key] = null;
            return null;
        }

        int width = source.width;
        int height = source.height;
        if (width < 4 || height < 4 || pixels.Length < width * height)
        {
            Cache[key] = null;
            return null;
        }

        var maps = new DerivedMaps
        {
            Normal = BuildNormal(pixels, width, height, source.name),
            MaskMap = BuildMaskMap(pixels, width, height, source.name, surfaceHint),
        };

        Cache[key] = maps;
        return maps;
    }

    static float Luminance(Color32 c) => (0.299f * c.r + 0.587f * c.g + 0.114f * c.b) / 255f;

    /// <summary>
    /// Surface relief from the painted linework, via a Sobel gradient of
    /// luminance.
    /// </summary>
    static Texture2D BuildNormal(Color32[] pixels, int width, int height, string name)
    {
        var normal = new Texture2D(width, height, TextureFormat.RGBA32, true, true)
        {
            name = name + "_bf_normal",
            wrapMode = TextureWrapMode.Repeat,
        };

        var output = new Color32[width * height];

        for (int y = 0; y < height; ++y)
        {
            for (int x = 0; x < width; ++x)
            {
                // Wrapped sampling: these are tiling textures, and clamping at
                // the border puts a visible seam down every repeat.
                float l00 = Luminance(pixels[Index(x - 1, y - 1, width, height)]);
                float l10 = Luminance(pixels[Index(x, y - 1, width, height)]);
                float l20 = Luminance(pixels[Index(x + 1, y - 1, width, height)]);
                float l01 = Luminance(pixels[Index(x - 1, y, width, height)]);
                float l21 = Luminance(pixels[Index(x + 1, y, width, height)]);
                float l02 = Luminance(pixels[Index(x - 1, y + 1, width, height)]);
                float l12 = Luminance(pixels[Index(x, y + 1, width, height)]);
                float l22 = Luminance(pixels[Index(x + 1, y + 1, width, height)]);

                float dx = (l20 + 2f * l21 + l22) - (l00 + 2f * l01 + l02);
                float dy = (l02 + 2f * l12 + l22) - (l00 + 2f * l10 + l20);

                Vector3 n = new Vector3(-dx * NormalStrength, -dy * NormalStrength, 1f).normalized;

                output[y * width + x] = new Color32(
                    (byte)Mathf.Clamp((n.x * 0.5f + 0.5f) * 255f, 0f, 255f),
                    (byte)Mathf.Clamp((n.y * 0.5f + 0.5f) * 255f, 0f, 255f),
                    (byte)Mathf.Clamp((n.z * 0.5f + 0.5f) * 255f, 0f, 255f),
                    255);
            }
        }

        normal.SetPixels32(output);
        normal.Apply(true, true);
        return normal;
    }

    /// <summary>
    /// HDRP mask map: metallic, occlusion, detail mask, smoothness.
    /// </summary>
    static Texture2D BuildMaskMap(Color32[] pixels, int width, int height,
                                  string name, BFSurfaceType surfaceHint)
    {
        BFSurfaceProfile profile = BFSurfaceProfile.Get(surfaceHint);
        bool allowMetallic = surfaceHint == BFSurfaceType.Metal ||
                             surfaceHint == BFSurfaceType.Glass;

        var mask = new Texture2D(width, height, TextureFormat.RGBA32, true, true)
        {
            name = name + "_bf_mask",
            wrapMode = TextureWrapMode.Repeat,
        };

        var output = new Color32[width * height];

        // Mean luminance over the whole texture: occlusion is defined against
        // the texture's own average, so a uniformly dark asset does not read
        // as entirely occluded.
        float mean = 0f;
        for (int i = 0; i < pixels.Length; ++i) mean += Luminance(pixels[i]);
        mean /= pixels.Length;

        for (int y = 0; y < height; ++y)
        {
            for (int x = 0; x < width; ++x)
            {
                int idx = y * width + x;
                Color32 c = pixels[idx];
                float l = Luminance(c);

                // Local contrast over a small neighbourhood - tight linework
                // means a hard, polished surface.
                float lo = 1f, hi = 0f;
                for (int oy = -1; oy <= 1; ++oy)
                {
                    for (int ox = -1; ox <= 1; ++ox)
                    {
                        float n = Luminance(pixels[Index(x + ox, y + oy, width, height)]);
                        if (n < lo) lo = n;
                        if (n > hi) hi = n;
                    }
                }
                float contrast = Mathf.Clamp01(hi - lo);

                // Occlusion: how much darker than the texture's own average.
                float occlusion = Mathf.Clamp01(1f - Mathf.Max(0f, mean - l) * 1.8f);

                // Smoothness: the surface's baseline, pushed by local contrast.
                float smoothness = Mathf.Clamp01(profile.BaseSmoothness + contrast * 0.45f);

                // Metallic: bright and desaturated, but only where the surface
                // type permits it at all.
                float metallic = 0f;
                if (allowMetallic)
                {
                    float maxc = Mathf.Max(c.r, Mathf.Max(c.g, c.b)) / 255f;
                    float minc = Mathf.Min(c.r, Mathf.Min(c.g, c.b)) / 255f;
                    float saturation = maxc > 0.001f ? (maxc - minc) / maxc : 0f;
                    metallic = Mathf.Clamp01((1f - saturation * 2.2f) * Mathf.Clamp01(l * 1.6f));
                }

                output[idx] = new Color32(
                    (byte)(metallic * 255f),
                    (byte)(occlusion * 255f),
                    128,                              // detail mask: neutral
                    (byte)(smoothness * 255f));
            }
        }

        mask.SetPixels32(output);
        mask.Apply(true, true);
        return mask;
    }

    static int Index(int x, int y, int width, int height)
    {
        // Wrap rather than clamp - see BuildNormal.
        x = ((x % width) + width) % width;
        y = ((y % height) + height) % height;
        return y * width + x;
    }
}
