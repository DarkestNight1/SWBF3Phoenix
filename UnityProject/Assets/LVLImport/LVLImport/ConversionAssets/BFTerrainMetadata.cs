using System;
using UnityEngine;

/// <summary>
/// Source metadata serialized onto an imported terrain mesh.
///
/// The authoritative record of an authored terrain is
/// <c>BFTerrainDefinition</c> in the runtime source database, which holds the
/// full TERR description and is rebuilt on every map load. This component is
/// the scene-side copy of the parts a saved prefab has to survive with: an
/// editor-imported terrain lives on long after the database that produced it
/// is gone, and rendering code may be replaced without re-reading the LVL.
/// </summary>
public sealed class BFTerrainMetadata : MonoBehaviour
{
    [SerializeField] string sourceName;
    [SerializeField] int layerCount;
    [SerializeField] float[] tileRanges = Array.Empty<float>();
    [SerializeField] bool hasBakedVertexColor;

    public string SourceName => sourceName;
    public int LayerCount => layerCount;
    public float[] TileRanges => (float[])tileRanges.Clone();
    public bool HasBakedVertexColor => hasBakedVertexColor;

    public void Initialize(string terrainName, int layers, float[] authoredTileRanges, bool bakedVertexColor)
    {
        sourceName = terrainName ?? string.Empty;
        layerCount = layers;
        tileRanges = authoredTileRanges == null ? Array.Empty<float>() : (float[])authoredTileRanges.Clone();
        hasBakedVertexColor = bakedVertexColor;
    }
}
