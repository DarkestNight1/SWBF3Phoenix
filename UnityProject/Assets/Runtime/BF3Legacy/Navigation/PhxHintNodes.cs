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

    // parsed properties
    public string Mode;            // "Attack" / "Defend" / ...
    public string Posture;         // "Stand" / "Crouch" / "Prone"
    public string CommandPost;     // owning CP name, if any

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

    public static void Load(World world)
    {
        if (world == null) return;

        HintNode[] hints = world.GetHintNodes();
        if (hints == null || hints.Length == 0) return;

        foreach (HintNode h in hints)
        {
            PhxHintNode node = new PhxHintNode
            {
                Name = h.Name,
                RawType = (int)h.Type,
                Type = TypeFromRaw((int)h.Type),
                Position = UnityUtils.Vec3FromLibWorld(h.Position),
                Rotation = UnityUtils.QuatFromLibWorld(h.Rotation),
            };

            h.GetProperties(out uint[] props, out string[] values);
            if (props != null)
            {
                for (int i = 0; i < props.Length; ++i)
                {
                    if (props[i] == HashUtils.GetFNV("Mode")) node.Mode = values[i];
                    else if (props[i] == HashUtils.GetFNV("Posture")) node.Posture = values[i];
                    else if (props[i] == HashUtils.GetFNV("CommandPost")) node.CommandPost = values[i];
                }
            }

            Nodes.Add(node);
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

    static PhxHintType TypeFromRaw(int raw)
    {
        if (raw >= 0 && raw <= (int)PhxHintType.Land)
        {
            return (PhxHintType)raw;
        }

        if (LoggedUnknownTypes.Add(raw))
        {
            Debug.LogWarning($"[BF3Legacy] Unrecognised hint node type {raw} - " +
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
