using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// An explicit surface type on an object, overriding everything inferred.
/// For the cases where a name says nothing useful and a human knows better.
/// </summary>
public sealed class BFSurfaceTag : MonoBehaviour
{
    public BFSurfaceType Type = BFSurfaceType.Unknown;
}

/// <summary>
/// Answers "what did I just hit?" for every system that needs to respond to a
/// surface.
/// </summary>
/// <remarks>
/// The stock data does not label surfaces, so the type has to be inferred from
/// what it does carry. In priority order:
///
/// <list type="number">
/// <item>An explicit <see cref="BFSurfaceTag"/> on the object or a parent.</item>
/// <item>The terrain's own blend map, sampled at the hit position - which is
/// how a Hoth valley floor reads as snow and the rock face beside it reads as
/// rock, from the same mesh.</item>
/// <item>The renderer's texture name, matched against the vocabulary the
/// original artists used consistently across twenty years of stock and mod
/// content ("snow", "ice", "sand", "metl", "durasteel", ...).</item>
/// <item>The object's collision layer, which at least separates people from
/// buildings from terrain.</item>
/// <item>The map's dominant surface from its lighting profile.</item>
/// </list>
///
/// Results are cached per collider: a firefight queries the same few hundred
/// colliders thousands of times, and the string matching is not free.
/// </remarks>
public static class BFSurfaceQuery
{
    static readonly Dictionary<int, BFSurfaceType> Cache = new Dictionary<int, BFSurfaceType>();

    /// <summary>Keyword to surface type, checked in order - longest first.</summary>
    static readonly (string Keyword, BFSurfaceType Type)[] TextureVocabulary =
    {
        ("durasteel", BFSurfaceType.Metal),
        ("concrete",  BFSurfaceType.Concrete),
        ("permacrete",BFSurfaceType.Concrete),
        ("plascrete", BFSurfaceType.Concrete),
        ("gravel",    BFSurfaceType.Rock),
        ("granite",   BFSurfaceType.Rock),
        ("gletsch",   BFSurfaceType.Ice),
        ("glacier",   BFSurfaceType.Ice),
        ("crystal",   BFSurfaceType.Glass),
        ("window",    BFSurfaceType.Glass),
        ("foliage",   BFSurfaceType.Grass),
        ("grass",     BFSurfaceType.Grass),
        ("leaf",      BFSurfaceType.Grass),
        ("moss",      BFSurfaceType.Grass),
        ("plant",     BFSurfaceType.Grass),
        ("water",     BFSurfaceType.Water),
        ("ocean",     BFSurfaceType.Water),
        ("river",     BFSurfaceType.Water),
        ("swamp",     BFSurfaceType.Mud),
        ("lava",      BFSurfaceType.Lava),
        ("magma",     BFSurfaceType.Lava),
        ("snow",      BFSurfaceType.Snow),
        ("sand",      BFSurfaceType.Sand),
        ("dune",      BFSurfaceType.Sand),
        ("dirt",      BFSurfaceType.Mud),
        ("mud",       BFSurfaceType.Mud),
        ("rock",      BFSurfaceType.Rock),
        ("stone",     BFSurfaceType.Rock),
        ("cliff",     BFSurfaceType.Rock),
        ("wood",      BFSurfaceType.Wood),
        ("bark",      BFSurfaceType.Wood),
        ("plank",     BFSurfaceType.Wood),
        ("tree",      BFSurfaceType.Wood),
        ("glass",     BFSurfaceType.Glass),
        ("ice",       BFSurfaceType.Ice),
        ("metl",      BFSurfaceType.Metal),
        ("metal",     BFSurfaceType.Metal),
        ("steel",     BFSurfaceType.Metal),
        ("hull",      BFSurfaceType.Metal),
        ("panel",     BFSurfaceType.Metal),
        ("floor",     BFSurfaceType.Metal),
        ("wall",      BFSurfaceType.Concrete),
    };

    /// <summary>Fallback when nothing at all identifies the surface.</summary>
    public static BFSurfaceType MapDefault = BFSurfaceType.Rock;

    public static void Reset()
    {
        Cache.Clear();
        MapDefault = BFSurfaceType.Rock;
    }

    /// <summary>Surface type at a raycast hit. Never throws.</summary>
    public static BFSurfaceType Resolve(RaycastHit hit)
    {
        return Resolve(hit.collider, hit.point);
    }

    /// <summary>Surface type of a collider at a world position.</summary>
    public static BFSurfaceType Resolve(Collider collider, Vector3 worldPosition)
    {
        if (collider == null) return MapDefault;

        // Terrain is sampled per position, so it can never be cached per
        // collider - one terrain collider covers the whole map.
        if (collider.gameObject.layer == PhxLayers.TerrainLayer)
        {
            BFSurfaceType sampled = BFTerrainSurfaceMap.Sample(worldPosition);
            return sampled != BFSurfaceType.Unknown ? sampled : MapDefault;
        }

        int id = collider.GetInstanceID();
        if (Cache.TryGetValue(id, out BFSurfaceType known)) return known;

        BFSurfaceType resolved = Classify(collider);
        Cache[id] = resolved;
        return resolved;
    }

    /// <summary>The response profile at a hit. Convenience over Resolve.</summary>
    public static BFSurfaceProfile ResolveProfile(RaycastHit hit)
    {
        return BFSurfaceProfile.Get(Resolve(hit));
    }

    public static BFSurfaceProfile ResolveProfile(Collider collider, Vector3 worldPosition)
    {
        return BFSurfaceProfile.Get(Resolve(collider, worldPosition));
    }

    static BFSurfaceType Classify(Collider collider)
    {
        BFSurfaceTag tag = collider.GetComponentInParent<BFSurfaceTag>();
        if (tag != null && tag.Type != BFSurfaceType.Unknown) return tag.Type;

        // People are people whatever they are wearing.
        if (collider.GetComponentInParent<PhxSoldier>() != null) return BFSurfaceType.Flesh;

        // Vehicles are metal even when the hull texture is named for its camo.
        if (collider.GetComponentInParent<PhxVehicle>() != null) return BFSurfaceType.Metal;

        BFSurfaceType fromTexture = FromRenderer(collider);
        if (fromTexture != BFSurfaceType.Unknown) return fromTexture;

        // Object name is the last textual clue - prop odf names are descriptive
        // ("cor1_prop_library_bustb") often enough to be worth a look.
        BFSurfaceType fromName = FromKeyword(collider.name);
        if (fromName != BFSurfaceType.Unknown) return fromName;

        return MapDefault;
    }

    static BFSurfaceType FromRenderer(Collider collider)
    {
        // The collider itself rarely has the renderer - imported models split
        // collision and visuals into sibling objects - so look at the parent's
        // subtree, but only one level up, to avoid attributing a whole
        // building's material to one small prop inside it.
        Transform search = collider.transform.parent != null
            ? collider.transform.parent
            : collider.transform;

        Renderer[] renderers = search.GetComponentsInChildren<Renderer>(false);
        for (int i = 0; i < renderers.Length; ++i)
        {
            Material material = renderers[i].sharedMaterial;
            if (material == null) continue;

            BFSurfaceType fromMaterial = FromKeyword(material.name);
            if (fromMaterial != BFSurfaceType.Unknown) return fromMaterial;

            Texture texture = material.HasProperty("_BaseColorMap")
                ? material.GetTexture("_BaseColorMap")
                : material.mainTexture;
            if (texture == null) continue;

            BFSurfaceType fromTexture = FromKeyword(texture.name);
            if (fromTexture != BFSurfaceType.Unknown) return fromTexture;
        }
        return BFSurfaceType.Unknown;
    }

    /// <summary>Match a name against the artists' vocabulary.</summary>
    public static BFSurfaceType FromKeyword(string name)
    {
        if (string.IsNullOrEmpty(name)) return BFSurfaceType.Unknown;

        string lower = name.ToLowerInvariant();
        for (int i = 0; i < TextureVocabulary.Length; ++i)
        {
            if (lower.Contains(TextureVocabulary[i].Keyword))
            {
                return TextureVocabulary[i].Type;
            }
        }
        return BFSurfaceType.Unknown;
    }
}
