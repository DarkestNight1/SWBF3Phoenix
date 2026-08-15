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


    /// <summary>
    /// What a map's ground is made of, when its layer names will not say.
    /// </summary>
    /// <remarks>
    /// Deferred to <see cref="BFSurfaceQuery.MapDefault"/>, which
    /// <see cref="BFLightingDirector"/> seeds from the map's
    /// <see cref="BFEnvironmentLightingProfile.DominantSurface"/> on every load.
    ///
    /// This used to be a second prefix table living here, and the two disagreed
    /// about the same maps: end/yav/kas/dag/fel resolved to Mud here and Grass
    /// in the profile, pol to Snow here and Metal there. Both feed
    /// BFSurfaceQuery - the profile as the map-wide default, this as the
    /// per-texel terrain answer - so footsteps, impact debris and wetness could
    /// contradict each other on the same ground depending on which path asked.
    ///
    /// One table owns this now, and it is the profile, because that is already
    /// the per-map authority for everything else and is the thing a person
    /// tuning a map edits. Adding a map here meant remembering two places.
    /// </remarks>
    static BFSurfaceType PlanetGround()
    {
        return BFSurfaceQuery.MapDefault;
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
