using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// What one renderable part of an imported model actually is.
/// </summary>
/// <remarks>
/// An MSH model is not one mesh. It is a set of SEGM segments, each with its
/// own material and its own bone, and each carrying a <c>Tag</c> the artist
/// wrote - which is how the original data says "this is the hull", "this is
/// the cockpit glass", "this is the left track".
///
/// The importer already reconstructs that structure (<c>SWBFSegment</c> knows
/// the index, the node and the tag) and then discards it: the finished
/// GameObject is a hierarchy of renderers with no record of which part is
/// which. Everything that wants to treat a model as having parts - per-zone
/// vehicle damage, a scorch mark that lands on the hull rather than the
/// canopy, snow that settles on the upward-facing plates, per-part LOD, a
/// destroyed engine that stops emitting - has to re-derive that from
/// transform names, or give up and treat the model as one object.
///
/// This carries it instead. It is deliberately tiny and added at import: an
/// int, a string and a reference, on objects that already exist.
/// </remarks>
public sealed class BFSegmentIdentity : MonoBehaviour
{
    /// <summary>Submesh/material index within the segment's node.</summary>
    public int Index;

    /// <summary>
    /// The artist's own tag from the MSH, lower-cased. Empty when untagged,
    /// which a lot of scenery is.
    /// </summary>
    public string Tag = "";

    /// <summary>Model this segment belongs to.</summary>
    public string ModelName = "";

    /// <summary>Bone/node it is attached to, or empty when skinned.</summary>
    public string NodeName = "";

    /// <summary>True for segments deformed by the skeleton rather than parented to it.</summary>
    public bool IsSkinned;

    /// <summary>Semantic role inferred from the tag and node name.</summary>
    public BFSegmentRole Role = BFSegmentRole.Unknown;

    /// <summary>
    /// Every segment identity under an object, in a reused buffer.
    /// </summary>
    /// <remarks>
    /// Reused because the callers that want this - a damage system resolving a
    /// hit, a decal deciding what it landed on - ask per event, and per-event
    /// allocation at Battlefront rates is exactly what the rest of this layer
    /// is careful to avoid. Valid until the next call.
    /// </remarks>
    static readonly List<BFSegmentIdentity> Scratch = new List<BFSegmentIdentity>();

    public static IReadOnlyList<BFSegmentIdentity> AllUnder(GameObject root)
    {
        Scratch.Clear();
        if (root != null) root.GetComponentsInChildren(true, Scratch);
        return Scratch;
    }

    /// <summary>The segment a collider belongs to, or null.</summary>
    public static BFSegmentIdentity Of(Collider collider)
    {
        return collider == null ? null : collider.GetComponentInParent<BFSegmentIdentity>();
    }

    /// <summary>First segment under <paramref name="root"/> with a role.</summary>
    public static BFSegmentIdentity FindRole(GameObject root, BFSegmentRole role)
    {
        if (root == null) return null;

        BFSegmentIdentity[] segments = root.GetComponentsInChildren<BFSegmentIdentity>(true);
        for (int i = 0; i < segments.Length; ++i)
        {
            if (segments[i].Role == role) return segments[i];
        }
        return null;
    }

    public override string ToString() =>
        $"{ModelName}/{(string.IsNullOrEmpty(NodeName) ? "<skinned>" : NodeName)}#{Index}" +
        (string.IsNullOrEmpty(Tag) ? "" : $" '{Tag}'") + $" [{Role}]";
}

/// <summary>What a segment is for, as far as can be told from the source.</summary>
/// <remarks>
/// Inferred from the artist's tag and node name rather than declared anywhere.
/// The stock content is consistent enough for this to be worth doing - twenty
/// years of vehicles name their turrets "turret" - but it is inference, so
/// <see cref="Unknown"/> is a normal and common answer and every consumer must
/// behave sensibly when that is what it gets.
/// </remarks>
public enum BFSegmentRole
{
    Unknown = 0,
    Hull,
    Cockpit,
    Glass,
    Turret,
    Weapon,
    Engine,
    Wheel,
    Track,
    Leg,
    Wing,
    Door,
    Light,

    /// <summary>Head, torso, limbs - the character equivalent of a damage zone.</summary>
    Body,
}

/// <summary>Infers <see cref="BFSegmentRole"/> from the names the source uses.</summary>
public static class BFSegmentRoles
{
    static readonly (string Keyword, BFSegmentRole Role)[] Vocabulary =
    {
        ("cockpit",  BFSegmentRole.Cockpit),
        ("canopy",   BFSegmentRole.Glass),
        ("windshield", BFSegmentRole.Glass),
        ("glass",    BFSegmentRole.Glass),
        ("turret",   BFSegmentRole.Turret),
        ("cannon",   BFSegmentRole.Weapon),
        ("weapon",   BFSegmentRole.Weapon),
        ("gun",      BFSegmentRole.Weapon),
        ("barrel",   BFSegmentRole.Weapon),
        ("launcher", BFSegmentRole.Weapon),
        ("engine",   BFSegmentRole.Engine),
        ("thruster", BFSegmentRole.Engine),
        ("exhaust",  BFSegmentRole.Engine),
        ("repulsor", BFSegmentRole.Engine),
        ("wheel",    BFSegmentRole.Wheel),
        ("tire",     BFSegmentRole.Wheel),
        ("track",    BFSegmentRole.Track),
        ("tread",    BFSegmentRole.Track),
        ("leg",      BFSegmentRole.Leg),
        ("foot",     BFSegmentRole.Leg),
        ("knee",     BFSegmentRole.Leg),
        ("hip",      BFSegmentRole.Leg),
        ("wing",     BFSegmentRole.Wing),
        ("flap",     BFSegmentRole.Wing),
        ("door",     BFSegmentRole.Door),
        ("hatch",    BFSegmentRole.Door),
        ("ramp",     BFSegmentRole.Door),
        ("light",    BFSegmentRole.Light),
        ("lamp",     BFSegmentRole.Light),
        ("glow",     BFSegmentRole.Light),
        ("hull",     BFSegmentRole.Hull),
        ("body",     BFSegmentRole.Hull),
        ("chassis",  BFSegmentRole.Hull),

        // Character parts. Deliberately last: a vehicle segment named
        // "gunner_arm" should read as a weapon mount before it reads as a
        // limb, and the vehicle keywords above catch that first.
        ("head",     BFSegmentRole.Body),
        ("torso",    BFSegmentRole.Body),
        ("spine",    BFSegmentRole.Body),
        ("arm",      BFSegmentRole.Body),
        ("hand",     BFSegmentRole.Body),
        ("thigh",    BFSegmentRole.Body),
        ("calf",     BFSegmentRole.Body),
        ("pelvis",   BFSegmentRole.Body),
    };

    /// <summary>
    /// Role from a segment's tag, falling back to its node name.
    /// </summary>
    /// <remarks>
    /// The tag is checked first because it is what the artist wrote about the
    /// segment specifically; the node name is the bone it hangs off, which is
    /// often descriptive too but sometimes just skeletal structure.
    /// </remarks>
    public static BFSegmentRole Infer(string tag, string nodeName)
    {
        BFSegmentRole fromTag = Match(tag);
        if (fromTag != BFSegmentRole.Unknown) return fromTag;

        return Match(nodeName);
    }

    /// <summary>
    /// Match on whole name tokens rather than raw substrings.
    /// </summary>
    /// <remarks>
    /// Substring matching was actively wrong, not merely imprecise. Measured
    /// on a real map, <c>rep_assultship_DOME</c> came back as a <b>leg</b>,
    /// because "s-hip" contains "hip". "arm" is inside "armor", "alarm" and
    /// "farm"; "leg" is inside "legend"; "gun" is inside "gunship" whether or
    /// not that segment is the gun. Every one of those is a confident wrong
    /// answer, which is worse than Unknown - a mis-roled segment gets the
    /// wrong damage zone and silently refuses decals.
    ///
    /// The names are machine-generated in a consistent style
    /// (<c>rep_fly_assault_dome</c>, <c>bone_pelvis</c>,
    /// <c>Main_library_upper_P_light1</c>), so splitting on separators, case
    /// changes and digits recovers real tokens. A keyword now has to BE a
    /// token, or a token has to start with it and be barely longer - which
    /// still catches "turret1" and "legL" without catching "gunship".
    /// </remarks>
    static BFSegmentRole Match(string name)
    {
        if (string.IsNullOrEmpty(name)) return BFSegmentRole.Unknown;

        Tokenize(name, TokenScratch);
        if (TokenScratch.Count == 0) return BFSegmentRole.Unknown;

        for (int i = 0; i < Vocabulary.Length; ++i)
        {
            string keyword = Vocabulary[i].Keyword;
            for (int t = 0; t < TokenScratch.Count; ++t)
            {
                string token = TokenScratch[t];
                if (token.Length < keyword.Length) continue;

                // Exact token, or the token is the keyword plus a short
                // suffix - "turret1", "legs", "wheel_fl". Anything longer is a
                // different word that happens to start the same way.
                if (!token.StartsWith(keyword, System.StringComparison.Ordinal)) continue;
                if (token.Length - keyword.Length > 2) continue;

                return Vocabulary[i].Role;
            }
        }
        return BFSegmentRole.Unknown;
    }

    static readonly List<string> TokenScratch = new List<string>();
    static readonly System.Text.StringBuilder TokenBuilder = new System.Text.StringBuilder();

    /// <summary>Split a source name into lower-case word tokens.</summary>
    static void Tokenize(string name, List<string> into)
    {
        into.Clear();
        TokenBuilder.Length = 0;

        for (int i = 0; i < name.Length; ++i)
        {
            char c = name[i];

            bool separator = c == '_' || c == '-' || c == '.' || c == ' ' || char.IsDigit(c);

            // A lower-to-upper transition starts a new word: "assultShip" is
            // two tokens even with no separator between them.
            bool caseBreak = i > 0 && char.IsUpper(c) && char.IsLower(name[i - 1]);

            if (separator || caseBreak)
            {
                if (TokenBuilder.Length > 0)
                {
                    into.Add(TokenBuilder.ToString().ToLowerInvariant());
                    TokenBuilder.Length = 0;
                }
                if (separator) continue;
            }
            TokenBuilder.Append(c);
        }

        if (TokenBuilder.Length > 0)
        {
            into.Add(TokenBuilder.ToString().ToLowerInvariant());
        }
    }
}
