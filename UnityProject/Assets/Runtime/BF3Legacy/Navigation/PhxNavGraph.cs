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

/// <summary>
/// What a unit can do on an arc, beyond simply fitting down it.
/// </summary>
/// <remarks>
/// Arcs carry <see cref="EArcAttributeFlags"/> saying that traversing them
/// requires a jump or a jet-jump, and until now nothing read them: every unit
/// was routed over every arc its size allowed. A rifleman handed a route
/// across a jet-jump gap walks to the edge, cannot cross, and stands there -
/// which reads as broken pathfinding rather than as the unit being unable to
/// make the jump. Declaring what a unit can do lets those arcs be excluded at
/// planning time so it gets a route it can actually walk.
/// </remarks>
[System.Flags]
public enum PhxNavCapabilities
{
    None = 0,
    Jump = 1,
    JetJump = 2,

    /// <summary>Infantry: can hop a low obstacle, cannot jet.</summary>
    Infantry = Jump,

    /// <summary>Jet troopers and anything that flies over a gap.</summary>
    JetInfantry = Jump | JetJump,
}

public class PhxNavGraph
{
    public class PhxHub
    {
        public string Name;
        public Vector3 Position;
        public float Radius;
        public readonly List<int> ArcIndices = new List<int>();

        /// <summary>The authored record this hub projects, kept for weights.</summary>
        public BFPlanningHubDefinition Source;
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

        /// <summary>
        /// Index of the authored connection this arc came from. One authored
        /// connection becomes two runtime arcs unless it is OneWay, so this is
        /// not the arc's own index.
        /// </summary>
        public int SourceIndex = -1;

        /// <summary>
        /// Extra traversal cost from runtime conditions - danger, crowding,
        /// recent deaths. Always >= 0 and expressed in metres so it composes
        /// with Length without a separate scale.
        /// </summary>
        public float DynamicCost;
    }

    class PhxBarrier
    {
        public string Name;
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 HalfExtents;
        public bool Enabled = true;

        // Per the mod tools docs, a barrier carries the SAME size filter set
        // as planning arcs (SOLDIER / HOVER / SMALL / MEDIUM / HUGE): "each
        // barrier has a set of filters that determine what AI types can pass
        // through them". A set bit therefore means that size is BLOCKED.
        //
        // A flag of 0 carries no filter information; we treat that as
        // "blocks everything", which is the conservative reading and matches
        // how a barrier with no filters behaves in the editor.
        public uint Flag;

        public bool Blocks(EArcFilterFlags size)
        {
            // A barrier with no filter bits carries no information about what
            // it stops. Treating that as "blocks everything" was the
            // conservative reading, but conservative in the wrong direction:
            // the failure mode is an AI that cannot move at all, which is worse
            // than one that walks somewhere it shouldn't.
            if (Flag == 0) return false;
            return ((EArcFilterFlags)Flag & size) != 0;
        }
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

    /// <summary>
    /// Build the runtime graph from the captured planning data.
    /// </summary>
    /// <remarks>
    /// Reads the semantic source database rather than the native wrappers on
    /// purpose. The database is captured once per load and is the record every
    /// other consumer (validation, tooling, mod diffing) works from; a second
    /// independent walk of the wrappers here would be a second place for the
    /// coordinate conventions and the hub-index base arithmetic to drift.
    /// </remarks>
    public void LoadPlanning(BFSourceDatabase source)
    {
        if (source == null) return;

        IReadOnlyList<BFPlanningHubDefinition> hubs = source.PlanningHubs;
        if (hubs.Count == 0)
        {
            Debug.Log("[BF3Legacy] No planning graph in this level - AI will fall back to direct steering");
            return;
        }

        foreach (BFPlanningHubDefinition h in hubs)
        {
            PhxHub hub = new PhxHub
            {
                Name = h.Name,
                Position = h.Position,
                Radius = h.Radius,
                Source = h,
            };
            if (!string.IsNullOrEmpty(hub.Name) && !HubsByName.ContainsKey(hub.Name.ToLowerInvariant()))
            {
                HubsByName.Add(hub.Name.ToLowerInvariant(), Hubs.Count);
            }
            Hubs.Add(hub);
            source.MarkImported(h.Source);
        }

        foreach (BFPlanningArcDefinition c in source.PlanningArcs)
        {
            if (c.StartHub < 0 || c.StartHub >= Hubs.Count ||
                c.EndHub < 0 || c.EndHub >= Hubs.Count)
            {
                continue;
            }

            EArcFilterFlags filter = (EArcFilterFlags)c.FilterFlags;
            EArcAttributeFlags attrs = (EArcAttributeFlags)c.AttributeFlags;

            int sourceIndex = c.Source == null ? -1 : c.Source.Ordinal;
            AddArc(c.StartHub, c.EndHub, c.Name, filter, attrs, sourceIndex);

            // arcs are bidirectional unless flagged OneWay
            if ((attrs & EArcAttributeFlags.OneWay) == 0)
            {
                AddArc(c.EndHub, c.StartHub, c.Name, filter, attrs, sourceIndex);
            }
            source.MarkImported(c.Source);
        }

        AllocateScratch();
        Debug.Log($"[BF3Legacy] Planning graph loaded: {Hubs.Count} hubs, {Arcs.Count} arcs");
    }

    void AddArc(int from, int to, string name, EArcFilterFlags filter, EArcAttributeFlags attrs, int sourceIndex)
    {
        PhxArc arc = new PhxArc
        {
            Name = name,
            From = from,
            To = to,
            Filter = filter,
            Attributes = attrs,
            Length = Vector3.Distance(Hubs[from].Position, Hubs[to].Position),
            SourceIndex = sourceIndex,
        };
        Hubs[from].ArcIndices.Add(Arcs.Count);
        Arcs.Add(arc);
    }

    /// <summary>Import AI keep-out barriers from a captured world layer.</summary>
    public void LoadBarriers(BFWorldDefinition world)
    {
        // Called once per world layer, and most layers of a map contribute
        // none - so report only what this layer added, next to the new total.
        // Logging the running total unconditionally printed the same "328 AI
        // barriers loaded" line once for every layer.
        if (world == null || world.Barriers.Count == 0) return;

        foreach (BFBarrierDefinition b in world.Barriers)
        {
            // Size is a half-extent (same convention as regions).
            //
            // The Y extent used to be forced to a minimum of 25m "to catch
            // ground units regardless of terrain height". That turns every
            // authored barrier into a 50m-tall slab, and on a map like
            // Coruscant - 328 barriers over a 155-hub graph - the inflated
            // volumes swallow the whole planning graph. Use the authored size.
            Barriers.Add(new PhxBarrier
            {
                Name = b.Name,
                Position = b.Position,
                Rotation = b.Rotation,
                HalfExtents = b.HalfExtents,
                Flag = b.Flags,
            });
            BFSourceDatabase.Active.MarkImported(b.Source);
        }

        Debug.Log($"[BF3Legacy] {world.Barriers.Count} AI barriers from layer " +
                  $"'{world.Name}' ({Barriers.Count} total)");
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

    // ----------------------------------------------------- designer weights

    // Reused per A* expansion so a path query allocates nothing.
    readonly Dictionary<int, float> BranchScratch = new Dictionary<int, float>();

    /// <summary>
    /// Which of the five authored planning layers a unit size uses.
    /// </summary>
    /// <remarks>
    /// The branch-weight buffer is laid out per planning layer and a hub
    /// carries exactly five layer counts, while the arc filter has six size
    /// bits. The mod tools name five AI types (SOLDIER / HOVER / SMALL /
    /// MEDIUM / HUGE), so the sixth filter bit has no layer of its own; Large
    /// is folded onto the last layer with Huge. Nothing downstream breaks if
    /// this mapping is wrong - a mismatched layer yields weights that don't
    /// decode, and <see cref="FillBranchWeights"/> falls back to unweighted
    /// routing.
    /// </remarks>
    static int LayerOf(PhxNavSize size)
    {
        switch (size)
        {
            case PhxNavSize.Soldier: return 0;
            case PhxNavSize.Hover: return 1;
            case PhxNavSize.Small: return 2;
            case PhxNavSize.Medium: return 3;
            default: return 4;                  // Large / Huge
        }
    }

    /// <summary>
    /// Decode the designers' route preferences for leaving <paramref name="hubIdx"/>
    /// on the way to <paramref name="goalHub"/>, as sourceArcIndex -> weight in [0,1].
    /// </summary>
    /// <remarks>
    /// The quantized buffer is [layer][slot][destination hub]: one byte per
    /// destination for each of the layer's outgoing slots, packing a 5-bit
    /// weight above a 2-bit selector into the hub's connection table. Decoded
    /// the same way the native helper does, but without its per-call name
    /// search over every hub, so it is affordable inside A*.
    /// </remarks>
    void FillBranchWeights(int hubIdx, int layer, int goalHub, Dictionary<int, float> into)
    {
        into.Clear();

        BFPlanningHubDefinition src = Hubs[hubIdx].Source;
        if (src == null || src.SetHubCount <= 0) return;

        IReadOnlyList<byte> weights = src.QuantizedWeights;
        IReadOnlyList<byte> perLayer = src.ConnectionsPerLayer;
        IReadOnlyList<byte> conIndices = src.ConnectionIndices;
        if (weights.Count == 0 || perLayer.Count <= layer || conIndices.Count == 0) return;

        int dest = goalHub - src.SetHubBase;
        if (dest < 0 || dest >= src.SetHubCount) return;

        int offset = dest;
        for (int j = 0; j < layer; ++j)
        {
            offset += perLayer[j] * src.SetHubCount;
        }

        int slots = perLayer[layer];
        for (int s = 0; s < slots; ++s)
        {
            int k = offset + s * src.SetHubCount;
            if (k < 0 || k >= weights.Count) return;

            byte packed = weights[k];
            int slot = packed & 0x3;
            if (slot >= conIndices.Count) continue;

            int sourceArc = src.SetArcBase + conIndices[slot];
            float weight = (packed >> 3) / 31.0f;

            // Several slots can select the same connection; the designers'
            // strongest preference is the one that should count.
            if (!into.TryGetValue(sourceArc, out float known) || weight > known)
            {
                into[sourceArc] = weight;
            }
        }
    }

    // ------------------------------------------------------- dynamic costs

    /// <summary>
    /// A transient reason to avoid part of the map: a firefight, a burning
    /// wreck, the spot four squadmates just died on.
    /// </summary>
    struct PhxCostSource
    {
        public Vector3 Position;
        public float Radius;
        public float Cost;
        public float ExpiresAt;
    }

    readonly List<PhxCostSource> CostSources = new List<PhxCostSource>();
    bool CostsDirty;

    /// <summary>
    /// Make routes through a volume more expensive without making them
    /// impossible.
    /// </summary>
    /// <remarks>
    /// Deliberately a cost and not a block. The authored graph is the
    /// authority on what is reachable - that is the whole reason this project
    /// imports it - so tactical state may only bias the choice between
    /// authored routes. Blocking here is how you get AI that refuses to move.
    ///
    /// <paramref name="cost"/> is in metres of equivalent detour, so a value
    /// near the map's typical arc length makes AI take a comparable-length
    /// alternative and a much larger one makes it take almost any alternative.
    /// </remarks>
    public void AddDynamicCost(Vector3 position, float radius, float cost, float duration)
    {
        if (radius <= 0f || cost <= 0f) return;

        CostSources.Add(new PhxCostSource
        {
            Position = position,
            Radius = radius,
            Cost = cost,
            ExpiresAt = duration > 0f ? Time.time + duration : float.MaxValue,
        });
        CostsDirty = true;
    }

    public void ClearDynamicCosts()
    {
        if (CostSources.Count == 0) return;
        CostSources.Clear();
        CostsDirty = true;
    }

    /// <summary>Drop expired cost sources. Cheap enough to call every frame.</summary>
    public void TickDynamicCosts()
    {
        float now = Time.time;
        for (int i = CostSources.Count - 1; i >= 0; --i)
        {
            if (now >= CostSources[i].ExpiresAt)
            {
                CostSources.RemoveAt(i);
                CostsDirty = true;
            }
        }
    }

    void RebuildDynamicCosts()
    {
        CostsDirty = false;

        for (int i = 0; i < Arcs.Count; ++i)
        {
            Arcs[i].DynamicCost = 0f;
        }
        if (CostSources.Count == 0) return;

        for (int i = 0; i < Arcs.Count; ++i)
        {
            PhxArc arc = Arcs[i];
            Vector3 a = Hubs[arc.From].Position;
            Vector3 b = Hubs[arc.To].Position;

            float total = 0f;
            for (int s = 0; s < CostSources.Count; ++s)
            {
                PhxCostSource cs = CostSources[s];
                float d = DistanceToSegment(cs.Position, a, b);
                if (d >= cs.Radius) continue;

                // Linear falloff: full cost at the centre, nothing at the rim.
                total += cs.Cost * (1f - d / cs.Radius);
            }
            arc.DynamicCost = total;
        }
    }

    static float DistanceToSegment(Vector3 point, Vector3 a, Vector3 b)
    {
        Vector3 ab = b - a;
        float lenSqr = ab.sqrMagnitude;
        if (lenSqr < 1e-4f) return Vector3.Distance(point, a);

        float t = Mathf.Clamp01(Vector3.Dot(point - a, ab) / lenSqr);
        return Vector3.Distance(point, a + ab * t);
    }

    // ------------------------------------------------------------- querying

    /// <summary>
    /// Is this point inside a barrier that blocks the given unit size?
    /// Barriers are size-filtered: one that keeps vehicles out must not stop
    /// infantry, which is why the size has to be passed in.
    /// </summary>
    public bool IsBlockedByBarrier(Vector3 worldPos, PhxNavSize size)
    {
        EArcFilterFlags sizeFlag = ToFilter(size);

        foreach (PhxBarrier b in Barriers)
        {
            if (!b.Enabled || !b.Blocks(sizeFlag)) continue;
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

    public int FindNearestHub(Vector3 position, PhxNavSize size,
                              PhxNavCapabilities capabilities = PhxNavCapabilities.JetInfantry)
    {
        int best = -1;
        float bestDist = float.MaxValue;
        EArcFilterFlags need = ToFilter(size);

        for (int i = 0; i < Hubs.Count; ++i)
        {
            // A hub is only useful if at least one arc this unit size can
            // actually traverse touches it. (Previously hubs with NO arcs
            // fell through this check and could be selected as the nearest,
            // which then guaranteed pathfinding failure.)
            bool usable = false;
            foreach (int a in Hubs[i].ArcIndices)
            {
                if (Arcs[a].Blocked) continue;
                if ((Arcs[a].Filter & need) == 0) continue;
                if (!CanTraverse(Arcs[a], capabilities)) continue;
                usable = true;
                break;
            }
            if (!usable) continue;

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
    public bool FindPath(Vector3 from, Vector3 to, PhxNavSize size, List<Vector3> outPath,
                         PhxNavCapabilities capabilities = PhxNavCapabilities.JetInfantry)
    {
        outPath.Clear();
        if (!IsLoaded) return false;

        int start = FindNearestHub(from, size, capabilities);
        int goal = FindNearestHub(to, size, capabilities);
        if (start < 0 || goal < 0) return false;
        if (start == goal)
        {
            outPath.Add(to);
            return true;
        }

        EArcFilterFlags need = ToFilter(size);
        int layer = LayerOf(size);
        if (CostsDirty) RebuildDynamicCosts();

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

            // The designers' own route preferences out of this hub toward the
            // goal. Decoded per expansion rather than precomputed: the table
            // is keyed by destination, so there is nothing to precompute that
            // isn't a full hubs-by-hubs matrix.
            FillBranchWeights(current, layer, goal, BranchScratch);

            foreach (int arcIdx in Hubs[current].ArcIndices)
            {
                PhxArc arc = Arcs[arcIdx];
                if (arc.Blocked) continue;
                if ((arc.Filter & need) == 0) continue;      // wrong unit size
                if (!CanTraverse(arc, capabilities)) continue;   // can't make the jump

                int next = arc.To;
                if (Closed[next]) continue;

                // NOTE: barriers deliberately do NOT veto graph traversal.
                //
                // They used to, and it made pathfinding fail completely -
                // measured at 0 of 4157 requests routed on Coruscant. The data
                // says why: of that map's 328 barriers, 321 carry flag 3
                // (Soldier|Small), so nearly every one of them "blocks"
                // soldiers, and with a 155-hub graph the volumes cover it
                // wholesale. Every neighbour was rejected and A* could never
                // expand.
                //
                // The authored graph is the authority: if a level designer
                // connected two hubs with a soldier-traversable arc, soldiers
                // may use it - which is exactly how the original game behaves
                // with this same graph and these same barriers. Barriers are
                // keep-out volumes for free movement and for Lua to toggle,
                // and are applied in steering rather than here.

                // Cost = authored length, biased by how strongly the designers
                // weighted this branch toward the goal, plus whatever the
                // current fight has made this stretch worth avoiding.
                //
                // The bias multiplier stays >= 1 (an unweighted or unknown arc
                // costs exactly its length, the least-preferred arc costs
                // double) and the dynamic term is non-negative, so the
                // straight-line heuristic remains admissible and A* still
                // returns an optimal route under these costs.
                float bias = 1f;
                if (arc.SourceIndex >= 0 && BranchScratch.TryGetValue(arc.SourceIndex, out float weight))
                {
                    bias = 2f - Mathf.Clamp01(weight);
                }

                float tentative = GScore[current] + arc.Length * bias + arc.DynamicCost;
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

    /// <summary>
    /// Whether a unit with these capabilities can get down this arc.
    /// </summary>
    /// <remarks>
    /// The two attribute bits are requirements, not hints: an arc flagged
    /// JetJump is a gap that must be jetted across. Vehicles are given the
    /// full capability set by their callers rather than being special-cased
    /// here - a hover crossing a "jump" arc is a designer's shorthand for a
    /// drop the vehicle can take, and refusing it would strand ground vehicles
    /// on maps that use those arcs on ordinary routes.
    /// </remarks>
    static bool CanTraverse(PhxArc arc, PhxNavCapabilities capabilities)
    {
        if ((arc.Attributes & EArcAttributeFlags.JetJump) != 0 &&
            (capabilities & PhxNavCapabilities.JetJump) == 0)
        {
            return false;
        }
        if ((arc.Attributes & EArcAttributeFlags.Jump) != 0 &&
            (capabilities & PhxNavCapabilities.Jump) == 0)
        {
            return false;
        }
        return true;
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
