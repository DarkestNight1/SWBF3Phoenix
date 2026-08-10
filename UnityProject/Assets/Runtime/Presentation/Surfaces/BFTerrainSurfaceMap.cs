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
        return Types[raw];
    }
}
