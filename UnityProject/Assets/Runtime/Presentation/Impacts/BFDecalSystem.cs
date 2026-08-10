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
        public float SpawnedAt;
        public float Lifetime;
        public float PeakOpacity;
        public bool InUse;
    }

    readonly List<PooledDecal> Pool = new List<PooledDecal>();
    Transform Root;
    Material DecalMaterial;

    /// <summary>Fraction of a decal's life spent fading out at the end.</summary>
    const float FadeTail = 0.25f;

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
        return material;
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
        decal.Projector.material = DecalMaterial;
        decal.Projector.fadeFactor = opacity;

        decal.PeakOpacity = opacity;
        decal.SpawnedAt = Time.time;
        decal.Lifetime = lifetime > 0f ? lifetime : DefaultLifetime();
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
            case BFQualityTier.Medium: return 25f;
            case BFQualityTier.High: return 60f;
            case BFQualityTier.Ultra: return 150f;
            default: return 0f;
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
        projector.material = DecalMaterial;
        projector.drawDistance = 90f;
        projector.fadeScale = 0.9f;

        return new PooledDecal { Object = obj, Projector = projector };
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
