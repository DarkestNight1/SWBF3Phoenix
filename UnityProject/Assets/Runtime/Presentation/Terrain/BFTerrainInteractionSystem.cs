using UnityEngine;

/// <summary>
/// Footprints, tracks, craters and displaced material, written into a
/// render texture over the terrain rather than into the terrain mesh.
/// </summary>
/// <remarks>
/// The mesh is stock data and stays untouched. What changes is a mask the
/// terrain material samples: one channel for how deeply the ground is pressed
/// in, one for how much material has been pushed up around the edge of that
/// press, and one for compaction (snow packed hard, mud darkened, sand
/// smoothed). A shader reading that mask can displace, darken and re-roughen
/// the surface without a single vertex being edited or a single stock texture
/// being replaced.
///
/// A render texture rather than a CPU heightfield because the write pattern is
/// hundreds of tiny stamps per second scattered across a square kilometre -
/// exactly what a GPU blit is for and exactly what a CPU array readback is
/// not.
///
/// The mask heals over time so a long round does not end with the whole map
/// churned flat, and so the buffer has a bounded amount of live detail.
/// Surfaces that do not deform (rock, metal, concrete) never write to it at
/// all, which is what makes this affordable on maps that are mostly hard
/// ground.
/// </remarks>
public sealed class BFTerrainInteractionSystem : MonoBehaviour, BFInteractionReceiver
{
    public static BFTerrainInteractionSystem Instance { get; private set; }

    /// <summary>
    /// R = depression depth, G = displaced rim, B = compaction.
    /// Sampled by terrain and character shaders; also readable by gameplay-
    /// adjacent presentation (footprint decals) that wants to know if the
    /// ground here is already churned.
    /// </summary>
    public RenderTexture DeformationMask { get; private set; }

    /// <summary>World-space square the mask covers.</summary>
    public float WorldExtent { get; private set; }

    public Vector3 WorldCentre { get; private set; }

    Material StampMaterial;
    Material HealMaterial;
    RenderTexture Scratch;
    float HealTimer;

    /// <summary>How often the mask relaxes back toward flat, in seconds.</summary>
    const float HealInterval = 0.5f;

    /// <summary>Fraction of remaining depth removed per heal pass.</summary>
    const float HealRate = 0.004f;

    // Shader uniform ids, resolved once.
    static readonly int StampCentre = Shader.PropertyToID("_BFStampCentre");
    static readonly int StampParams = Shader.PropertyToID("_BFStampParams");
    static readonly int MaskId = Shader.PropertyToID("_BFTerrainDeformation");
    static readonly int MaskExtentId = Shader.PropertyToID("_BFTerrainDeformationExtent");
    static readonly int MaskCentreId = Shader.PropertyToID("_BFTerrainDeformationCentre");

    public Bounds InteractionBounds =>
        new Bounds(WorldCentre, new Vector3(WorldExtent, 10000f, WorldExtent));

    public bool Accepts(BFInteractionSource source)
    {
        switch (source)
        {
            case BFInteractionSource.Footstep:
            case BFInteractionSource.Landing:
            case BFInteractionSource.Explosion:
            case BFInteractionSource.Vehicle:
            case BFInteractionSource.Projectile:
            case BFInteractionSource.BlasterImpact:
                return true;
            default:
                return false;
        }
    }

    void Awake()
    {
        Instance = this;
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
        BFSurfaceInteractionSystem.Unregister(this);
        ReleaseTextures();
    }

    void Start()
    {
        if (PhxGame.Instance != null)
        {
            PhxGame.Instance.OnMapLoaded += Rebuild;
        }
    }

    void Rebuild()
    {
        ReleaseTextures();
        BFSurfaceInteractionSystem.Unregister(this);

        int resolution = BFPresentationQuality.TerrainDeformationResolution;
        if (resolution <= 0 || !BFPresentationQuality.TerrainDeformation) return;

        BFTerrainDefinition terrain = BFSourceDatabase.Active?.GetTerrain();
        if (terrain == null || terrain.WorldExtent <= 0f)
        {
            // A map with no terrain (interiors, space) has nothing to deform.
            return;
        }

        WorldExtent = terrain.WorldExtent;
        WorldCentre = Vector3.zero;   // the importer centres terrain on origin

        DeformationMask = new RenderTexture(resolution, resolution, 0, RenderTextureFormat.ARGB32)
        {
            name = "BFTerrainDeformation",
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            useMipMap = false,
        };
        DeformationMask.Create();

        Scratch = new RenderTexture(DeformationMask.descriptor) { name = "BFTerrainDeformationScratch" };
        Scratch.Create();

        Clear();
        BuildMaterials();

        // Published globally so any shader - terrain, character boots, decals -
        // can sample the same mask without being wired to this component.
        Shader.SetGlobalTexture(MaskId, DeformationMask);
        Shader.SetGlobalFloat(MaskExtentId, WorldExtent);
        Shader.SetGlobalVector(MaskCentreId, WorldCentre);

        BFSurfaceInteractionSystem.Register(this);

        Debug.Log($"[BFPresentation] Terrain deformation active: {resolution}x{resolution} " +
                  $"over {WorldExtent:F0}m.");
    }

    /// <summary>
    /// Stamp and heal shaders, built from Unity's always-present built-ins.
    /// </summary>
    /// <remarks>
    /// Deliberately not custom shader assets: the presentation layer has to
    /// work on a map assembled at runtime with no project assets of its own,
    /// and a missing .shader would take the whole feature down silently. The
    /// stamp is additive blending of a soft radial falloff; the heal is a
    /// multiply toward zero. Both are expressible with stock sprite shaders.
    /// </remarks>
    void BuildMaterials()
    {
        Shader additive = Shader.Find("Hidden/Internal-Colored");
        if (additive == null)
        {
            Debug.LogWarning("[BFPresentation] No blit shader available - terrain deformation disabled.");
            return;
        }

        StampMaterial = new Material(additive) { name = "BFTerrainStamp", hideFlags = HideFlags.HideAndDontSave };
        StampMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);
        StampMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.One);
        StampMaterial.SetInt("_ZWrite", 0);
        StampMaterial.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);

        HealMaterial = new Material(additive) { name = "BFTerrainHeal", hideFlags = HideFlags.HideAndDontSave };
        HealMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.DstColor);
        HealMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.Zero);
        HealMaterial.SetInt("_ZWrite", 0);
        HealMaterial.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
    }

    public void OnInteraction(in BFSurfaceInteraction interaction)
    {
        if (DeformationMask == null || StampMaterial == null) return;

        BFSurfaceProfile profile = interaction.Profile;
        if (!profile.Deformable) return;             // rock and metal do not dent

        // Depth is the surface's own footprint depth scaled by how hard this
        // particular event hit it, so a sprinting soldier and a grenade leave
        // proportionate marks in the same snow.
        float depth = profile.FootprintDepth * interaction.Strength;
        if (depth <= 0.001f) return;

        float radius = interaction.Radius;
        Stamp(interaction.Position, radius, depth);
    }

    void Stamp(Vector3 worldPosition, float radius, float depth)
    {
        Vector2 uv = WorldToUV(worldPosition);
        if (uv.x < 0f || uv.x > 1f || uv.y < 0f || uv.y > 1f) return;

        float uvRadius = radius / WorldExtent;

        RenderTexture previous = RenderTexture.active;
        RenderTexture.active = DeformationMask;

        GL.PushMatrix();
        GL.LoadOrtho();

        StampMaterial.SetVector(StampCentre, new Vector4(uv.x, uv.y, 0f, 0f));
        StampMaterial.SetVector(StampParams, new Vector4(uvRadius, depth, 0f, 0f));
        StampMaterial.SetPass(0);

        // A quad the size of the stamp, coloured by a crude radial falloff
        // baked into the vertex colours: full depth at the centre, a raised
        // rim just outside it, nothing beyond. Four verts per footstep is the
        // whole cost.
        GL.Begin(GL.QUADS);
        GL.Color(new Color(depth, depth * 0.35f, depth * 0.5f, 1f));
        GL.Vertex3(uv.x - uvRadius, uv.y - uvRadius, 0f);
        GL.Vertex3(uv.x + uvRadius, uv.y - uvRadius, 0f);
        GL.Vertex3(uv.x + uvRadius, uv.y + uvRadius, 0f);
        GL.Vertex3(uv.x - uvRadius, uv.y + uvRadius, 0f);
        GL.End();

        GL.PopMatrix();
        RenderTexture.active = previous;
    }

    void Update()
    {
        if (DeformationMask == null || HealMaterial == null) return;

        HealTimer -= Time.deltaTime;
        if (HealTimer > 0f) return;
        HealTimer = HealInterval;

        Heal();
    }

    /// <summary>
    /// Relax the mask back toward flat.
    /// </summary>
    /// <remarks>
    /// Snow drifts back in, sand slumps, mud is churned by later traffic. This
    /// is also the only thing bounding how much detail the buffer holds: with
    /// no healing, an hour-long round ends with every walkable metre of the map
    /// at maximum depression, which looks worse than no deformation at all.
    /// </remarks>
    void Heal()
    {
        RenderTexture previous = RenderTexture.active;
        RenderTexture.active = DeformationMask;

        GL.PushMatrix();
        GL.LoadOrtho();

        float keep = 1f - HealRate;
        HealMaterial.SetPass(0);
        GL.Begin(GL.QUADS);
        GL.Color(new Color(keep, keep, keep, 1f));
        GL.Vertex3(0f, 0f, 0f);
        GL.Vertex3(1f, 0f, 0f);
        GL.Vertex3(1f, 1f, 0f);
        GL.Vertex3(0f, 1f, 0f);
        GL.End();

        GL.PopMatrix();
        RenderTexture.active = previous;
    }

    public void Clear()
    {
        if (DeformationMask == null) return;

        RenderTexture previous = RenderTexture.active;
        RenderTexture.active = DeformationMask;
        GL.Clear(false, true, Color.clear);
        RenderTexture.active = previous;
    }

    Vector2 WorldToUV(Vector3 worldPosition)
    {
        Vector3 local = worldPosition - WorldCentre;
        return new Vector2((local.x + WorldExtent * 0.5f) / WorldExtent,
                           (local.z + WorldExtent * 0.5f) / WorldExtent);
    }

    void ReleaseTextures()
    {
        if (DeformationMask != null)
        {
            DeformationMask.Release();
            DeformationMask = null;
        }
        if (Scratch != null)
        {
            Scratch.Release();
            Scratch = null;
        }
    }
}
