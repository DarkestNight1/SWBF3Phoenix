using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// One authored key/value pair as it appears in the source data.
///
/// The native layer hands back FNV hashes rather than names, because that is
/// how the munged data stores them. The hash is kept as the identity and the
/// name is resolved where we know it (<see cref="BFPropertyNames"/>), so an
/// unrecognised property is still preserved rather than dropped.
/// </summary>
public readonly struct BFProperty
{
    public readonly uint Hash;
    public readonly string Name;
    public readonly string Value;

    public BFProperty(uint hash, string name, string value)
    {
        Hash = hash;
        Name = name ?? string.Empty;
        Value = value ?? string.Empty;
    }

    public bool IsNamed => !string.IsNullOrEmpty(Name);
    public override string ToString() => (IsNamed ? Name : $"0x{Hash:X8}") + " = " + Value;
}

/// <summary>Authored placement of an object in a world layer.</summary>
public sealed class BFInstanceDefinition
{
    public readonly BFSourceRef Source;
    public readonly string Name;
    public readonly string EntityClassName;

    /// <summary>Immediate base class from the odf, e.g. "prop" or "soldier".</summary>
    public readonly string BaseClassName;

    /// <summary>
    /// Base class at the root of the odf inheritance chain - the one the class
    /// registry actually dispatches on. A map odf usually derives from another
    /// odf, so <see cref="BaseClassName"/> alone does not say what this is.
    /// </summary>
    public readonly string RootBaseClassName;

    public readonly Vector3 Position;
    public readonly Quaternion Rotation;
    public readonly IReadOnlyList<BFProperty> OverriddenProperties;

    public BFInstanceDefinition(BFSourceRef source, string name, string entityClassName,
                                string baseClassName, string rootBaseClassName,
                                Vector3 position, Quaternion rotation,
                                IReadOnlyList<BFProperty> overriddenProperties)
    {
        Source = source;
        Name = name ?? string.Empty;
        EntityClassName = entityClassName ?? string.Empty;
        BaseClassName = baseClassName ?? string.Empty;
        RootBaseClassName = rootBaseClassName ?? string.Empty;
        Position = position;
        Rotation = rotation;
        OverriddenProperties = overriddenProperties ?? Array.Empty<BFProperty>();
    }

    public string GetProperty(string name, string fallback = "")
    {
        for (int i = 0; i < OverriddenProperties.Count; ++i)
        {
            if (string.Equals(OverriddenProperties[i].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return OverriddenProperties[i].Value;
            }
        }
        return fallback;
    }
}

/// <summary>An odf class as authored, independent of whether we can run it.</summary>
public sealed class BFEntityClassDefinition
{
    public readonly BFSourceRef Source;
    public readonly string Name;
    public readonly string BaseClassName;
    public readonly string RootBaseClassName;
    public readonly string GeometryName;

    /// <summary>How many placed instances in this map use the class.</summary>
    public int InstanceCount { get; internal set; }

    /// <summary>
    /// True once <see cref="PhxClassRegister"/> is known to have a runtime
    /// type for the root base class. A class that parses but has no registry
    /// entry is data that can never become behaviour, which is exactly the
    /// gap the validation report exists to surface.
    /// </summary>
    public bool HasRuntimeType { get; internal set; }

    public BFEntityClassDefinition(BFSourceRef source, string name, string baseClassName,
                                   string rootBaseClassName, string geometryName)
    {
        Source = source;
        Name = name ?? string.Empty;
        BaseClassName = baseClassName ?? string.Empty;
        RootBaseClassName = rootBaseClassName ?? string.Empty;
        GeometryName = geometryName ?? string.Empty;
    }
}

/// <summary>Authored trigger volume.</summary>
public sealed class BFRegionDefinition
{
    public readonly BFSourceRef Source;

    /// <summary>Chunk name, which is not necessarily what scripts address.</summary>
    public readonly string Name;

    /// <summary>
    /// The name from the region's own "Name" property when it has one - this
    /// is what Lua's GetRegion resolves against.
    /// </summary>
    public readonly string ScriptName;

    public readonly string Type;
    public readonly Vector3 Position;
    public readonly Quaternion Rotation;
    public readonly Vector3 Size;
    public readonly IReadOnlyList<BFProperty> Properties;

    public BFRegionDefinition(BFSourceRef source, string name, string scriptName, string type,
                              Vector3 position, Quaternion rotation, Vector3 size,
                              IReadOnlyList<BFProperty> properties)
    {
        Source = source;
        Name = name ?? string.Empty;
        ScriptName = string.IsNullOrEmpty(scriptName) ? Name : scriptName;
        Type = type ?? string.Empty;
        Position = position;
        Rotation = rotation;
        Size = size;
        Properties = properties ?? Array.Empty<BFProperty>();
    }
}

/// <summary>Authored AI keep-out volume.</summary>
public sealed class BFBarrierDefinition
{
    public readonly BFSourceRef Source;
    public readonly string Name;
    public readonly Vector3 Position;
    public readonly Quaternion Rotation;
    public readonly Vector3 HalfExtents;
    public readonly uint Flags;

    public BFBarrierDefinition(BFSourceRef source, string name, Vector3 position,
                               Quaternion rotation, Vector3 halfExtents, uint flags)
    {
        Source = source;
        Name = name ?? string.Empty;
        Position = position;
        Rotation = rotation;
        HalfExtents = halfExtents;
        Flags = flags;
    }
}

/// <summary>Authored tactical annotation.</summary>
public sealed class BFHintNodeDefinition
{
    public readonly BFSourceRef Source;
    public readonly string Name;

    /// <summary>Raw uint16 from the chunk, kept because its encoding is not settled.</summary>
    public readonly int RawType;
    public readonly Vector3 Position;
    public readonly Quaternion Rotation;
    public readonly IReadOnlyList<BFProperty> Properties;

    public BFHintNodeDefinition(BFSourceRef source, string name, int rawType,
                                Vector3 position, Quaternion rotation,
                                IReadOnlyList<BFProperty> properties)
    {
        Source = source;
        Name = name ?? string.Empty;
        RawType = rawType;
        Position = position;
        Rotation = rotation;
        Properties = properties ?? Array.Empty<BFProperty>();
    }
}

/// <summary>
/// Rendering-independent view of an authored terrain.
///
/// Distinct from <c>BFTerrainMetadata</c>, which is the serialized component
/// left on the imported mesh so an editor-saved prefab keeps its provenance
/// without a live database.
/// </summary>
public sealed class BFTerrainDefinition
{
    public readonly BFSourceRef Source;
    public readonly IReadOnlyList<string> LayerTextureNames;
    public readonly uint HeightMapDimension;
    public readonly uint HeightMapScale;
    public readonly uint BlendMapDimension;
    public readonly uint BlendLayerCount;
    public readonly float HeightLowerBound;
    public readonly float HeightUpperBound;
    public readonly bool HasBakedVertexColor;

    public BFTerrainDefinition(BFSourceRef source, IReadOnlyList<string> layerTextureNames,
                               uint heightMapDimension, uint heightMapScale,
                               uint blendMapDimension, uint blendLayerCount,
                               float heightLowerBound, float heightUpperBound,
                               bool hasBakedVertexColor)
    {
        Source = source;
        LayerTextureNames = layerTextureNames ?? Array.Empty<string>();
        HeightMapDimension = heightMapDimension;
        HeightMapScale = heightMapScale;
        BlendMapDimension = blendMapDimension;
        BlendLayerCount = blendLayerCount;
        HeightLowerBound = heightLowerBound;
        HeightUpperBound = heightUpperBound;
        HasBakedVertexColor = hasBakedVertexColor;
    }

    /// <summary>World extent of one terrain side, in metres.</summary>
    public float WorldExtent => HeightMapDimension * HeightMapScale;
}

/// <summary>Authored light from a world layer's .lgt config.</summary>
public sealed class BFLightDefinition
{
    public readonly BFSourceRef Source;
    public readonly string Name;

    /// <summary>1 directional, 2 point, 3 spot - the encoding used by .lgt.</summary>
    public readonly int Type;
    public readonly Vector3 Position;
    public readonly Quaternion Rotation;
    public readonly Color Color;
    public readonly float Range;
    public readonly Vector2 Cone;

    /// <summary>Named in the config's GlobalLights block (Light1 / Light2).</summary>
    public readonly bool IsGlobal;

    /// <summary>Region this light is gated on, empty when unconditional.</summary>
    public readonly string RegionName;

    public BFLightDefinition(BFSourceRef source, string name, int type, Vector3 position,
                             Quaternion rotation, Color color, float range, Vector2 cone,
                             bool isGlobal, string regionName)
    {
        Source = source;
        Name = name ?? string.Empty;
        Type = type;
        Position = position;
        Rotation = rotation;
        Color = color;
        Range = range;
        Cone = cone;
        IsGlobal = isGlobal;
        RegionName = regionName ?? string.Empty;
    }
}

/// <summary>One node of an authored path.</summary>
public readonly struct BFPathNode
{
    public readonly Vector3 Position;
    public readonly Quaternion Rotation;
    public readonly float Knot;
    public readonly float Time;
    public readonly float PauseTime;

    public BFPathNode(Vector3 position, Quaternion rotation, float knot, float time, float pauseTime)
    {
        Position = position;
        Rotation = rotation;
        Knot = knot;
        Time = time;
        PauseTime = pauseTime;
    }
}

/// <summary>Authored path (spawn paths, vehicle routes, camera rails).</summary>
public sealed class BFPathDefinition
{
    public readonly BFSourceRef Source;
    public readonly string Name;
    public readonly IReadOnlyList<BFPathNode> Nodes;

    public BFPathDefinition(BFSourceRef source, string name, IReadOnlyList<BFPathNode> nodes)
    {
        Source = source;
        Name = name ?? string.Empty;
        Nodes = nodes ?? Array.Empty<BFPathNode>();
    }
}

/// <summary>A planning-graph waypoint.</summary>
public sealed class BFPlanningHubDefinition
{
    public readonly BFSourceRef Source;
    public readonly string Name;
    public readonly Vector3 Position;
    public readonly float Radius;

    /// <summary>Per-layer arc counts, needed to index the branch-weight table.</summary>
    public readonly IReadOnlyList<byte> ConnectionsPerLayer;
    public readonly IReadOnlyList<byte> ConnectionIndices;
    public readonly IReadOnlyList<byte> QuantizedWeights;

    // A map may ship several PlanSets and we flatten them into one graph, but
    // the branch-weight buffer is indexed against the hub and arc numbering of
    // the set it came from. Keeping the set's origin and size next to the hub
    // is what lets that buffer still be decoded after flattening.
    public readonly int SetHubBase;
    public readonly int SetHubCount;
    public readonly int SetArcBase;

    public BFPlanningHubDefinition(BFSourceRef source, string name, Vector3 position, float radius,
                                   IReadOnlyList<byte> connectionsPerLayer,
                                   IReadOnlyList<byte> connectionIndices,
                                   IReadOnlyList<byte> quantizedWeights,
                                   int setHubBase, int setHubCount, int setArcBase)
    {
        Source = source;
        Name = name ?? string.Empty;
        Position = position;
        Radius = radius;
        ConnectionsPerLayer = connectionsPerLayer ?? Array.Empty<byte>();
        ConnectionIndices = connectionIndices ?? Array.Empty<byte>();
        QuantizedWeights = quantizedWeights ?? Array.Empty<byte>();
        SetHubBase = setHubBase;
        SetHubCount = setHubCount;
        SetArcBase = setArcBase;
    }
}

/// <summary>A planning-graph arc, exactly as authored (before mirroring).</summary>
public sealed class BFPlanningArcDefinition
{
    public readonly BFSourceRef Source;
    public readonly string Name;
    public readonly int StartHub;
    public readonly int EndHub;
    public readonly uint FilterFlags;
    public readonly uint AttributeFlags;

    public BFPlanningArcDefinition(BFSourceRef source, string name, int startHub, int endHub,
                                   uint filterFlags, uint attributeFlags)
    {
        Source = source;
        Name = name ?? string.Empty;
        StartHub = startHub;
        EndHub = endHub;
        FilterFlags = filterFlags;
        AttributeFlags = attributeFlags;
    }
}

/// <summary>Everything one world layer contributes to the map.</summary>
public sealed class BFWorldDefinition
{
    public readonly BFSourceRef Source;
    public readonly string Name;
    public readonly string SkydomeName;

    public readonly IReadOnlyList<BFInstanceDefinition> Instances;
    public readonly IReadOnlyList<BFRegionDefinition> Regions;
    public readonly IReadOnlyList<BFBarrierDefinition> Barriers;
    public readonly IReadOnlyList<BFHintNodeDefinition> HintNodes;

    /// <summary>
    /// Lights of this layer's .lgt config.
    /// </summary>
    /// <remarks>
    /// Filled by a second capture call rather than the constructor: lights are
    /// not children of the World chunk but of a config looked up by world
    /// name, which the importer resolves later in the load. The list is
    /// assigned once and never mutated afterwards, so consumers holding a
    /// world definition still see a stable record.
    /// </remarks>
    public IReadOnlyList<BFLightDefinition> Lights { get; private set; }

    /// <summary>Null when the layer contributes no terrain, which is normal.</summary>
    public readonly BFTerrainDefinition Terrain;

    public BFWorldDefinition(BFSourceRef source, string name, string skydomeName,
                             IReadOnlyList<BFInstanceDefinition> instances,
                             IReadOnlyList<BFRegionDefinition> regions,
                             IReadOnlyList<BFBarrierDefinition> barriers,
                             IReadOnlyList<BFHintNodeDefinition> hintNodes,
                             BFTerrainDefinition terrain)
    {
        Source = source;
        Name = name ?? string.Empty;
        SkydomeName = skydomeName ?? string.Empty;
        Instances = instances ?? Array.Empty<BFInstanceDefinition>();
        Regions = regions ?? Array.Empty<BFRegionDefinition>();
        Barriers = barriers ?? Array.Empty<BFBarrierDefinition>();
        HintNodes = hintNodes ?? Array.Empty<BFHintNodeDefinition>();
        Lights = Array.Empty<BFLightDefinition>();
        Terrain = terrain;
    }

    /// <summary>Capture-time only; see <see cref="Lights"/>.</summary>
    public void AssignLights(IReadOnlyList<BFLightDefinition> lights)
    {
        Lights = lights ?? Array.Empty<BFLightDefinition>();
    }

    /// <summary>The light record with this name, or null.</summary>
    public BFLightDefinition FindLight(string name)
    {
        for (int i = 0; i < Lights.Count; ++i)
        {
            if (string.Equals(Lights[i].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return Lights[i];
            }
        }
        return null;
    }
}
