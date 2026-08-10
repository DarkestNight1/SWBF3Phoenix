using System.Collections.Generic;
using UnityEngine;
using LibSWBF2.Wrappers;
using LibSWBF2.Utils;

/// <summary>
/// The original game's AI hint nodes - the level designers' tactical
/// annotations, placed in ZeroEditor's HINTNODE mode and never used until now.
///
/// Per the SWBF2 mod tools documentation the node types are:
///   SNIPE    - marksman firing position
///   PATROL   - loiter/reposition area
///   COVER    - duck behind the adjacent object
///   ACCESS   - stand at a console/panel (flavour)
///   JETJUMP  - jet troopers may leap to a higher level here
///   MINE     - lay a minefield here
///   LAND     - transport landing/deploy spot
///
/// Nodes also carry properties (Mode attack/defend, posture stand/crouch/prone,
/// an owning CommandPost, allied pathing) which are read from the node's
/// property list.
///
/// IMPORTANT - type numbering: LibSWBF2 exposes the raw uint16 from the Hint
/// chunk. The mod tools document the *names* and their editor order, but not
/// the on-disk numeric values, so the mapping below follows that documented
/// order and any value we don't recognise is kept as Unknown and logged once.
/// If a map's nodes come through mis-typed, correct TypeFromRaw - everything
/// downstream is driven off the enum, not the raw number.
/// </summary>
public enum PhxHintType
{
    Unknown = -1,
    Snipe = 0,
    Patrol = 1,
    Cover = 2,
    Access = 3,
    JetJump = 4,
    Mine = 5,
    Land = 6,
}

public class PhxHintNode
{
    public string Name;
    public PhxHintType Type = PhxHintType.Unknown;
    public int RawType;
    public Vector3 Position;
    public Quaternion Rotation;

    // Parsed properties. Names taken from the ModTools .hnt sources, which are
    // plain text - a node looks like:
    //     Hint("HintNode0", "2")
    //     { Position(...); Rotation(...); Radius(2.0);
    //       PrimaryStance(1); SecondaryStance(24); Mode(0); }
    //
    // An earlier version read "Posture" and "CommandPost", which appear nowhere
    // in that data (2045 nodes carry PrimaryStance, 2006 SecondaryStance, 50
    // Radius, and none carry the other two), so those fields were always empty
    // and every stance the level designers authored was discarded.
    public string Mode;              // behaviour mode, numeric in the data
    public int PrimaryStance;        // stand / crouch / prone, see StanceFromRaw
    public int SecondaryStance;
    public float Radius = 2f;        // influence radius; 2 is the editor default

    /// <summary>The authored record this node projects.</summary>
    public BFHintNodeDefinition Source;

    // runtime occupancy so two bots don't fight over one node
    public object Occupant;
    public float ReleaseTime;

    public bool IsFree(float now) => Occupant == null || now > ReleaseTime;

    /// <summary>Direction the designer pointed this node (e.g. firing lane).</summary>
    public Vector3 Facing => Rotation * Vector3.forward;
}

public static class PhxHintNodes
{
    static readonly List<PhxHintNode> Nodes = new List<PhxHintNode>();
    static readonly HashSet<int> LoggedUnknownTypes = new HashSet<int>();

    public static IReadOnlyList<PhxHintNode> All => Nodes;
    public static int Count => Nodes.Count;

    public static void Reset()
    {
        Nodes.Clear();
        LoggedUnknownTypes.Clear();
    }

    public static void Load(BFWorldDefinition world)
    {
        if (world == null || world.HintNodes.Count == 0) return;

        uint modeHash = HashUtils.GetFNV("Mode");
        uint primaryHash = HashUtils.GetFNV("PrimaryStance");
        uint secondaryHash = HashUtils.GetFNV("SecondaryStance");
        uint radiusHash = HashUtils.GetFNV("Radius");

        foreach (BFHintNodeDefinition h in world.HintNodes)
        {
            PhxHintNode node = new PhxHintNode
            {
                Name = h.Name,
                RawType = h.RawType,
                Type = TypeFromRaw(h.RawType),
                Position = h.Position,
                Rotation = h.Rotation,
                Source = h,
            };

            for (int i = 0; i < h.Properties.Count; ++i)
            {
                BFProperty prop = h.Properties[i];
                if (prop.Hash == modeHash)
                {
                    node.Mode = prop.Value;
                }
                else if (prop.Hash == primaryHash)
                {
                    int.TryParse(prop.Value, out node.PrimaryStance);
                }
                else if (prop.Hash == secondaryHash)
                {
                    int.TryParse(prop.Value, out node.SecondaryStance);
                }
                else if (prop.Hash == radiusHash)
                {
                    if (float.TryParse(prop.Value, System.Globalization.NumberStyles.Float,
                                       System.Globalization.CultureInfo.InvariantCulture,
                                       out float radius) && radius > 0f)
                    {
                        node.Radius = radius;
                    }
                }
            }

            Nodes.Add(node);
            BFSourceDatabase.Active.MarkImported(h.Source);
        }

        Debug.Log($"[BF3Legacy] {Nodes.Count} AI hint nodes loaded " +
                  $"(cover: {CountOf(PhxHintType.Cover)}, snipe: {CountOf(PhxHintType.Snipe)}, " +
                  $"patrol: {CountOf(PhxHintType.Patrol)}, jetjump: {CountOf(PhxHintType.JetJump)})");
    }

    static int CountOf(PhxHintType type)
    {
        int n = 0;
        foreach (PhxHintNode node in Nodes) if (node.Type == type) n++;
        return n;
    }

    /// <summary>
    /// Whether a stance value means crouched or prone rather than standing.
    /// </summary>
    /// <remarks>
    /// The ZeroEditor guide describes stances as "stand, crouch, go prone, or
    /// face left or right", but does not publish the numbering, and the stock
    /// data only ever uses a small set of values (1 and 24 dominate). Treating
    /// non-zero, non-one values as low is a reading of that data, not a
    /// documented mapping - revisit if a stance ever looks wrong in game.
    /// </remarks>
    public static bool StanceIsLow(int stance)
    {
        return stance > 1;
    }

    static PhxHintType TypeFromRaw(int raw)
    {
        // The TYPE chunk is read as a raw uint16, and the values that come out
        // of stock maps are 48, 50, 55, 56 - which are the ASCII codes for
        // '0', '2', '7' and '8'. The type is stored as a digit character, not
        // as a number, so every node fell outside the enum and the whole hint
        // network was classified Unknown: the load line read
        // "291 AI hint nodes loaded (cover: 0, snipe: 0, patrol: 0, jetjump: 0)"
        // on a map that is plainly full of cover and sniping positions.
        //
        // Decode the digit, which puts the common types (snipe 0, cover 2)
        // back in range and makes the hint network usable by the AI.
        if (raw >= '0' && raw <= '9')
        {
            raw -= '0';
        }

        if (raw >= 0 && raw <= (int)PhxHintType.Land)
        {
            return (PhxHintType)raw;
        }

        // Types above Land do occur (7 and 8 are both present in stock maps)
        // but their meaning is not established here, so they stay Unknown
        // rather than being guessed into a behaviour.
        if (LoggedUnknownTypes.Add(raw))
        {
            Debug.LogWarning($"[BF3Legacy] Hint node type {raw} has no known meaning - " +
                             "treated as Unknown (see PhxHintNodes.TypeFromRaw)");
        }
        return PhxHintType.Unknown;
    }

    /// <summary>
    /// Nearest free node of a type within range, optionally one that faces
    /// roughly toward a threat (so 'cover' actually covers from the enemy).
    /// </summary>
    public static PhxHintNode FindNearest(Vector3 from, PhxHintType type, float maxRange,
                                          Vector3? facingToward = null)
    {
        float now = Time.time;
        PhxHintNode best = null;
        float bestScore = float.MaxValue;
        float maxRangeSqr = maxRange * maxRange;

        foreach (PhxHintNode node in Nodes)
        {
            if (node.Type != type || !node.IsFree(now)) continue;

            float d = (node.Position - from).sqrMagnitude;
            if (d > maxRangeSqr) continue;

            float score = d;
            if (facingToward.HasValue)
            {
                Vector3 toThreat = facingToward.Value - node.Position;
                toThreat.y = 0f;
                if (toThreat.sqrMagnitude > 0.01f)
                {
                    // prefer nodes whose authored facing points at the threat
                    float angle = Vector3.Angle(node.Facing, toThreat.normalized);
                    if (angle > 100f) continue;                 // faces away - useless
                    score *= 1f + angle / 90f;
                }
            }

            if (score < bestScore)
            {
                bestScore = score;
                best = node;
            }
        }
        return best;
    }

    /// <summary>
    /// Every free node of a type within range of an anchor, appended to
    /// <paramref name="into"/>. For behaviours that want a set rather than the
    /// single nearest - a patrol route, the landing spots around a hangar.
    /// </summary>
    public static void FindAllWithin(Vector3 anchor, PhxHintType type, float maxRange,
                                     List<PhxHintNode> into)
    {
        if (into == null) return;
        into.Clear();

        float now = Time.time;
        float maxRangeSqr = maxRange * maxRange;

        foreach (PhxHintNode node in Nodes)
        {
            if (node.Type != type || !node.IsFree(now)) continue;
            if ((node.Position - anchor).sqrMagnitude > maxRangeSqr) continue;

            into.Add(node);
        }
    }

    /// <summary>
    /// The next node of a patrol loop after <paramref name="current"/>: the
    /// nearest other one of the same type, excluding where we already are.
    /// </summary>
    /// <remarks>
    /// Designers place PATROL nodes as a scattered set rather than an ordered
    /// route, so "the route" is whatever chaining them nearest-first produces.
    /// Excluding the current node is what stops a lone patrol node making a
    /// soldier stand still and call it patrolling.
    /// </remarks>
    public static PhxHintNode NextPatrolNode(PhxHintNode current, Vector3 anchor, float maxRange)
    {
        float now = Time.time;
        float maxRangeSqr = maxRange * maxRange;

        PhxHintNode best = null;
        float bestDist = float.MaxValue;
        Vector3 from = current != null ? current.Position : anchor;

        foreach (PhxHintNode node in Nodes)
        {
            if (node.Type != PhxHintType.Patrol || node == current) continue;
            if (!node.IsFree(now)) continue;
            if ((node.Position - anchor).sqrMagnitude > maxRangeSqr) continue;

            float d = (node.Position - from).sqrMagnitude;
            if (d >= bestDist) continue;

            bestDist = d;
            best = node;
        }
        return best;
    }

    /// <summary>Claim a node for a while so other AI pick different ones.</summary>
    public static bool TryOccupy(PhxHintNode node, object occupant, float holdSeconds)
    {
        if (node == null) return false;
        if (!node.IsFree(Time.time) && node.Occupant != occupant) return false;

        node.Occupant = occupant;
        node.ReleaseTime = Time.time + holdSeconds;
        return true;
    }

    public static void Release(PhxHintNode node, object occupant)
    {
        if (node != null && node.Occupant == occupant)
        {
            node.Occupant = null;
            node.ReleaseTime = 0f;
        }
    }
}
