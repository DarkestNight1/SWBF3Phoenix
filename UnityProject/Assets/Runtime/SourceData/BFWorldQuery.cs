using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Everything a system needs to know about a point in the world, answered
/// once.
/// </summary>
/// <remarks>
/// This is the seam the project was missing. Graphics, AI, weapons, vehicles,
/// terrain, water, destruction and audio all ask the same small set of
/// questions about the same world - what surface is this, what region am I in,
/// how high is the ground, am I indoors - and before this each of them
/// answered independently. That is how a footstep can decide it is on snow
/// while the impact system decides the same square metre is rock: two
/// implementations, two answers, no way to notice they disagree.
///
/// A single query layer makes those answers definitionally consistent, and it
/// also gives the caching somewhere to live. A firefight asks about the same
/// few hundred colliders thousands of times a second; a shared layer caches
/// once instead of every consumer inventing its own.
/// </remarks>
public struct BFWorldContext
{
    public Vector3 Position;

    public BFSurfaceType Surface;
    public BFSurfaceProfile SurfaceProfile;

    /// <summary>Ground height beneath the position, or the position's own y.</summary>
    public float GroundHeight;
    public Vector3 GroundNormal;
    public bool HasGround;

    /// <summary>Water surface here, or null.</summary>
    public BFWaterSurface Water;

    /// <summary>Depth below the water surface; 0 when not submerged.</summary>
    public float WaterDepth;

    /// <summary>Authored trigger regions containing this position.</summary>
    public IReadOnlyList<PhxRegion> Regions;

    /// <summary>
    /// True when there is geometry overhead within a short distance - the
    /// cheap, honest version of "indoors".
    /// </summary>
    public bool Indoors;

    /// <summary>Source record of the object here, when there is one.</summary>
    public BFSourceRef Source;

    public bool IsUnderwater => Water != null && WaterDepth > 0f;
}

/// <summary>
/// The world query layer. Every question about a position goes through here.
/// </summary>
public static class BFWorldQuery
{
    /// <summary>How far up to look for a ceiling when deciding "indoors".</summary>
    const float IndoorProbeHeight = 30f;

    /// <summary>How far down to look for ground.</summary>
    const float GroundProbeDepth = 200f;

    static readonly List<PhxRegion> RegionScratch = new List<PhxRegion>();
    static readonly Collider[] OverlapScratch = new Collider[32];

    // Indoor-ness is a raycast and changes only when something moves, so it is
    // cached on a coarse spatial grid rather than recomputed per query. A
    // soldier walking a corridor asks this every footstep, every impact and
    // every audio decision.
    struct IndoorSample
    {
        public bool Indoors;
        public float SampledAt;
    }

    static readonly Dictionary<long, IndoorSample> IndoorCache = new Dictionary<long, IndoorSample>();

    /// <summary>Grid cell size for the indoor cache, in metres.</summary>
    const float IndoorCellSize = 4f;

    /// <summary>How long an indoor sample stays valid.</summary>
    const float IndoorCacheSeconds = 10f;

    public static void Reset()
    {
        IndoorCache.Clear();
        RegionScratch.Clear();
    }

    // ------------------------------------------------------------ full query

    /// <summary>
    /// Everything about a position. Callers that need one fact should use the
    /// individual queries below - this does all the work.
    /// </summary>
    public static BFWorldContext Describe(Vector3 position, bool includeRegions = true)
    {
        var context = new BFWorldContext
        {
            Position = position,
            GroundHeight = position.y,
            GroundNormal = Vector3.up,
            Surface = BFSurfaceQuery.MapDefault,
        };

        if (Physics.Raycast(position + Vector3.up * 2f, Vector3.down, out RaycastHit hit,
                            GroundProbeDepth, PhxLayers.SoldierGround, QueryTriggerInteraction.Ignore))
        {
            context.HasGround = true;
            context.GroundHeight = hit.point.y;
            context.GroundNormal = hit.normal;
            context.Surface = BFSurfaceQuery.Resolve(hit);
            context.Source = BFSourceLink.Find(hit.collider.gameObject);
        }

        context.SurfaceProfile = BFSurfaceProfile.Get(context.Surface);

        context.Water = BFWaterSystem.BodyAt(position);
        context.WaterDepth = context.Water != null ? context.Water.DepthAt(position) : 0f;

        context.Indoors = IsIndoors(position);

        if (includeRegions)
        {
            context.Regions = RegionsAt(position);
        }

        return context;
    }

    // ------------------------------------------------------- single queries

    /// <summary>Surface at a position, probing downward for the ground.</summary>
    public static BFSurfaceType GetSurface(Vector3 position)
    {
        if (Physics.Raycast(position + Vector3.up * 2f, Vector3.down, out RaycastHit hit,
                            GroundProbeDepth, PhxLayers.SoldierGround, QueryTriggerInteraction.Ignore))
        {
            return BFSurfaceQuery.Resolve(hit);
        }
        return BFSurfaceQuery.MapDefault;
    }

    /// <summary>Surface of a specific hit - the cheapest form, no extra cast.</summary>
    public static BFSurfaceType GetSurface(RaycastHit hit) => BFSurfaceQuery.Resolve(hit);

    /// <summary>Ground height beneath a position, or the position's own y.</summary>
    public static float GetGroundHeight(Vector3 position)
    {
        return Physics.Raycast(position + Vector3.up * 2f, Vector3.down, out RaycastHit hit,
                               GroundProbeDepth, PhxLayers.SoldierGround, QueryTriggerInteraction.Ignore)
            ? hit.point.y
            : position.y;
    }

    /// <summary>Water surface height at a position, or float.MinValue.</summary>
    public static float GetWaterLevel(Vector3 position)
    {
        BFWaterSurface water = BFWaterSystem.BodyAt(position);
        return water != null ? water.SurfaceHeight : float.MinValue;
    }

    /// <summary>The source record of whatever object is at a position.</summary>
    public static BFSourceRef GetSourceObject(Vector3 position, float radius = 1f)
    {
        int count = Physics.OverlapSphereNonAlloc(position, radius, OverlapScratch,
                                                  ~0, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < count; ++i)
        {
            BFSourceRef source = BFSourceLink.Find(OverlapScratch[i].gameObject);
            if (source != null) return source;
        }
        return null;
    }

    /// <summary>The lighting profile in force. Constant per map, but asked for by position.</summary>
    public static BFEnvironmentLightingProfile GetLightingProfile(Vector3 position)
    {
        return BFLightingDirector.Active;
    }

    /// <summary>
    /// Authored regions containing a position.
    /// </summary>
    /// <remarks>
    /// The returned list is reused between calls, so it is valid only until
    /// the next query. Callers that need to keep it must copy - which is the
    /// right trade here, because the alternative is an allocation on a call
    /// that AI makes for every soldier several times a second.
    /// </remarks>
    public static IReadOnlyList<PhxRegion> RegionsAt(Vector3 position)
    {
        RegionScratch.Clear();

        int count = Physics.OverlapSphereNonAlloc(position, 0.1f, OverlapScratch,
                                                  ~0, QueryTriggerInteraction.Collide);
        for (int i = 0; i < count; ++i)
        {
            PhxRegion region = OverlapScratch[i].GetComponent<PhxRegion>();
            if (region != null) RegionScratch.Add(region);
        }
        return RegionScratch;
    }

    /// <summary>
    /// Whether a position is under cover from above.
    /// </summary>
    /// <remarks>
    /// A ceiling probe, not a portal graph. The imported maps have no indoor
    /// volumes to read - the .lgt and .sky data describe the map as a whole -
    /// so "is there geometry over my head" is the only answer available from
    /// the data that exists, and it is the right answer often enough for what
    /// depends on it: whether rain reaches you, whether snow settles, whether
    /// audio should sound enclosed, whether AI should expect air support.
    ///
    /// Cached on a coarse grid because it is a raycast asked by many systems
    /// about slowly-changing positions.
    /// </remarks>
    public static bool IsIndoors(Vector3 position)
    {
        long key = CellKey(position);
        float now = Time.time;

        if (IndoorCache.TryGetValue(key, out IndoorSample cached) &&
            now - cached.SampledAt < IndoorCacheSeconds)
        {
            return cached.Indoors;
        }

        bool indoors = Physics.Raycast(position + Vector3.up * 0.5f, Vector3.up,
                                       IndoorProbeHeight, PhxLayers.SoldierGround,
                                       QueryTriggerInteraction.Ignore);

        IndoorCache[key] = new IndoorSample { Indoors = indoors, SampledAt = now };

        // The cache is per-map and grid-bounded, but a very large map walked
        // end to end still grows it; drop it wholesale rather than paying for
        // per-entry eviction on a value this cheap to recompute.
        if (IndoorCache.Count > 4096) IndoorCache.Clear();

        return indoors;
    }

    static long CellKey(Vector3 position)
    {
        long x = (long)Mathf.Floor(position.x / IndoorCellSize);
        long y = (long)Mathf.Floor(position.y / IndoorCellSize);
        long z = (long)Mathf.Floor(position.z / IndoorCellSize);
        return (x & 0x1FFFFF) | ((y & 0x1FFFFF) << 21) | ((z & 0x1FFFFF) << 42);
    }
}
