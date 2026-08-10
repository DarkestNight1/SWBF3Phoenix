using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Runtime texture upscaling for the original game's 2005-era art.
///
/// SWBF2 textures are typically 128-512px; stretched across a 4K screen they
/// read as soft and blocky. This upscales them on the GPU at load time -
/// Catmull-Rom bicubic reconstruction followed by contrast-adaptive
/// sharpening (see PhxTextureUpscale.shader) - and swaps the result into the
/// materials. No source assets are modified or redistributed; this is a
/// display-time enhancement of textures the user's own game files provide.
///
/// Design notes:
///  - Results stay as RenderTextures with generated mips: no GPU->CPU
///    readback, so upscaling a texture costs a couple of blits, not a stall.
///  - Work is amortized across frames (UpscalesPerFrame) so map load doesn't
///    hitch.
///  - A VRAM budget caps total added memory; once hit, remaining textures are
///    left at native resolution rather than risking an allocation failure.
///  - Small textures (UI, lightmaps flagged by name) and already-large ones
///    are skipped - upscaling them wastes memory for no visible gain.
/// </summary>
public class PhxTextureUpscaler : MonoBehaviour
{
    public const string ShaderName = "BF3Legacy/TextureUpscale";

    // Only upscale textures within this size window
    const int MinSourceSize = 32;
    const int MaxSourceSize = 1024;

    // Never produce anything larger than this
    const int MaxOutputSize = 4096;

    const int UpscalesPerFrame = 4;

    Material UpscaleMat;
    PhxScene ActiveScene;
    long BudgetUsedBytes;

    readonly Dictionary<Texture, RenderTexture> Cache = new Dictionary<Texture, RenderTexture>();
    readonly HashSet<Texture> Rejected = new HashSet<Texture>();

    // Material texture slots worth upscaling, and whether each is a normal map
    static readonly (string prop, bool isNormal)[] TextureSlots =
    {
        ("_BaseColorMap", false),
        ("_MainTex", false),
        ("_NormalMap", true),
        ("_BumpMap", true),
        ("_MaskMap", false),
        ("_EmissiveColorMap", false),
        ("_DetailMap", false),
    };


    void Start()
    {
        Shader shader = Shader.Find(ShaderName);
        if (shader == null)
        {
            Debug.LogWarning($"[BF3Legacy] Upscale shader '{ShaderName}' not found - " +
                             "texture upscaling disabled. (Is it in a Resources/ or " +
                             "Always Included Shaders list for builds?)");
            enabled = false;
            return;
        }
        UpscaleMat = new Material(shader);
    }

    void Update()
    {
        PhxScene scene = PhxGame.GetScene();
        if (scene == ActiveScene) return;
        ActiveScene = scene;

        // New map: the old map's source textures are gone, so the cache keys
        // are dead and their RenderTextures pure VRAM waste. Not releasing
        // them also left BudgetUsedBytes accumulated across loads, which
        // silently exhausted the budget after a map change or two.
        StopAllCoroutines();
        foreach (RenderTexture rt in Cache.Values)
        {
            if (rt != null) rt.Release();
        }
        Cache.Clear();
        Rejected.Clear();
        BudgetUsedBytes = 0;

        if (scene != null && PhxBF3.Config.UpscaleTextures)
        {
            StartCoroutine(UpscaleSceneTextures());
        }
    }

    IEnumerator UpscaleSceneTextures()
    {
        // let the map finish importing before we walk its materials
        yield return new WaitForSeconds(1f);

        long budgetBytes = (long)PhxBF3.Config.UpscaleBudgetMB * 1024L * 1024L;
        int processed = 0, upscaled = 0, thisFrame = 0;

        Renderer[] renderers = FindObjectsOfType<Renderer>();
        foreach (Renderer r in renderers)
        {
            if (r == null) continue;

            foreach (Material mat in r.sharedMaterials)
            {
                if (mat == null) continue;

                foreach ((string prop, bool isNormal) in TextureSlots)
                {
                    if (!mat.HasProperty(prop)) continue;

                    Texture src = mat.GetTexture(prop);
                    if (src == null || Rejected.Contains(src)) continue;

                    processed++;

                    if (Cache.TryGetValue(src, out RenderTexture cached))
                    {
                        mat.SetTexture(prop, cached);
                        continue;
                    }

                    if (!ShouldUpscale(src))
                    {
                        Rejected.Add(src);
                        continue;
                    }

                    int factor = PickFactor(src);
                    long cost = EstimateBytes(src.width * factor, src.height * factor);
                    if (BudgetUsedBytes + cost > budgetBytes)
                    {
                        Rejected.Add(src);
                        continue;
                    }

                    RenderTexture result = Upscale(src, factor, isNormal);
                    if (result != null)
                    {
                        Cache[src] = result;
                        mat.SetTexture(prop, result);
                        BudgetUsedBytes += cost;
                        upscaled++;
                    }
                    else
                    {
                        Rejected.Add(src);
                    }

                    if (++thisFrame >= UpscalesPerFrame)
                    {
                        thisFrame = 0;
                        yield return null;
                    }
                }
            }
        }

        Debug.Log($"[BF3Legacy] Texture upscale pass: {upscaled} textures upscaled " +
                  $"({processed} slots inspected, {BudgetUsedBytes / (1024 * 1024)} MB / " +
                  $"{PhxBF3.Config.UpscaleBudgetMB} MB budget used)");
    }

    bool ShouldUpscale(Texture src)
    {
        if (src is RenderTexture) return false;          // already ours
        if (src.width < MinSourceSize || src.height < MinSourceSize) return false;
        if (src.width > MaxSourceSize || src.height > MaxSourceSize) return false;

        // skip things that gain nothing from magnification
        string n = src.name.ToLowerInvariant();
        if (n.Contains("lightmap") || n.Contains("_lm") || n.Contains("shadow")) return false;

        return true;
    }

    int PickFactor(Texture src)
    {
        int largest = Mathf.Max(src.width, src.height);
        int factor = PhxBF3.Config.UpscaleFactor;

        // clamp so we never exceed the output ceiling
        while (factor > 1 && largest * factor > MaxOutputSize)
        {
            factor /= 2;
        }
        return Mathf.Max(factor, 1);
    }

    static long EstimateBytes(int w, int h)
    {
        // RGBA32 + ~33% for the mip chain
        return (long)(w * h * 4 * 1.34f);
    }

    RenderTexture Upscale(Texture src, int factor, bool isNormal)
    {
        if (factor <= 1) return null;

        int w = src.width * factor;
        int h = src.height * factor;

        RenderTextureDescriptor desc = new RenderTextureDescriptor(w, h)
        {
            colorFormat = RenderTextureFormat.ARGB32,
            depthBufferBits = 0,
            useMipMap = true,
            autoGenerateMips = false,
            sRGB = !isNormal,
            msaaSamples = 1,
            dimension = UnityEngine.Rendering.TextureDimension.Tex2D,
        };

        RenderTexture result = new RenderTexture(desc);
        result.name = $"{src.name}_x{factor}";
        result.filterMode = FilterMode.Trilinear;
        result.anisoLevel = 8;
        result.wrapMode = (src is Texture2D t2d) ? t2d.wrapMode : TextureWrapMode.Repeat;
        if (!result.Create())
        {
            Object.Destroy(result);
            return null;
        }

        UpscaleMat.SetFloat("_IsNormalMap", isNormal ? 1f : 0f);
        UpscaleMat.SetFloat("_Sharpness", PhxBF3.Config.UpscaleSharpness);

        // pass 0: bicubic reconstruction
        RenderTexture intermediate = RenderTexture.GetTemporary(desc);
        Graphics.Blit(src, intermediate, UpscaleMat, 0);

        // pass 1: adaptive sharpen (skipped for normal maps)
        if (isNormal)
        {
            Graphics.Blit(intermediate, result);
        }
        else
        {
            Graphics.Blit(intermediate, result, UpscaleMat, 1);
        }
        RenderTexture.ReleaseTemporary(intermediate);

        result.GenerateMips();
        return result;
    }

    void OnDestroy()
    {
        foreach (RenderTexture rt in Cache.Values)
        {
            if (rt != null) rt.Release();
        }
        Cache.Clear();
        if (UpscaleMat != null) Destroy(UpscaleMat);
    }
}
