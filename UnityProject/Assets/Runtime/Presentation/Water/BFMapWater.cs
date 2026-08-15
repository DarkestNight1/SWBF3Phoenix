using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Builds the water body for maps whose water never survives the import.
/// </summary>
/// <remarks>
/// <see cref="BFWaterSystem"/> takes water over rather than creating it: it
/// scans the loaded map for a renderer that reads as water and replaces how
/// that renderer is shaded. That is the right design when the map brings its
/// own water plane, and it finds nothing at all on Kashyyyk, because Kashyyyk's
/// water is not a placed object.
///
/// SWBF2 draws that water from the terrain, not from world geometry, and the
/// terrain chunk this project reads does not carry it. The <c>tern</c> INFO
/// block LibSWBF2 parses stops at grid and texture counts - there is no water
/// height, no water texture and no per-patch water flag in the struct - so no
/// amount of work further down the import can recover a surface that was never
/// read. Nothing arrives, so nothing is discovered, and the village stands over
/// a dry hole.
///
/// So the surface is built here instead, and the height is measured rather than
/// typed in. The terrain still says where the water goes even though it no
/// longer says how high: the artists painted the bed with a water texture, and
/// <see cref="BFTerrainSurfaceMap"/> already classifies every terrain layer by
/// texture name for footsteps and impacts. The painted region is the water's
/// footprint, and the shoreline - the highest bed still painted as water - is
/// its surface height, because that is what a shoreline is.
///
/// Measuring beats a per-map constant here. A typed height is one number that
/// is right on one map at one scale and silently wrong everywhere else, and
/// there is no way to check it from the Unity side without the game installed;
/// a measured one is derived from the same data the ground is drawn from, and
/// comes out right on any map whose bed is painted, Kashyyyk included.
/// <see cref="Overrides"/> exists for maps where it is not, and is empty until
/// one turns up - an empty table is the honest state, not a placeholder.
/// </remarks>
public sealed class BFMapWater : MonoBehaviour
{
    /// <summary>A map whose water level cannot be measured off its own terrain.</summary>
    struct WaterLevel
    {
        public string ScriptPrefix;     // map, by mission-script prefix
        public float SurfaceHeight;     // world Y of the still surface
        public string Why;
    }

    /// <summary>
    /// Empty on purpose. Every map whose bed carries a water texture is handled
    /// by measurement; this is for one that does not, and adding speculative
    /// entries would put unverifiable numbers in front of the measurement that
    /// would otherwise have been right.
    /// </summary>
    static readonly WaterLevel[] Overrides = { };

    /// <summary>
    /// Grid resolution of the search for painted water, per axis.
    /// </summary>
    /// <remarks>
    /// This walks the terrain looking for a region, not shading it, so it wants
    /// to be dense enough to find a river and cheap enough to run once at load.
    /// 128 x 128 is sixteen thousand samples of an in-memory byte array.
    /// </remarks>
    const int SampleGrid = 128;

    /// <summary>
    /// Where in the sorted bed heights the surface is taken from.
    /// </summary>
    /// <remarks>
    /// Not the maximum. A blend map is painted by hand and its edges feather,
    /// so the single highest texel that still counts as water is usually one
    /// stray sample partway up a bank, and taking it floods the map. The upper
    /// decile is the shoreline proper: past the bed, short of the outliers.
    /// </remarks>
    const float ShorelinePercentile = 0.9f;

    /// <summary>Least painted area worth building a surface for, in samples.</summary>
    /// <remarks>
    /// A handful of scattered water texels is a texture blend on a riverbed
    /// that has no water in it, not a lake. Building a plane for those puts a
    /// sheet of water across a dry map.
    /// </remarks>
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
        // surface already has one, and a second plane at a measured height
        // would z-fight the authored one.
        if (BFWaterSystem.Surfaces.Count > 0) return;

        string world = PhxGame.GetEnvironment()?.GetWorldName();
        if (string.IsNullOrEmpty(world)) return;

        for (int i = 0; i < Overrides.Length; ++i)
        {
            WaterLevel o = Overrides[i];
            if (!world.StartsWith(o.ScriptPrefix, System.StringComparison.OrdinalIgnoreCase)) continue;

            if (TerrainFootprint(out Bounds full))
            {
                Create(full, o.SurfaceHeight, world, $"authored level - {o.Why}");
            }
            return;
        }

        if (!MeasurePaintedWater(out Bounds footprint, out float height)) return;

        Create(footprint, height, world, "measured from the terrain's own water painting");
    }

    /// <summary>
    /// Find the painted water region and the height of its shoreline.
    /// </summary>
    bool MeasurePaintedWater(out Bounds footprint, out float surfaceHeight)
    {
        footprint = default;
        surfaceHeight = 0f;

        if (!BFTerrainSurfaceMap.IsLoaded) return false;
        if (!TerrainFootprint(out Bounds terrain)) return false;

        var bedHeights = new List<float>();
        bool any = false;

        float stepX = terrain.size.x / SampleGrid;
        float stepZ = terrain.size.z / SampleGrid;

        for (int gx = 0; gx < SampleGrid; ++gx)
        {
            for (int gz = 0; gz < SampleGrid; ++gz)
            {
                float x = terrain.min.x + (gx + 0.5f) * stepX;
                float z = terrain.min.z + (gz + 0.5f) * stepZ;

                // The sample map is indexed in world space, so the probe does
                // not need the terrain's height to ask what is painted there.
                Vector3 column = new Vector3(x, terrain.center.y, z);
                if (BFTerrainSurfaceMap.Sample(column) != BFSurfaceType.Water) continue;

                // It does need the height to place the surface, and only the
                // collider knows that - the blend map carries no elevation.
                if (!BedHeightAt(x, z, terrain, out float bed)) continue;

                bedHeights.Add(bed);

                Vector3 point = new Vector3(x, bed, z);
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

        if (!any || bedHeights.Count < MinimumSamples)
        {
            return false;
        }

        bedHeights.Sort();
        int index = Mathf.Clamp(Mathf.RoundToInt((bedHeights.Count - 1) * ShorelinePercentile),
                                0, bedHeights.Count - 1);
        surfaceHeight = bedHeights[index];

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
        int mask = 1 << LayerMask.NameToLayer("TerrainAll");

        if (!Physics.Raycast(from, Vector3.down, out RaycastHit hit,
                             terrain.size.y + 20f, mask, QueryTriggerInteraction.Ignore))
        {
            return false;
        }

        height = hit.point.y;
        return true;
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
    /// <see cref="BFWaterSurface"/>, and it attaches itself to anything whose
    /// name reads as water - so naming this correctly is the whole handoff.
    /// Shading it here as well would be the second implementation of water in
    /// a codebase that only wants one.
    /// </remarks>
    void Create(Bounds footprint, float surfaceHeight, string world, string provenance)
    {
        GameObject surface = GameObject.CreatePrimitive(PrimitiveType.Quad);

        // "water" is the keyword BFSurfaceQuery classifies on, and BFWaterSystem
        // discovers by that classification. The name is load-bearing.
        surface.name = "BFMapWater_water";
        surface.transform.SetParent(transform, false);
        surface.transform.position = new Vector3(footprint.center.x, surfaceHeight,
                                                 footprint.center.z);
        // A quad faces +Z; the surface has to face up.
        surface.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
        surface.transform.localScale = new Vector3(footprint.size.x, footprint.size.z, 1f);

        // No collider. Water is something soldiers wade into, and the surface
        // interaction system reports entry through its own bounds - a solid
        // plane across the map would have them walking on it instead.
        Collider collider = surface.GetComponent<Collider>();
        if (collider != null) Destroy(collider);

        Debug.Log($"[BFPresentation] Water built on '{world}' at y={surfaceHeight:0.00}, " +
                  $"{footprint.size.x:0}x{footprint.size.z:0}m - {provenance}.");

        // The system has already run its own discovery for this load by now,
        // so hand it this one directly rather than waiting for the next map.
        BFWaterSystem.Adopt(surface);
    }
}
