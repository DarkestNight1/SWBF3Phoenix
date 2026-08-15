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

    // Shared imported material -> our upscaled copy. A null value is a
    // remembered "nothing here worth upscaling", so the second renderer
    // using that material skips the check entirely.
    readonly Dictionary<Material, Material> Instanced = new Dictionary<Material, Material>();

    // Set from PhxGame.OnMapLoaded. The pass used to guess with a fixed
    // one-second delay and usually walked an empty scene.
    bool MapLoaded;

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
        // MaterialLoader binds glow through _EmissionMap (builtin name)
        // as well as _EmissiveColorMap, and writes the base map via
        // Material.mainTexture - which resolves to _BaseColorMap on
        // HDRP/Lit and _MainTex elsewhere. Both are already listed
        // above; _EmissionMap was the one genuinely missing.
        ("_EmissionMap", false),
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

        // The real load-complete signal, replacing a fixed delay that was
        // always a guess. Subscribed once here rather than per map.
        if (PhxGame.Instance != null)
        {
            PhxGame.Instance.OnMapLoaded += OnMapLoadedHandler;
        }
    }

    /// <summary>
    /// A named handler, because a lambda cannot be detached.
    /// </summary>
    /// <remarks>
    /// This was subscribed as <c>() =&gt; MapLoaded = true</c>. Nothing holds a
    /// reference to that delegate, so there is no way to remove it - the
    /// subscription outlives the component unconditionally, and every later map
    /// load calls into a destroyed MonoBehaviour. Of the seven systems here
    /// that were missing an unsubscribe, this was the only one that could not
    /// simply have one added.
    /// </remarks>
    void OnMapLoadedHandler()
    {
        MapLoaded = true;
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

        // Our instanced copies referenced the old map's textures. Destroy
        // them rather than leaking one material per distinct source
        // material, every map load.
        foreach (Material m in Instanced.Values)
        {
            if (m != null) Destroy(m);
        }
        Instanced.Clear();
        MapLoaded = false;
        BudgetUsedBytes = 0;

        if (scene != null && PhxBF3.Config.UpscaleTextures)
        {
            StartCoroutine(UpscaleSceneTextures());
        }
    }

    /// <remarks>
    /// This is a second writer of material texture state, and it writes to
    /// <c>sharedMaterials</c> - the imported asset itself, not a per-renderer
    /// copy - a second after the map loads. MaterialLoader is the first
    /// writer, at import; nothing reconciles the two, and because the target
    /// is shared, a swap made while walking one renderer silently applies to
    /// every other renderer using that material.
    ///
    /// It is also, on the evidence, doing nothing: the last run reported
    /// "0 textures upscaled (0 slots inspected)". A pass that inspects zero
    /// slots has found no material with any of its TextureSlots, which means
    /// the property names it looks for do not match what MaterialLoader binds.
    /// So this is currently a dormant hazard rather than an active one - it
    /// costs a full scene walk and delivers nothing.
    ///
    /// Left in place rather than removed because the intent is sound and the
    /// fix is to reconcile the slot names, but it must not be woken up before
    /// it either takes ownership from MaterialLoader or works on instanced
    /// copies. Waking it as-is would mutate shared imported assets from a
    /// coroutine, which is the same class of bug as the lighting writers.
    /// </remarks>
    /// <summary>
    /// Walk the loaded map's materials and swap in upscaled textures.
    /// </summary>
    /// <remarks>
    /// Four things had to be true before this could be woken up safely, and
    /// none of them were:
    ///
    /// 1. It ran on a fixed one-second delay, which is nowhere near a real map
    ///    import, so it usually walked an empty scene. It now runs off the
    ///    actual load-complete signal.
    /// 2. FindObjectsOfType skipped inactive objects, and plenty of map
    ///    geometry is inactive while loading finishes.
    /// 3. Its slot list did not match what MaterialLoader binds: the importer
    ///    writes the base map through Material.mainTexture and glow through
    ///    _EmissionMap, neither of which was looked for. That is why it kept
    ///    reporting "0 slots inspected".
    /// 4. It wrote to sharedMaterials - the imported asset itself - so a swap
    ///    made while walking one renderer silently applied everywhere, and
    ///    fought MaterialLoader for ownership.
    ///
    /// (4) is fixed by instancing once per shared material and giving every
    /// renderer that referenced the original the same instance. That keeps one
    /// material per distinct source material, so batching survives, while the
    /// imported asset is left alone.
    /// </remarks>
    IEnumerator UpscaleSceneTextures()
    {
        // Wait for the map to actually finish importing. The old fixed delay
        // was a guess, and usually a wrong one.
        float timeout = Time.realtimeSinceStartup + 120f;
        while (!MapLoaded && Time.realtimeSinceStartup < timeout)
        {
            yield return null;
        }
        if (!MapLoaded) yield break;

        // One more frame so the last import batch has its renderers enabled.
        yield return null;

        long budgetBytes = (long)PhxBF3.Config.UpscaleBudgetMB * 1024L * 1024L;
        int processed = 0, upscaled = 0, thisFrame = 0, instanced = 0;

        // Include inactive: map geometry is frequently still being enabled.
        Renderer[] renderers = FindObjectsOfType<Renderer>(true);

        foreach (Renderer r in renderers)
        {
            if (r == null) continue;

            Material[] shared = r.sharedMaterials;
            if (shared == null || shared.Length == 0) continue;

            Material[] replacement = null;

            for (int slot = 0; slot < shared.Length; ++slot)
            {
                Material source = shared[slot];
                if (source == null) continue;

                // Already instanced this source material for an earlier
                // renderer - reuse it so they keep sharing one material.
                if (Instanced.TryGetValue(source, out Material existing))
                {
                    if (existing != null)
                    {
                        replacement = replacement ?? (Material[])shared.Clone();
                        replacement[slot] = existing;
                    }
                    continue;
                }

                // Decide whether this material has anything worth upscaling
                // BEFORE instancing it - instancing every material in the map
                // would multiply draw-call state for no benefit.
                if (!MaterialHasUpscalable(source, budgetBytes))
                {
                    // Remember the miss so the next renderer using it skips
                    // the whole check.
                    Instanced[source] = null;
                    continue;
                }

                Material copy = new Material(source);
                copy.name = source.name + " (upscaled)";
                Instanced[source] = copy;
                ++instanced;

                foreach ((string prop, bool isNormal) in TextureSlots)
                {
                    if (!copy.HasProperty(prop)) continue;

                    Texture src = copy.GetTexture(prop);
                    if (src == null || Rejected.Contains(src)) continue;

                    processed++;

                    if (Cache.TryGetValue(src, out RenderTexture cached))
                    {
                        copy.SetTexture(prop, cached);
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
                        copy.SetTexture(prop, result);
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

                replacement = replacement ?? (Material[])shared.Clone();
                replacement[slot] = copy;
            }

            // Assign once per renderer rather than per slot: every write to
            // .materials reallocates the array.
            if (replacement != null && r != null)
            {
                r.sharedMaterials = replacement;
            }
        }

        Debug.Log($"[BF3Legacy] Texture upscale pass: {upscaled} textures upscaled " +
                  $"({processed} slots inspected, {instanced} material(s) instanced, " +
                  $"{BudgetUsedBytes / (1024 * 1024)} MB / " +
                  $"{PhxBF3.Config.UpscaleBudgetMB} MB budget used)");
    }

    /// <summary>
    /// Whether a material holds at least one texture this pass would replace.
    /// </summary>
    /// <remarks>
    /// Checked before instancing so materials that would gain nothing keep
    /// pointing at the shared imported asset.
    /// </remarks>
    bool MaterialHasUpscalable(Material mat, long budgetBytes)
    {
        foreach ((string prop, bool _) in TextureSlots)
        {
            if (!mat.HasProperty(prop)) continue;

            Texture src = mat.GetTexture(prop);
            if (src == null) continue;

            if (Cache.ContainsKey(src)) return true;
            if (Rejected.Contains(src)) continue;
            if (ShouldUpscale(src)) return true;
        }
        return false;
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
        if (PhxGame.Instance != null) PhxGame.Instance.OnMapLoaded -= OnMapLoadedHandler;
        foreach (RenderTexture rt in Cache.Values)
        {
            if (rt != null) rt.Release();
        }
        Cache.Clear();
        Rejected.Clear();

        // Our instanced copies referenced the old map's textures. Destroy
        // them rather than leaking one material per distinct source
        // material, every map load.
        foreach (Material m in Instanced.Values)
        {
            if (m != null) Destroy(m);
        }
        Instanced.Clear();
        MapLoaded = false;
    }
}
