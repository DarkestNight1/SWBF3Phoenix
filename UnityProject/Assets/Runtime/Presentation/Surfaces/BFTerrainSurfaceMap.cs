using UnityEngine;

/// <summary>
/// Which surface the terrain is, per position, derived from the map's own
/// blend map and layer texture names.
/// </summary>
/// <remarks>
/// A SWBF2 terrain is one mesh with up to sixteen blended layers, and the
/// interesting distinction on a map like Hoth is entirely within it: the
/// valley floor is snow, the ridge beside it is rock, and a boot or a blaster
/// bolt should know which it just hit. The blend map already says, so the
/// dominant layer per texel is collapsed to a surface type once at import and
/// then sampled directly.
///
/// One byte per blend texel: a 512x512 blend map costs 256 KB, which buys a
/// per-position answer for the whole map with no per-query texture read.
/// </remarks>
public static class BFTerrainSurfaceMap
{
    static BFSurfaceType[] Types;
    static int Dimension;
    static float WorldExtent;

    public static bool IsLoaded => Types != null && Dimension > 0 && WorldExtent > 0f;

    public static void Reset()
    {
        Types = null;
        CachedWorld = null;
        CachedGround = BFSurfaceType.Unknown;
        Dimension = 0;
        WorldExtent = 0f;
    }

    /// <summary>
    /// Build from the raw blend map and layer names the terrain importer
    /// already has in hand.
    /// </summary>
    /// <param name="blendDimension">Blend map side length in texels.</param>
    /// <param name="layerCount">Layers packed per texel.</param>
    /// <param name="blendMap">Raw weights, layerCount bytes per texel.</param>
    /// <param name="layerTextureNames">Names, in layer order.</param>
    /// <param name="worldExtent">Terrain side length in metres.</param>
    public static void Build(uint blendDimension, uint layerCount, byte[] blendMap,
                             System.Collections.Generic.IReadOnlyList<string> layerTextureNames,
                             float worldExtent)
    {
        Reset();

        if (blendMap == null || blendDimension == 0 || layerCount == 0 ||
            layerTextureNames == null || layerTextureNames.Count == 0 || worldExtent <= 0f)
        {
            return;
        }

        // Classify each layer once - the expensive part is the string matching,
        // and there are at most sixteen layers against a quarter-million texels.
        //
        // The keyword match alone classifies nothing on any stock map. Probing
        // all twelve shipped terrains, every layer texture is named
        // <map>_main_<n> - hoth_main_1, tat2_main_2, end_main_1, kas2_main_1 -
        // and not one of them contains "snow", "sand", "rock", "grass" or any
        // other word BFSurfaceQuery looks for. So every texel on every map
        // resolved to Unknown, and everything keyed on the result quietly did
        // nothing: no footprints in Hoth's snow or Tatooine's sand, because
        // Unknown is not Deformable, and no surface-correct impact debris,
        // wetness or snow accumulation either.
        //
        // The planet fallback is NOT applied here. Build runs during import,
        // and the world name it keys on is not reliably set that early - baking
        // it into the table would mean the fallback silently doing nothing
        // depending on load order. Sample applies it instead, by which point
        // the environment certainly exists. A name that does say something is
        // still resolved here and wins later, so mod terrain that names its
        // layers usefully keeps that answer.
        var layerTypes = new BFSurfaceType[layerCount];
        for (int i = 0; i < layerCount; ++i)
        {
            layerTypes[i] = i < layerTextureNames.Count
                ? BFSurfaceQuery.FromKeyword(layerTextureNames[i])
                : BFSurfaceType.Unknown;
        }

        int dim = (int)blendDimension;
        var types = new BFSurfaceType[dim * dim];
        int expected = dim * dim * (int)layerCount;
        if (blendMap.Length < expected)
        {
            Debug.LogWarning($"[BFSurface] Terrain blend map is {blendMap.Length} bytes, " +
                             $"expected {expected} - terrain surface types unavailable.");
            return;
        }

        for (int texel = 0; texel < types.Length; ++texel)
        {
            int baseIndex = texel * (int)layerCount;
            int best = 0;
            byte bestWeight = 0;

            for (int layer = 0; layer < layerCount; ++layer)
            {
                byte weight = blendMap[baseIndex + layer];
                if (weight <= bestWeight) continue;

                bestWeight = weight;
                best = layer;
            }

            // A texel with no weight anywhere is a hole or an unpainted patch;
            // leave it Unknown so the caller falls back to the map default
            // rather than claiming it is whatever layer 0 happens to be.
            types[texel] = bestWeight > 0 ? layerTypes[best] : BFSurfaceType.Unknown;
        }

        Types = types;
        Dimension = dim;
        WorldExtent = worldExtent;

        Debug.Log($"[BFSurface] Terrain surface map built: {dim}x{dim} over {worldExtent:F0}m, " +
                  $"layers [{string.Join(", ", layerTypes)}]");
    }

    static string CachedWorld;
    static BFSurfaceType CachedGround = BFSurfaceType.Unknown;

    /// <summary>
    /// What a map's ground is made of, when its layer names will not say.
    /// </summary>
    /// <remarks>
    /// Keyed by mission-script prefix, which is the same key
    /// <see cref="BFMapCollisionOverrides"/> and <see cref="BFMapWater"/> use.
    /// Each entry is the material a soldier is standing on for most of that
    /// map - not every texel of it, which no single answer could be, but the
    /// one that decides whether boots leave prints and what a blaster bolt
    /// throws up.
    ///
    /// A map not listed keeps Unknown, which is the honest answer for one
    /// nobody has looked at, and leaves it behaving exactly as it does now.
    /// </remarks>
    static BFSurfaceType PlanetGround()
    {
        string world = PhxGame.GetEnvironment()?.GetWorldName();
        if (string.IsNullOrEmpty(world)) return BFSurfaceType.Unknown;

        // Sample is on the path of every footstep, impact and query, so the
        // string work happens once per map rather than once per call.
        if (world == CachedWorld) return CachedGround;

        CachedWorld = world;
        CachedGround = Classify(world);
        return CachedGround;
    }

    static BFSurfaceType Classify(string world)
    {
        string key = world.ToLowerInvariant();

        // Snow, and the reason the user can name Hoth without being told.
        if (key.StartsWith("hot")) return BFSurfaceType.Snow;

        // Desert.
        if (key.StartsWith("tat")) return BFSurfaceType.Sand;
        if (key.StartsWith("geo")) return BFSurfaceType.Sand;

        // Forest floor and jungle. Mud is this codebase's word for loose dark
        // ground - BFSurfaceQuery already maps the keyword "dirt" onto it - and
        // it is the deepest print any surface takes, which is right for a
        // forest floor and wrong for nothing here.
        if (key.StartsWith("end")) return BFSurfaceType.Mud;
        if (key.StartsWith("yav")) return BFSurfaceType.Mud;
        if (key.StartsWith("kas")) return BFSurfaceType.Mud;
        if (key.StartsWith("dag")) return BFSurfaceType.Mud;
        if (key.StartsWith("fel")) return BFSurfaceType.Mud;

        // Meadow.
        if (key.StartsWith("nab")) return BFSurfaceType.Grass;

        // Stone and volcanic rock, which take no prints - naming them still
        // matters, because it is what stops a bolt on Mustafar throwing up
        // the same debris as one in a snowdrift.
        if (key.StartsWith("mus")) return BFSurfaceType.Rock;
        if (key.StartsWith("uta")) return BFSurfaceType.Rock;
        if (key.StartsWith("myg")) return BFSurfaceType.Rock;
        if (key.StartsWith("rhn")) return BFSurfaceType.Rock;
        if (key.StartsWith("pol")) return BFSurfaceType.Snow;

        return BFSurfaceType.Unknown;
    }

    /// <summary>Surface at a world position, or Unknown if unavailable.</summary>
    public static BFSurfaceType Sample(Vector3 worldPosition)
    {
        if (!IsLoaded) return BFSurfaceType.Unknown;

        // Same world-space mapping the terrain shader uses for its blend
        // textures: (worldPos.xz + extent/2) / extent. Keeping the two in step
        // is what makes the sampled type match what is actually drawn there.
        int u = Mathf.Clamp((int)(((worldPosition.x + WorldExtent * 0.5f) / WorldExtent) * Dimension),
                            0, Dimension - 1);
        int v = Mathf.Clamp((int)(((worldPosition.z + WorldExtent * 0.5f) / WorldExtent) * Dimension),
                            0, Dimension - 1);

        // The raw blend buffer is not in UV order. WorldLoader writes texel
        // (w, h) of the source to image pixel (dim-1-w, h), so the row is
        // mirrored relative to V - undo exactly that to get back to the raw
        // index this map is built over. Getting this backwards is invisible on
        // a symmetric map and puts snow on the ridges everywhere else.
        int raw = (Dimension - 1 - v) * Dimension + u;
        BFSurfaceType type = Types[raw];

        // Unknown here means the layer name said nothing, which on stock data
        // is every texel of every map - so this is the branch that actually
        // decides what Hoth and Tatooine are made of.
        return type != BFSurfaceType.Unknown ? type : PlanetGround();
    }
}
