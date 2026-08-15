using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

/// <summary>
/// Scorch marks, craters, footprints and grime, projected onto whatever is
/// already there.
/// </summary>
/// <remarks>
/// HDRP decal projectors, pooled and budgeted. Decals are the right tool
/// precisely because they leave the stock assets alone: a blaster mark on a
/// wall is a projector in front of the wall, not an edit to the wall's
/// twenty-year-old texture. Nothing here modifies imported material data.
///
/// The budget is the design. A firefight will ask for far more marks than a
/// scene can afford, so the pool is fixed and the oldest decal is recycled -
/// which also gives the fade for free, since a decal approaching recycling is
/// already fading out.
/// </remarks>
public sealed class BFDecalSystem : MonoBehaviour
{
    public static BFDecalSystem Instance { get; private set; }

    sealed class PooledDecal
    {
        public GameObject Object;
        public DecalProjector Projector;
        public Material Material;
        public float SpawnedAt;
        public float Lifetime;
        public float PeakOpacity;
        public bool InUse;
    }

    readonly List<PooledDecal> Pool = new List<PooledDecal>();
    Transform Root;
    Material DecalMaterial;

    /// <summary>Fraction of a decal's life spent fading out at the end.</summary>
    /// <remarks>
    /// Half, so a mark spends as long going as it does sitting there. A short
    /// tail on a long life is a mark that looks permanent and then vanishes
    /// while you are looking at it; a long one is a mark you stop noticing.
    /// </remarks>
    const float FadeTail = 0.5f;

    /// <summary>
    /// Shortest a mark may last, whatever the budget says.
    /// </summary>
    /// <remarks>
    /// Battle damage that outlives the firefight that caused it is the point of
    /// having it, so a minute is the floor rather than something only the top
    /// tier gets. The budget still decides how MANY marks exist at once, which
    /// is where the actual cost is - a decal already placed costs the same
    /// whether it has four seconds left or ninety.
    /// </remarks>
    const float MinimumLifetime = 60f;

    void Awake()
    {
        Instance = this;
        Root = new GameObject("BFDecals").transform;
        Root.SetParent(transform, false);
        DecalMaterial = BuildDecalMaterial();
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    /// <summary>
    /// A decal material built in code from the HDRP decal shader.
    /// </summary>
    /// <remarks>
    /// Built rather than referenced so the presentation layer has no asset
    /// dependencies to wire up in a scene - it has to work on a map loaded
    /// entirely at runtime from the user's own game files.
    ///
    /// Returns null when the shader is unavailable, which disables decals
    /// rather than spawning untextured magenta squares over the level.
    /// </remarks>
    static Material BuildDecalMaterial()
    {
        Shader shader = Shader.Find("HDRP/Decal");
        if (shader == null)
        {
            Debug.LogWarning("[BFPresentation] HDRP/Decal shader unavailable - decals disabled.");
            return null;
        }

        var material = new Material(shader) { name = "BFDecal" };
        material.enableInstancing = true;

        // Two things a code-built HDRP decal material needs that the inspector
        // would have done, and without which this drew nothing at all.
        //
        // The keyword first. HDRP's decal shader branches on
        // _MATERIAL_AFFECTS_ALBEDO, which the material editor sets from the
        // "Affect BaseColor" toggle; a material made with `new Material(shader)`
        // has the float property at its default but not the keyword, so the
        // albedo path is compiled out and the projector contributes nothing to
        // the frame. That is the whole reason no scorch mark has ever appeared:
        // the pool, the budget, the fade and every caller were working, and the
        // one thing being drawn was a decal with its colour output disabled.
        material.SetFloat("_AffectAlbedo", 1f);
        material.EnableKeyword("_MATERIAL_AFFECTS_ALBEDO");

        // Normals and smoothness stay off. A scorch is soot lying on a surface,
        // not a dent in it, and leaving those channels on makes every mark read
        // as a shallow crater under a grazing light.
        material.SetFloat("_AffectNormal", 0f);
        material.SetFloat("_AffectSmoothness", 0f);
        material.SetFloat("_AffectMetal", 0f);
        material.SetFloat("_AffectAO", 0f);

        // Then a shape. The mask texture is what makes a decal a mark rather
        // than a square: with no base map HDRP samples white, so every hit
        // would have been a hard-edged opaque tile the size of the projector.
        material.SetTexture("_BaseColorMap", BuildScorchTexture());

        return material;
    }

    /// <summary>
    /// The scorch mark itself - a soft-edged smudge, generated rather than
    /// shipped.
    /// </summary>
    /// <remarks>
    /// Procedural because a texture asset is one more thing to import, keep in
    /// the right folder and reference by path, for an image that is a radial
    /// falloff with some noise in it. 64x64 is plenty for something never seen
    /// larger than half a metre and always over an existing surface.
    ///
    /// Only the alpha channel carries the shape. RGB is left white so the
    /// per-decal colour multiplies cleanly through it, which is what lets one
    /// texture serve soot on stone, dark wet grey on snow and pale dust on
    /// sand.
    /// </remarks>
    static Texture2D BuildScorchTexture()
    {
        const int Size = 64;
        var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, true, true)
        {
            name = "BFScorch",
            wrapMode = TextureWrapMode.Clamp,
        };

        var pixels = new Color32[Size * Size];
        // Fixed seed: the mark should be the same every run, and a decal that
        // reshuffles its own noise between sessions is not reproducible when
        // something about it looks wrong.
        var rng = new System.Random(20051101);

        for (int y = 0; y < Size; ++y)
        {
            for (int x = 0; x < Size; ++x)
            {
                float dx = (x + 0.5f) / Size * 2f - 1f;
                float dy = (y + 0.5f) / Size * 2f - 1f;
                float d = Mathf.Sqrt(dx * dx + dy * dy);

                // Dense core, long soft edge - smoothstep from the centre out,
                // squared so the falloff stays weighted toward the middle the
                // way a burn is.
                float a = 1f - Mathf.SmoothStep(0.15f, 1f, d);
                a *= a;

                // Break the perfect circle up. Without this a wall taking
                // several hits shows a row of identical dots, and the random
                // roll on placement cannot hide a shape that is symmetric.
                a *= 0.75f + 0.25f * (float)rng.NextDouble();

                pixels[y * Size + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(a) * 255f));
            }
        }

        tex.SetPixels32(pixels);
        tex.Apply(true, true);
        return tex;
    }

    /// <summary>
    /// Project a mark onto the world.
    /// </summary>
    /// <param name="normal">Surface normal; the decal faces into it.</param>
    /// <param name="color">Tint - the surface profile's scorch colour.</param>
    /// <param name="size">Width and height in metres.</param>
    /// <param name="lifetime">Seconds before it is gone. 0 uses the default.</param>
    public static void Place(Vector3 position, Vector3 normal, Color color,
                             float size, float opacity = 1f, float lifetime = 0f)
    {
        if (!BFPresentationQuality.Decals || Instance == null) return;
        if (color.a <= 0f) return;          // surfaces that take no marks

        Instance.PlaceInternal(position, normal, color, size, opacity, lifetime);
    }

    void PlaceInternal(Vector3 position, Vector3 normal, Color color,
                       float size, float opacity, float lifetime)
    {
        if (DecalMaterial == null) return;

        PooledDecal decal = Acquire();
        if (decal == null) return;

        // Rotated to face along the surface normal, with a random roll so a
        // wall taking twenty hits does not show twenty identical marks.
        Quaternion rotation = Quaternion.LookRotation(-normal, Vector3.up) *
                              Quaternion.Euler(0f, 0f, Random.Range(0f, 360f));

        decal.Object.transform.SetPositionAndRotation(position + normal * 0.05f, rotation);
        decal.Object.SetActive(true);

        // Depth is generous relative to width so the decal survives being
        // projected onto a surface that is not quite flat.
        decal.Projector.size = new Vector3(size, size, Mathf.Max(0.4f, size));
        // The colour the caller asked for, which until now was accepted and
        // then thrown away - every mark on every surface was drawn with the
        // shared template's untouched white.
        decal.Material.SetColor("_BaseColor", color);
        decal.Projector.material = decal.Material;
        decal.Projector.fadeFactor = opacity;

        decal.PeakOpacity = opacity;
        decal.SpawnedAt = Time.time;
        decal.Lifetime = Mathf.Max(lifetime > 0f ? lifetime : DefaultLifetime(),
                                   MinimumLifetime);
        decal.InUse = true;
    }

    /// <summary>
    /// How long a mark lasts, from the budget rather than a constant.
    /// </summary>
    /// <remarks>
    /// A large budget can afford to keep battle damage around long enough for
    /// a position to visibly accumulate a history of being fought over, which
    /// is a real part of how a Battlefront map reads late in a round. A small
    /// budget cannot, and recycling constantly would make marks flicker, so it
    /// keeps them briefly and honestly instead.
    /// </remarks>
    static float DefaultLifetime()
    {
        switch (BFPresentationQuality.Tier)
        {
            case BFQualityTier.Medium: return MinimumLifetime;
            case BFQualityTier.High: return 90f;
            case BFQualityTier.Ultra: return 150f;

            // Low, and anything unrecognised. This used to return zero, which
            // is not "the default lifetime" but "already expired" - Place
            // treats a zero argument as "use the default" and then Update
            // recycled the decal on the frame it appeared. A mark that exists
            // for one frame is worse than no mark, because it reads as a
            // flicker rather than as a feature that is off.
            default: return MinimumLifetime;
        }
    }

    void Update()
    {
        float now = Time.time;

        for (int i = 0; i < Pool.Count; ++i)
        {
            PooledDecal decal = Pool[i];
            if (!decal.InUse) continue;

            float age = now - decal.SpawnedAt;
            if (age >= decal.Lifetime)
            {
                Release(decal);
                continue;
            }

            float fadeStart = decal.Lifetime * (1f - FadeTail);
            if (age <= fadeStart) continue;

            float t = 1f - (age - fadeStart) / (decal.Lifetime * FadeTail);
            decal.Projector.fadeFactor = decal.PeakOpacity * t;
        }
    }

    PooledDecal Acquire()
    {
        int budget = BFPresentationQuality.DecalBudget;
        if (budget <= 0) return null;

        for (int i = 0; i < Pool.Count; ++i)
        {
            if (!Pool[i].InUse) return Pool[i];
        }

        if (Pool.Count < budget)
        {
            PooledDecal created = Create();
            Pool.Add(created);
            return created;
        }

        PooledDecal oldest = null;
        float earliest = float.MaxValue;
        for (int i = 0; i < Pool.Count; ++i)
        {
            if (Pool[i].SpawnedAt >= earliest) continue;
            earliest = Pool[i].SpawnedAt;
            oldest = Pool[i];
        }
        return oldest;
    }

    PooledDecal Create()
    {
        var obj = new GameObject("Decal");
        obj.transform.SetParent(Root, false);
        obj.SetActive(false);

        DecalProjector projector = obj.AddComponent<DecalProjector>();

        // Its own material instance, not the shared template.
        //
        // The colour a caller passes is the whole point of the call - soot on
        // stone, wet grey on snow, pale dust on sand - and a DecalProjector
        // takes no MaterialPropertyBlock, so the only place per-decal colour
        // can live is a per-decal material. One clone per pooled decal is
        // bounded by the budget and created once.
        Material instance = new Material(DecalMaterial) { name = "BFDecal (instance)" };

        projector.material = instance;
        projector.drawDistance = 90f;
        projector.fadeScale = 0.9f;

        return new PooledDecal { Object = obj, Projector = projector, Material = instance };
    }

    void Release(PooledDecal decal)
    {
        decal.InUse = false;
        decal.Object.SetActive(false);
    }

    /// <summary>Remove every mark - map change.</summary>
    public static void Clear()
    {
        if (Instance == null) return;

        for (int i = 0; i < Instance.Pool.Count; ++i)
        {
            Instance.Release(Instance.Pool[i]);
        }
    }

    public static int ActiveCount
    {
        get
        {
            if (Instance == null) return 0;

            int count = 0;
            for (int i = 0; i < Instance.Pool.Count; ++i)
            {
                if (Instance.Pool[i].InUse) ++count;
            }
            return count;
        }
    }
}
