using System.Collections.Generic;
using UnityEngine;
using LibSWBF2.Wrappers;
using LibSWBF2.Enums;

/// <summary>
/// The original game's AI navigation data, finally used.
///
/// Every SWBF2 map ships a hand-authored "planning graph" (ZeroEditor's
/// PLANNING mode): a set of HUBS (named, positioned, with a radius) joined by
/// CONNECTIONS (arcs). Each arc carries:
///   - EArcFilterFlags: which unit sizes may traverse it
///     (Soldier / Small / Medium / Hover / Large / Huge)
///   - EArcAttributeFlags: OneWay, Jump, JetJump
/// plus per-hub quantized branch weights the shipped AI used to bias routes.
///
/// Maps also ship BARRIERS - oriented boxes marking "AI keep out" volumes -
/// which Lua can toggle at runtime (DisableBarriers / EnableBarriers), and the
/// graph itself can be edited live via BlockPlanningGraphArcs.
///
/// This replaces straight-line movement plus raycast whiskers with real
/// pathfinding over the routes the level designers actually authored, which is
/// why AI can now handle buildings, bridges, canyons and multi-level maps.
///
/// Usage:
///   PhxNavGraph.Instance.FindPath(from, to, PhxNavSize.Soldier, resultList)
/// </summary>
public enum PhxNavSize
{
    Soldier,
    Small,
    Medium,
    Hover,
    Large,
    Huge,
}

public class PhxNavGraph
{
    public class PhxHub
    {
        public string Name;
        public Vector3 Position;
        public float Radius;
        public readonly List<int> ArcIndices = new List<int>();
    }

    public class PhxArc
    {
        public string Name;
        public int From;
        public int To;
        public EArcFilterFlags Filter;
        public EArcAttributeFlags Attributes;
        public float Length;
        public bool Blocked;          // BlockPlanningGraphArcs
    }

    class PhxBarrier
    {
        public string Name;
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 HalfExtents;
        public bool Enabled = true;
    }

    public static PhxNavGraph Instance { get; private set; } = new PhxNavGraph();

    public bool IsLoaded => Hubs.Count > 0;
    public int HubCount => Hubs.Count;
    public int ArcCount => Arcs.Count;

    readonly List<PhxHub> Hubs = new List<PhxHub>();
    readonly List<PhxArc> Arcs = new List<PhxArc>();
    readonly List<PhxBarrier> Barriers = new List<PhxBarrier>();
    readonly Dictionary<string, int> HubsByName = new Dictionary<string, int>();

    // A* scratch, reused to avoid per-query allocation
    float[] GScore;
    float[] FScore;
    int[] CameFrom;
    bool[] Closed;

    public static void Reset()
    {
        Instance = new PhxNavGraph();
    }

    // ------------------------------------------------------------------ load

    /// <summary>Import the planning graph from the world level.</summary>
    public void LoadPlanning(Level worldLevel)
    {
        if (worldLevel == null) return;

        PlanSet[] planSets = worldLevel.Get<PlanSet>();
        if (planSets == null || planSets.Length == 0)
        {
            Debug.Log("[BF3Legacy] No planning graph in this level - AI will fall back to direct steering");
            return;
        }

        foreach (PlanSet set in planSets)
        {
            Hub[] hubs = set.GetHubs();
            Connection[] connections = set.GetConnections();
            if (hubs == null) continue;

            int hubBase = Hubs.Count;

            foreach (Hub h in hubs)
            {
                // world-space data uses the *FromLibWorld conversions (Z flip),
                // matching how regions/instances are imported
                PhxHub hub = new PhxHub
                {
                    Name = h.Name,
                    Position = UnityUtils.Vec3FromLibWorld(h.Position),
                    Radius = h.Radius,
                };
                if (!string.IsNullOrEmpty(hub.Name) && !HubsByName.ContainsKey(hub.Name.ToLowerInvariant()))
                {
                    HubsByName.Add(hub.Name.ToLowerInvariant(), Hubs.Count);
                }
                Hubs.Add(hub);
            }

            if (connections == null) continue;
            foreach (Connection c in connections)
            {
                int from = hubBase + c.Start;
                int to = hubBase + c.End;
                if (from < 0 || from >= Hubs.Count || to < 0 || to >= Hubs.Count) continue;

                AddArc(from, to, c.Name, c.FilterFlags, c.AttributeFlags);

                // arcs are bidirectional unless flagged OneWay
                if ((c.AttributeFlags & EArcAttributeFlags.OneWay) == 0)
                {
                    AddArc(to, from, c.Name, c.FilterFlags, c.AttributeFlags);
                }
            }
        }

        AllocateScratch();
        Debug.Log($"[BF3Legacy] Planning graph loaded: {Hubs.Count} hubs, {Arcs.Count} arcs");
    }

    void AddArc(int from, int to, string name, EArcFilterFlags filter, EArcAttributeFlags attrs)
    {
        PhxArc arc = new PhxArc
        {
            Name = name,
            From = from,
            To = to,
            Filter = filter,
            Attributes = attrs,
            Length = Vector3.Distance(Hubs[from].Position, Hubs[to].Position),
        };
        Hubs[from].ArcIndices.Add(Arcs.Count);
        Arcs.Add(arc);
    }

    /// <summary>Import AI keep-out barriers from a world layer.</summary>
    public void LoadBarriers(World world)
    {
        if (world == null) return;

        Barrier[] barriers = world.GetBarriers();
        if (barriers == null) return;

        foreach (Barrier b in barriers)
        {
            // Size is a half-extent (same convention as regions). Barriers are
            // authored as flat footprints, so give Y generous height to catch
            // ground units regardless of terrain height at that spot.
            Vector3 size = UnityUtils.Vec3FromLibWorld(b.Size);
            Barriers.Add(new PhxBarrier
            {
                Name = b.Name,
                Position = UnityUtils.Vec3FromLibWorld(b.Position),
                Rotation = UnityUtils.QuatFromLibWorld(b.Rotation),
                HalfExtents = new Vector3(Mathf.Abs(size.x),
                                          Mathf.Max(Mathf.Abs(size.y), 25f),
                                          Mathf.Abs(size.z)),
            });
        }

        if (Barriers.Count > 0)
        {
            Debug.Log($"[BF3Legacy] {Barriers.Count} AI barriers loaded");
        }
    }

    void AllocateScratch()
    {
        GScore = new float[Hubs.Count];
        FScore = new float[Hubs.Count];
        CameFrom = new int[Hubs.Count];
        Closed = new bool[Hubs.Count];
    }

    // --------------------------------------------------------- Lua controls

    /// <summary>Lua: BlockPlanningGraphArcs / UnblockPlanningGraphArcs.</summary>
    public void SetHubArcsBlocked(string hubName, bool blocked)
    {
        if (string.IsNullOrEmpty(hubName)) return;
        if (!HubsByName.TryGetValue(hubName.ToLowerInvariant(), out int hubIdx)) return;
        SetHubArcsBlocked(hubIdx, blocked);
    }

    public void SetHubArcsBlocked(int hubIdx, bool blocked)
    {
        if (hubIdx < 0 || hubIdx >= Hubs.Count) return;

        // block arcs leaving this hub, and any arriving at it
        foreach (int a in Hubs[hubIdx].ArcIndices)
        {
            Arcs[a].Blocked = blocked;
        }
        for (int i = 0; i < Arcs.Count; ++i)
        {
            if (Arcs[i].To == hubIdx) Arcs[i].Blocked = blocked;
        }
    }

    /// <summary>Lua: EnableBarriers / DisableBarriers (name may match several).</summary>
    public void SetBarriersEnabled(string barrierName, bool enabled)
    {
        if (string.IsNullOrEmpty(barrierName)) return;
        string want = barrierName.ToLowerInvariant();
        foreach (PhxBarrier b in Barriers)
        {
            if (b.Name != null && b.Name.ToLowerInvariant() == want)
            {
                b.Enabled = enabled;
            }
        }
    }

    // ------------------------------------------------------------- querying

    public bool IsBlockedByBarrier(Vector3 worldPos)
    {
        foreach (PhxBarrier b in Barriers)
        {
            if (!b.Enabled) continue;
            Vector3 local = Quaternion.Inverse(b.Rotation) * (worldPos - b.Position);
            if (Mathf.Abs(local.x) <= b.HalfExtents.x &&
                Mathf.Abs(local.y) <= b.HalfExtents.y &&
                Mathf.Abs(local.z) <= b.HalfExtents.z)
            {
                return true;
            }
        }
        return false;
    }

    public int FindNearestHub(Vector3 position, PhxNavSize size)
    {
        int best = -1;
        float bestDist = float.MaxValue;
        EArcFilterFlags need = ToFilter(size);

        for (int i = 0; i < Hubs.Count; ++i)
        {
            // a hub is only useful if at least one usable arc touches it
            bool usable = false;
            foreach (int a in Hubs[i].ArcIndices)
            {
                if (!Arcs[a].Blocked && (Arcs[a].Filter & need) != 0) { usable = true; break; }
            }
            if (!usable && Hubs[i].ArcIndices.Count > 0) continue;

            float d = (Hubs[i].Position - position).sqrMagnitude;
            if (d < bestDist)
            {
                bestDist = d;
                best = i;
            }
        }
        return best;
    }

    /// <summary>
    /// A* over the planning graph. Returns false if no route exists (caller
    /// should fall back to direct steering). Path is written as world
    /// positions, excluding the start hub.
    /// </summary>
    public bool FindPath(Vector3 from, Vector3 to, PhxNavSize size, List<Vector3> outPath)
    {
        outPath.Clear();
        if (!IsLoaded) return false;

        int start = FindNearestHub(from, size);
        int goal = FindNearestHub(to, size);
        if (start < 0 || goal < 0) return false;
        if (start == goal)
        {
            outPath.Add(to);
            return true;
        }

        EArcFilterFlags need = ToFilter(size);

        for (int i = 0; i < Hubs.Count; ++i)
        {
            GScore[i] = float.MaxValue;
            FScore[i] = float.MaxValue;
            CameFrom[i] = -1;
            Closed[i] = false;
        }

        GScore[start] = 0f;
        FScore[start] = Heuristic(start, goal);

        // small graphs (a few hundred hubs) - linear open-set scan is fine
        // and avoids allocating a heap per query
        while (true)
        {
            int current = -1;
            float bestF = float.MaxValue;
            for (int i = 0; i < Hubs.Count; ++i)
            {
                if (!Closed[i] && FScore[i] < bestF)
                {
                    bestF = FScore[i];
                    current = i;
                }
            }
            if (current < 0) return false;          // exhausted, no route
            if (current == goal) break;

            Closed[current] = true;

            foreach (int arcIdx in Hubs[current].ArcIndices)
            {
                PhxArc arc = Arcs[arcIdx];
                if (arc.Blocked) continue;
                if ((arc.Filter & need) == 0) continue;      // wrong unit size

                int next = arc.To;
                if (Closed[next]) continue;

                // avoid routing through disabled-by-Lua keep-out volumes
                if (IsBlockedByBarrier(Hubs[next].Position)) continue;

                float tentative = GScore[current] + arc.Length;
                if (tentative < GScore[next])
                {
                    CameFrom[next] = current;
                    GScore[next] = tentative;
                    FScore[next] = tentative + Heuristic(next, goal);
                }
            }
        }

        // reconstruct
        List<int> reversed = new List<int>();
        for (int node = goal; node != -1 && node != start; node = CameFrom[node])
        {
            reversed.Add(node);
            if (reversed.Count > Hubs.Count) break;   // cycle guard
        }
        for (int i = reversed.Count - 1; i >= 0; --i)
        {
            outPath.Add(Hubs[reversed[i]].Position);
        }
        outPath.Add(to);
        return outPath.Count > 0;
    }

    float Heuristic(int a, int b)
    {
        return Vector3.Distance(Hubs[a].Position, Hubs[b].Position);
    }

    public static EArcFilterFlags ToFilter(PhxNavSize size)
    {
        switch (size)
        {
            case PhxNavSize.Small: return EArcFilterFlags.Small;
            case PhxNavSize.Medium: return EArcFilterFlags.Medium;
            case PhxNavSize.Hover: return EArcFilterFlags.Hover;
            case PhxNavSize.Large: return EArcFilterFlags.Large;
            case PhxNavSize.Huge: return EArcFilterFlags.Huge;
            default: return EArcFilterFlags.Soldier;
        }
    }

    /// <summary>Map an ODF AISizeType string onto a nav size.</summary>
    public static PhxNavSize SizeFromAIType(string aiSizeType)
    {
        if (string.IsNullOrEmpty(aiSizeType)) return PhxNavSize.Soldier;
        switch (aiSizeType.ToUpperInvariant())
        {
            case "SMALL": return PhxNavSize.Small;
            case "MEDIUM": return PhxNavSize.Medium;
            case "HOVER": return PhxNavSize.Hover;
            case "LARGE": return PhxNavSize.Large;
            case "HUGE": return PhxNavSize.Huge;
            default: return PhxNavSize.Soldier;
        }
    }
}
