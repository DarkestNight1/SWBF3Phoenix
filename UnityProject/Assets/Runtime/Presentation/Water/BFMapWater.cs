using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Builds the water body for maps whose water never survives the import.
/// </summary>
/// <remarks>
/// <see cref="BFWaterSystem"/> takes water over rather than creating it: it
/// scans a loaded map for a renderer that reads as water and replaces how that
/// renderer is shaded. That is right for maps that bring their own water plane,
/// and it finds nothing on Kashyyyk.
///
/// Probing kas2.lvl says why, and rules out the two obvious explanations:
///
/// <list type="bullet">
/// <item>The water is not a placed object. There is no water model and no world
/// instance whose name or class reads as water - only the texture set
/// (kas2_water, plus water_bumpmap_0..15 and water_normalmap_0..15, an animated
/// sixteen-frame surface).</item>
/// <item>The bed is not painted with a water terrain layer either. kas2's three
/// layers are kas2_main_1..3, and no stock map has a water-named layer at
/// all - so a level cannot be derived from the terrain painting.</item>
/// </list>
///
/// The texture exists and nothing draws it, which is what terrain-section water
/// looks like from this side: SWBF2's terrain renderer holds the water height
/// and the water texture, and the <c>tern</c> INFO block LibSWBF2 parses stops
/// at grid and texture counts. The height is not lost in translation, it is
/// never read, and it is not present anywhere else in the munged level. So it
/// cannot be computed here and has to be stated.
///
/// Stated from measurement rather than taste. Probing kas2's terrain: the
/// height range is -0.36 to 145.08, and over half of every vertex on the map
/// sits at exactly 0.00 (p40, p50 = 0.00; the ground only starts climbing at
/// p55). That flat plane at zero, covering the majority of a map that rises to
/// 145 m elsewhere, is the seabed the village stands over, and the water sits
/// just above it.
///
/// The footprint is measured rather than stated, because it can be: the water
/// covers the flat plane, and the flat plane is found by asking the terrain
/// collider for its height. What is deliberately NOT done is inferring the
/// water level from that plane - "largest flat area low in the range" also
/// describes Tatooine, Endor, Yavin, Naboo and Rhen Var, whose terrain is
/// equally dominated by a single height and equally dry, and a rule that
/// floods five maps to fill one is not a rule.
/// </remarks>
public sealed class BFMapWater : MonoBehaviour
{
    /// <summary>A map whose water level the munged level does not carry.</summary>
    struct WaterLevel
    {
        public string ScriptPrefix;     // map, by mission-script prefix
        public float SurfaceHeight;     // world Y of the still surface
        public float BedTolerance;      // how far above the surface still counts as bed
        public string Why;
    }

    static readonly WaterLevel[] Levels =
    {
        new WaterLevel
        {
            ScriptPrefix = "kas",

            // Above the bed, and with enough depth to read as water rather than
            // as a wet floor. The seabed is authored at exactly 0.00 and the
            // terrain's own minimum is -0.36, so anything at or near zero
            // z-fights the ground it is supposed to cover.
            //
            // The ceiling on this is the shoreline, not taste: the bank starts
            // climbing at the 55th percentile of terrain height, which measures
            // 0.72m. A surface above that stops being a sea and starts being a
            // flood, so this sits below it with room to spare.
            SurfaceHeight = 0.45f,

            // The bed is flat to within a few centimetres over half the map, so
            // the footprint only needs to admit ground at about water level;
            // anything higher is the bank and the shoreline belongs there.
            BedTolerance = 0.6f,

            Why = "terrain-section water: kas2 ships the kas2_water texture set and " +
                  "a seabed authored flat at y=0, but no water geometry, and the tern " +
                  "chunk LibSWBF2 reads carries no water height",
        },
    };

    /// <summary>Grid resolution of the footprint search, per axis.</summary>
    /// <remarks>
    /// This finds a region rather than shading one, so it wants to be dense
    /// enough to trace a shoreline and cheap enough to run once at load.
    /// 128 x 128 is sixteen thousand raycasts against one collider.
    /// </remarks>
    const int SampleGrid = 128;

    /// <summary>Least submerged area worth building a surface for, in samples.</summary>
    const int MinimumSamples = 24;

    void Start()
    {
        if (PhxGame.Instance != null) PhxGame.Instance.OnMapLoaded += Build;
    }

    void OnDestroy()
    {
        if (PhxGame.Instance != null) PhxGame.Instance.OnMapLoaded -= Build;
    }

    void Build()
    {
        // Only ever adds water that is missing. A map that imported its own
        // surface already has one, and a second plane would z-fight it.
        if (BFWaterSystem.Surfaces.Count > 0) return;

        string world = PhxGame.GetEnvironment()?.GetWorldName();
        if (string.IsNullOrEmpty(world)) return;

        for (int i = 0; i < Levels.Length; ++i)
        {
            WaterLevel level = Levels[i];
            if (!world.StartsWith(level.ScriptPrefix, System.StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!MeasureFootprint(level, out Bounds footprint, out int samples))
            {
                Debug.LogWarning($"[BFPresentation] '{world}' has a water level of " +
                                 $"{level.SurfaceHeight}m configured, but only {samples} " +
                                 "terrain sample(s) sit at or below it - no water built. " +
                                 "Either the level is wrong for this map or the terrain " +
                                 "did not import.");
                return;
            }

            Create(footprint, level.SurfaceHeight, world, level.Why);
            return;
        }
    }

    /// <summary>
    /// The area of terrain lying at or below the water level.
    /// </summary>
    bool MeasureFootprint(WaterLevel level, out Bounds footprint, out int samples)
    {
        footprint = default;
        samples = 0;

        if (!TerrainFootprint(out Bounds terrain)) return false;

        float ceiling = level.SurfaceHeight + level.BedTolerance;
        float stepX = terrain.size.x / SampleGrid;
        float stepZ = terrain.size.z / SampleGrid;
        bool any = false;

        for (int gx = 0; gx < SampleGrid; ++gx)
        {
            for (int gz = 0; gz < SampleGrid; ++gz)
            {
                float x = terrain.min.x + (gx + 0.5f) * stepX;
                float z = terrain.min.z + (gz + 0.5f) * stepZ;

                if (!BedHeightAt(x, z, terrain, out float bed)) continue;
                if (bed > ceiling) continue;

                ++samples;

                Vector3 point = new Vector3(x, level.SurfaceHeight, z);
                if (!any)
                {
                    footprint = new Bounds(point, Vector3.zero);
                    any = true;
                }
                else
                {
                    footprint.Encapsulate(point);
                }
            }
        }

        if (!any || samples < MinimumSamples) return false;

        // Grow by one grid step. The footprint is the centre of every sample
        // that hit, so its edge sits half a step inside the real shoreline, and
        // water that stops short of the bank reads as a hole.
        footprint.Expand(new Vector3(stepX, 0f, stepZ));
        return true;
    }

    /// <summary>Terrain height under a column, from the collider.</summary>
    static bool BedHeightAt(float x, float z, Bounds terrain, out float height)
    {
        height = 0f;

        // From above the terrain, straight down. Terrain lives on TerrainAll,
        // which is what keeps this from measuring a crate standing in a river.
        Vector3 from = new Vector3(x, terrain.max.y + 10f, z);
        int layer = LayerMask.NameToLayer("TerrainAll");
        if (layer < 0) return false;

        if (!Physics.Raycast(from, Vector3.down, out RaycastHit hit,
                             terrain.size.y + 20f, 1 << layer, QueryTriggerInteraction.Ignore))
        {
            return false;
        }

        height = hit.point.y;
        return true;
    }

    /// <summary>
    /// Switch the built surface to transparent so the bed shows through it.
    /// </summary>
    /// <remarks>
    /// GameObject.CreatePrimitive hands back HDRP's default Lit material, which
    /// is opaque - so the surface read as a sheet of wet concrete laid over the
    /// bay rather than as water, and none of BFWaterSurface's depth colouring
    /// could show because there was nothing to see through.
    ///
    /// HDRP does not switch surface type from a property alone: _SurfaceType
    /// drives the inspector, but what actually moves the material into the
    /// transparent pass is the render queue and the blend state, and the
    /// _SURFACE_TYPE_TRANSPARENT keyword is what compiles the blending in. All
    /// four have to be set together, which is the part that makes this look
    /// like more work than "set alpha".
    /// </remarks>
    static void MakeTransparent(GameObject surface)
    {
        Renderer renderer = surface.GetComponent<Renderer>();
        if (renderer == null) return;

        Material mat = renderer.material;
        if (mat == null) return;

        mat.SetFloat("_SurfaceType", 1f);                 // 0 opaque, 1 transparent
        mat.SetFloat("_BlendMode", 0f);                   // alpha
        mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
        mat.SetFloat("_ZWrite", 0f);
        mat.SetFloat("_AlphaCutoffEnable", 0f);

        mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        mat.EnableKeyword("_BLENDMODE_ALPHA");
        mat.DisableKeyword("_ALPHATEST_ON");

        mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;

        // Alpha low enough to read as water. BFWaterSurface owns the colour
        // itself and overwrites _BaseColor on Start - this only has to make
        // sure the material it writes into is one that can show through.
        Color c = mat.HasProperty("_BaseColor") ? mat.GetColor("_BaseColor") : Color.white;
        c.a = 0.55f;
        mat.SetColor("_BaseColor", c);

        // Smooth, because a rough transparent surface reads as frosted glass.
        if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.95f);
    }

    /// <summary>Bounds of the imported terrain, or false if there is none.</summary>
    static bool TerrainFootprint(out Bounds bounds)
    {
        bounds = default;

        GameObject terrain = GameObject.Find("Terrain");
        if (terrain == null) return false;

        Renderer renderer = terrain.GetComponent<Renderer>();
        if (renderer == null) return false;

        bounds = renderer.bounds;
        return bounds.size.x > 1f && bounds.size.z > 1f;
    }

    /// <summary>
    /// Lay a surface over the footprint and let <see cref="BFWaterSystem"/>
    /// take it from there.
    /// </summary>
    /// <remarks>
    /// Deliberately only geometry and a name. Depth colour, smoothness, the
    /// planar reflection, ripples and the underwater volume all belong to
    /// <see cref="BFWaterSurface"/>, and it attaches to anything whose name
    /// reads as water - so naming this correctly is the whole handoff. Shading
    /// it here as well would be the second implementation of water in a
    /// codebase that only wants one.
    /// </remarks>
    void Create(Bounds footprint, float surfaceHeight, string world, string why)
    {
        GameObject surface = GameObject.CreatePrimitive(PrimitiveType.Quad);

        // "water" is the keyword BFSurfaceQuery classifies on and BFWaterSystem
        // discovers by. The name is load-bearing.
        surface.name = "BFMapWater_water";
        surface.transform.SetParent(transform, false);
        surface.transform.position = new Vector3(footprint.center.x, surfaceHeight,
                                                 footprint.center.z);
        // A quad faces +Z; the surface has to face up.
        surface.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
        surface.transform.localScale = new Vector3(footprint.size.x, footprint.size.z, 1f);

        // No collider. Water is waded into, and the interaction system reports
        // entry through the renderer's bounds - a solid plane across the bay
        // would have soldiers walking on it instead.
        Collider collider = surface.GetComponent<Collider>();
        if (collider != null) Destroy(collider);

        MakeTransparent(surface);

        Debug.Log($"[BFPresentation] Water built on '{world}' at y={surfaceHeight:0.00}, " +
                  $"{footprint.size.x:0}x{footprint.size.z:0}m - {why}.");

        // Discovery has already run for this load by the time this fires, so
        // hand the body over directly rather than waiting for the next map.
        BFWaterSystem.Adopt(surface);
    }
}
