using System;
using UnityEngine;

/// <summary>
/// The categories of authored BF2 data this project reconstructs.
///
/// Kept as an enum rather than the free-form strings the first sketch used:
/// every count in the validation report and every lookup in
/// <see cref="BFSourceDatabase"/> is keyed by kind, and a typo in a string
/// there produces a silently empty category rather than a compile error.
/// </summary>
public enum BFSourceKind
{
    Unknown = 0,
    World,
    Instance,
    EntityClass,
    Region,
    Barrier,
    HintNode,
    Terrain,
    Light,
    Path,
    PlanningHub,
    PlanningArc,
    Material,
    Model,
    Texture,
    Sound,
    Effect,
    Config,
    Animation,
}

/// <summary>
/// Stable address of one object in the source data mounted for a map.
///
/// Immutable on purpose. A ref is handed to Unity objects, held in the
/// database and written into reports; if any of those could edit it, the
/// "stable address" guarantee would only hold until the first careless
/// assignment.
/// </summary>
public sealed class BFSourceRef : IEquatable<BFSourceRef>
{
    public readonly string LevelName;
    public readonly string WorldName;
    public readonly BFSourceKind Kind;
    public readonly string Name;

    /// <summary>
    /// Position within its own category, which is what makes an unnamed
    /// object addressable at all.
    /// </summary>
    /// <remarks>
    /// A large share of the instances in a stock world carry an empty Name -
    /// props, foliage and clutter are placed without one. Keying purely on
    /// the name collapses all of them onto a single id, so a database keyed
    /// that way holds one entry where the map has four hundred, and any
    /// count derived from it is wrong in the direction that hides missing
    /// content.
    /// </remarks>
    public readonly int Ordinal;

    public BFSourceRef(string levelName, string worldName, BFSourceKind kind, string name, int ordinal)
    {
        LevelName = levelName ?? string.Empty;
        WorldName = worldName ?? string.Empty;
        Kind = kind;
        Name = name ?? string.Empty;
        Ordinal = ordinal;
    }

    public string Id => $"{LevelName}:{WorldName}:{Kind}:{Name}#{Ordinal}";

    /// <summary>Human-facing address, for logs and reports.</summary>
    public string DisplayName =>
        string.IsNullOrEmpty(Name) ? $"{Kind}[{Ordinal}]" : $"{Kind} '{Name}'";

    public bool Equals(BFSourceRef other) => other != null && other.Id == Id;
    public override bool Equals(object obj) => Equals(obj as BFSourceRef);
    public override int GetHashCode() => Id.GetHashCode();
    public override string ToString() => Id;
}

/// <summary>
/// Source identity attached to the Unity projection of a BF2 object.
///
/// This is the link that makes the Unity scene a *view* of the source data
/// rather than the only surviving copy of it: given any imported GameObject
/// you can recover which authored object it came from, and given the source
/// record you can find what (if anything) was built for it.
/// </summary>
public sealed class BFSourceLink : MonoBehaviour
{
    // Not serialized: a BFSourceRef is only meaningful next to the database
    // that captured it, and that database is rebuilt on every map load.
    // Serializing it into a scene would preserve an address whose target no
    // longer exists.
    [NonSerialized] public BFSourceRef Source;

    /// <summary>Source ref of this object or the nearest linked ancestor.</summary>
    public static BFSourceRef Find(GameObject obj)
    {
        if (obj == null) return null;

        BFSourceLink link = obj.GetComponentInParent<BFSourceLink>();
        return link == null ? null : link.Source;
    }
}
