using System;
using System.Collections.Generic;
using UnityEngine;
using LibSWBF2.Wrappers;
using LibSWBF2.Utils;

using LibTerrain = LibSWBF2.Wrappers.Terrain;
using LibVec3 = LibSWBF2.Types.Vector3;
using LibVec2 = LibSWBF2.Types.Vector2;

/// <summary>
/// The semantic representation of the BF2 data mounted for a map.
///
/// This exists because a Unity scene is a lossy, renderer-shaped projection of
/// the source: once an instance has become a GameObject with a MeshRenderer,
/// the odf it came from, the properties it overrode and the fact that four
/// hundred others share its class are all gone. Anything that wants to
/// re-render the world differently, validate that the import was complete,
/// diff a mod against stock data or report what a map contains has to re-read
/// the LVL - or, with this, read a typed record.
///
/// Capture is deliberately additive: it walks the same wrappers the importers
/// walk and never influences what they build. Import order, object identity
/// and behaviour are unchanged whether or not a database is active.
/// </summary>
public sealed class BFSourceDatabase
{
    /// <summary>
    /// Database for the map currently being loaded or played.
    ///
    /// Static because the importers that need to report into it
    /// (ClassLoader, ModelLoader, WorldLoader) are process-wide singletons
    /// with no route to the PhxScene that owns the map.
    /// </summary>
    public static BFSourceDatabase Active { get; private set; } = new BFSourceDatabase();

    /// <summary>Start a fresh capture for a new map load.</summary>
    public static BFSourceDatabase BeginCapture(string levelName)
    {
        Active = new BFSourceDatabase { LevelName = levelName ?? string.Empty };
        return Active;
    }

    public string LevelName { get; private set; } = string.Empty;

    readonly List<BFWorldDefinition> worlds = new List<BFWorldDefinition>();
    readonly Dictionary<string, BFEntityClassDefinition> classes =
        new Dictionary<string, BFEntityClassDefinition>(StringComparer.OrdinalIgnoreCase);
    readonly List<BFPlanningHubDefinition> planningHubs = new List<BFPlanningHubDefinition>();
    readonly List<BFPlanningArcDefinition> planningArcs = new List<BFPlanningArcDefinition>();
    readonly Dictionary<string, BFPathDefinition> paths =
        new Dictionary<string, BFPathDefinition>(StringComparer.OrdinalIgnoreCase);

    // Source ids that produced something in the Unity scene. Kept separate
    // from the definitions so "was it authored" and "did we build it" stay
    // independently answerable - collapsing them is what makes silent import
    // loss invisible.
    readonly HashSet<string> imported = new HashSet<string>();
    readonly Dictionary<BFSourceKind, int> importedByKind = new Dictionary<BFSourceKind, int>();

    public IReadOnlyList<BFWorldDefinition> Worlds => worlds;
    public IReadOnlyDictionary<string, BFEntityClassDefinition> Classes => classes;
    public IReadOnlyList<BFPlanningHubDefinition> PlanningHubs => planningHubs;
    public IReadOnlyList<BFPlanningArcDefinition> PlanningArcs => planningArcs;
    public IReadOnlyCollection<BFPathDefinition> Paths => paths.Values;

    public bool IsEmpty => worlds.Count == 0 && classes.Count == 0;

    // ------------------------------------------------------------- capturing

    /// <summary>
    /// Record one world layer. Safe to call for every layer of a map; each
    /// produces its own definition, mirroring how layers stack at runtime.
    /// </summary>
    public BFWorldDefinition CaptureWorld(World world)
    {
        if (world == null) return null;

        string worldName = world.Name ?? string.Empty;
        BFSourceRef worldRef = Ref(worldName, BFSourceKind.World, worldName, worlds.Count);

        var definition = new BFWorldDefinition(
            worldRef, worldName, world.SkydomeName,
            CaptureInstances(world, worldName),
            CaptureRegions(world, worldName),
            CaptureBarriers(world, worldName),
            CaptureHintNodes(world, worldName),
            CaptureTerrain(world, worldName));

        worlds.Add(definition);
        return definition;
    }

    List<BFInstanceDefinition> CaptureInstances(World world, string worldName)
    {
        var result = new List<BFInstanceDefinition>();

        Instance[] instances;
        try { instances = world.GetInstances(); }
        catch (Exception e)
        {
            Debug.LogWarning($"[BFSource] Could not enumerate instances of '{worldName}': {e.Message}");
            return result;
        }
        if (instances == null) return result;

        for (int i = 0; i < instances.Length; ++i)
        {
            Instance inst = instances[i];
            if (inst == null) continue;

            try
            {
                EntityClass ec = inst.EntityClass;
                EntityClass root = ec == null ? null : ClassLoader.GetRootClass(ec);
                string rootBase = root == null ? string.Empty : root.BaseClassName ?? string.Empty;

                inst.GetOverriddenProperties(out uint[] hashes, out string[] values);

                var definition = new BFInstanceDefinition(
                    Ref(worldName, BFSourceKind.Instance, inst.Name, i),
                    inst.Name, inst.EntityClassName,
                    ec == null ? string.Empty : ec.BaseClassName,
                    rootBase,
                    UnityUtils.Vec3FromLibWorld(inst.Position),
                    UnityUtils.QuatFromLibWorld(inst.Rotation),
                    BFPropertyNames.Pair(hashes, values));

                result.Add(definition);
                NoteClass(worldName, ec, definition.EntityClassName, rootBase);
            }
            catch (Exception e)
            {
                // One unreadable instance must not cost the rest of the world's
                // record; the gap shows up as a count mismatch in the report,
                // which is exactly the signal we want.
                Debug.LogWarning($"[BFSource] Instance {i} of '{worldName}' could not be captured: {e.Message}");
            }
        }
        return result;
    }

    void NoteClass(string worldName, EntityClass ec, string className, string rootBaseClassName)
    {
        if (string.IsNullOrEmpty(className)) return;

        if (classes.TryGetValue(className, out BFEntityClassDefinition known))
        {
            known.InstanceCount++;
            return;
        }

        string geometry = string.Empty;
        if (ec != null && ec.GetProperty("GeometryName", out string geom))
        {
            geometry = geom;
        }

        var definition = new BFEntityClassDefinition(
            Ref(worldName, BFSourceKind.EntityClass, className, classes.Count),
            className,
            ec == null ? string.Empty : ec.BaseClassName,
            rootBaseClassName,
            geometry);
        definition.InstanceCount = 1;
        // "Registered", not "has an instance type": ordnance classes are
        // registered with a null instance type on purpose because projectiles
        // are pooled rather than instantiated, and counting those as gaps
        // would swamp the report's list of classes that genuinely do nothing.
        definition.HasRuntimeType = PhxClassRegister.IsRegistered(rootBaseClassName);
        classes.Add(className, definition);
    }

    List<BFRegionDefinition> CaptureRegions(World world, string worldName)
    {
        var result = new List<BFRegionDefinition>();

        LibSWBF2.Wrappers.Region[] regions;
        try { regions = world.GetRegions(); }
        catch (Exception e)
        {
            Debug.LogWarning($"[BFSource] Could not enumerate regions of '{worldName}': {e.Message}");
            return result;
        }
        if (regions == null) return result;

        uint nameHash = HashUtils.GetFNV("Name");
        for (int i = 0; i < regions.Length; ++i)
        {
            LibSWBF2.Wrappers.Region region = regions[i];
            if (region == null) continue;

            try
            {
                region.GetProperties(out uint[] hashes, out string[] values);
                List<BFProperty> props = BFPropertyNames.Pair(hashes, values);

                string scriptName = region.Name;
                for (int p = 0; p < props.Count; ++p)
                {
                    if (props[p].Hash == nameHash) scriptName = props[p].Value;
                }

                result.Add(new BFRegionDefinition(
                    Ref(worldName, BFSourceKind.Region, region.Name, i),
                    region.Name, scriptName, region.Type,
                    UnityUtils.Vec3FromLibWorld(region.Position),
                    UnityUtils.QuatFromLibWorld(region.Rotation),
                    new Vector3(region.Size.X, region.Size.Y, region.Size.Z),
                    props));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[BFSource] Region {i} of '{worldName}' could not be captured: {e.Message}");
            }
        }
        return result;
    }

    List<BFBarrierDefinition> CaptureBarriers(World world, string worldName)
    {
        var result = new List<BFBarrierDefinition>();

        Barrier[] barriers;
        try { barriers = world.GetBarriers(); }
        catch (Exception e)
        {
            Debug.LogWarning($"[BFSource] Could not enumerate barriers of '{worldName}': {e.Message}");
            return result;
        }
        if (barriers == null) return result;

        for (int i = 0; i < barriers.Length; ++i)
        {
            Barrier b = barriers[i];
            if (b == null) continue;

            try
            {
                Vector3 size = UnityUtils.Vec3FromLibWorld(b.Size);
                result.Add(new BFBarrierDefinition(
                    Ref(worldName, BFSourceKind.Barrier, b.Name, i),
                    b.Name,
                    UnityUtils.Vec3FromLibWorld(b.Position),
                    UnityUtils.QuatFromLibWorld(b.Rotation),
                    new Vector3(Mathf.Abs(size.x), Mathf.Abs(size.y), Mathf.Abs(size.z)),
                    b.Flag));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[BFSource] Barrier {i} of '{worldName}' could not be captured: {e.Message}");
            }
        }
        return result;
    }

    List<BFHintNodeDefinition> CaptureHintNodes(World world, string worldName)
    {
        var result = new List<BFHintNodeDefinition>();

        HintNode[] hints;
        try { hints = world.GetHintNodes(); }
        catch (Exception e)
        {
            Debug.LogWarning($"[BFSource] Could not enumerate hint nodes of '{worldName}': {e.Message}");
            return result;
        }
        if (hints == null) return result;

        for (int i = 0; i < hints.Length; ++i)
        {
            HintNode h = hints[i];
            if (h == null) continue;

            try
            {
                h.GetProperties(out uint[] hashes, out string[] values);
                result.Add(new BFHintNodeDefinition(
                    Ref(worldName, BFSourceKind.HintNode, h.Name, i),
                    h.Name, (int)h.Type,
                    UnityUtils.Vec3FromLibWorld(h.Position),
                    UnityUtils.QuatFromLibWorld(h.Rotation),
                    BFPropertyNames.Pair(hashes, values)));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[BFSource] Hint node {i} of '{worldName}' could not be captured: {e.Message}");
            }
        }
        return result;
    }

    BFTerrainDefinition CaptureTerrain(World world, string worldName)
    {
        LibTerrain terrain;
        try { terrain = world.GetTerrain(); }
        catch (Exception e)
        {
            Debug.LogWarning($"[BFSource] Could not read terrain of '{worldName}': {e.Message}");
            return null;
        }
        if (terrain == null) return null;

        try
        {
            terrain.GetHeightMap(out uint dim, out uint dimScale, out _);
            terrain.GetBlendMap(out uint blendDim, out uint numLayers, out _);

            var layerNames = new List<string>();
            if (terrain.LayerTextures != null)
            {
                layerNames.AddRange(terrain.LayerTextures);
            }

            byte[] colors = terrain.GetColorBuffer();

            return new BFTerrainDefinition(
                Ref(worldName, BFSourceKind.Terrain, worldName, 0),
                layerNames, dim, dimScale, blendDim, numLayers,
                terrain.HeightLowerBound, terrain.HeightUpperBound,
                colors != null && colors.Length > 0);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[BFSource] Terrain of '{worldName}' could not be captured: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Record the lights of a world layer from its .lgt config.
    /// </summary>
    /// <remarks>
    /// Lights are not children of the World wrapper - they live in a separate
    /// config the importer looks up by world name - so they are captured in a
    /// second call rather than inside <see cref="CaptureWorld"/>. The world
    /// definition already exists at that point, so its light list is replaced
    /// wholesale by a rebuilt definition.
    /// </remarks>
    public BFWorldDefinition CaptureLighting(string worldName, Config lightingConfig)
    {
        int index = worlds.FindIndex(w => string.Equals(w.Name, worldName, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return null;
        if (lightingConfig == null) return worlds[index];

        var lights = new List<BFLightDefinition>();
        try
        {
            string light1 = string.Empty, light2 = string.Empty;
            Field globals = lightingConfig.GetField("GlobalLights");
            if (globals != null)
            {
                light1 = globals.Scope.GetField("Light1")?.GetString() ?? string.Empty;
                light2 = globals.Scope.GetField("Light2")?.GetString() ?? string.Empty;
            }

            Field[] lightFields = lightingConfig.GetFields("Light");
            if (lightFields != null)
            {
                for (int i = 0; i < lightFields.Length; ++i)
                {
                    string name = lightFields[i].GetString();
                    Scope sl = lightFields[i].Scope;

                    // The authored position, with none of the importer's
                    // presentation fudges (the extra Z mirror and +0.2 Y lift
                    // in WorldLoader.ImportLights). A source record has to say
                    // what the level designer placed; workarounds for how it
                    // reads in a different renderer belong to the renderer.
                    LibVec3 pos = sl.GetVec3("Position");
                    LibVec3 col = sl.GetVec3("Color");
                    LibVec2 cone = sl.GetVec2("Cone");

                    bool isGlobal = string.Equals(name, light1, StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(name, light2, StringComparison.OrdinalIgnoreCase);

                    lights.Add(new BFLightDefinition(
                        Ref(worldName, BFSourceKind.Light, name, i),
                        name, (int)sl.GetFloat("Type"),
                        UnityUtils.Vec3FromLibWorld(pos),
                        UnityUtils.QuatFromLibLGT(sl.GetVec4("Rotation")),
                        new Color(col.X, col.Y, col.Z),
                        sl.GetFloat("Range"),
                        new Vector2(cone.X, cone.Y),
                        isGlobal,
                        sl.GetString("Region")));
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[BFSource] Lighting of '{worldName}' could not be captured: {e.Message}");
            return worlds[index];
        }

        worlds[index].AssignLights(lights);
        return worlds[index];
    }

    /// <summary>Record the map's planning graph exactly as authored.</summary>
    public void CapturePlanning(Level worldLevel)
    {
        if (worldLevel == null) return;

        PlanSet[] planSets;
        try { planSets = worldLevel.Get<PlanSet>(); }
        catch (Exception e)
        {
            Debug.LogWarning($"[BFSource] Could not read planning graph: {e.Message}");
            return;
        }
        if (planSets == null) return;

        foreach (PlanSet set in planSets)
        {
            int hubBase = planningHubs.Count;
            int arcBase = planningArcs.Count;

            Hub[] hubs = set.GetHubs();
            int hubCount = hubs == null ? 0 : hubs.Length;

            for (int i = 0; i < hubCount; ++i)
            {
                Hub h = hubs[i];
                planningHubs.Add(new BFPlanningHubDefinition(
                    Ref(string.Empty, BFSourceKind.PlanningHub, h.Name, hubBase + i),
                    h.Name, UnityUtils.Vec3FromLibWorld(h.Position), h.Radius,
                    h.ConnectionsPerLayer, h.ConnectionIndices, h.QuantizedWeights,
                    hubBase, hubCount, arcBase));
            }

            Connection[] connections = set.GetConnections();
            if (connections != null)
            {
                for (int i = 0; i < connections.Length; ++i)
                {
                    Connection c = connections[i];
                    planningArcs.Add(new BFPlanningArcDefinition(
                        Ref(string.Empty, BFSourceKind.PlanningArc, c.Name, planningArcs.Count),
                        c.Name, hubBase + c.Start, hubBase + c.End,
                        (uint)c.FilterFlags, (uint)c.AttributeFlags));
                }
            }
        }
    }

    /// <summary>Record a path the moment the importer resolves one.</summary>
    public BFPathDefinition CapturePath(string name, IReadOnlyList<BFPathNode> nodes)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (paths.TryGetValue(name, out BFPathDefinition known)) return known;

        var definition = new BFPathDefinition(
            Ref(string.Empty, BFSourceKind.Path, name, paths.Count), name, nodes);
        paths.Add(name, definition);
        return definition;
    }

    // ------------------------------------------------------- import tracking

    /// <summary>
    /// Note that a source record produced this Unity object, and give the
    /// object a way back to its record.
    /// </summary>
    public void RegisterImported(GameObject gameObject, BFSourceRef source)
    {
        if (source == null) return;

        MarkImported(source);

        if (gameObject == null) return;
        BFSourceLink link = gameObject.GetComponent<BFSourceLink>();
        if (link == null) link = gameObject.AddComponent<BFSourceLink>();
        link.Source = source;
    }

    /// <summary>Note an import with no single GameObject to attach to.</summary>
    public void MarkImported(BFSourceRef source)
    {
        if (source == null || !imported.Add(source.Id)) return;

        importedByKind.TryGetValue(source.Kind, out int count);
        importedByKind[source.Kind] = count + 1;
    }

    public bool WasImported(BFSourceRef source) => source != null && imported.Contains(source.Id);

    public int ImportedCount(BFSourceKind kind)
    {
        importedByKind.TryGetValue(kind, out int count);
        return count;
    }

    // -------------------------------------------------------------- querying

    public int SourceCount(BFSourceKind kind)
    {
        switch (kind)
        {
            case BFSourceKind.World: return worlds.Count;
            case BFSourceKind.EntityClass: return classes.Count;
            case BFSourceKind.PlanningHub: return planningHubs.Count;
            case BFSourceKind.PlanningArc: return planningArcs.Count;
            case BFSourceKind.Path: return paths.Count;
        }

        int total = 0;
        for (int i = 0; i < worlds.Count; ++i)
        {
            BFWorldDefinition w = worlds[i];
            switch (kind)
            {
                case BFSourceKind.Instance: total += w.Instances.Count; break;
                case BFSourceKind.Region: total += w.Regions.Count; break;
                case BFSourceKind.Barrier: total += w.Barriers.Count; break;
                case BFSourceKind.HintNode: total += w.HintNodes.Count; break;
                case BFSourceKind.Light: total += w.Lights.Count; break;
                case BFSourceKind.Terrain: if (w.Terrain != null) total++; break;
            }
        }
        return total;
    }

    /// <summary>Every placed instance across every captured world layer.</summary>
    public IEnumerable<BFInstanceDefinition> AllInstances()
    {
        for (int i = 0; i < worlds.Count; ++i)
        {
            IReadOnlyList<BFInstanceDefinition> instances = worlds[i].Instances;
            for (int j = 0; j < instances.Count; ++j)
            {
                yield return instances[j];
            }
        }
    }

    public BFEntityClassDefinition GetClass(string className)
    {
        if (string.IsNullOrEmpty(className)) return null;
        return classes.TryGetValue(className, out BFEntityClassDefinition def) ? def : null;
    }

    /// <summary>The authored terrain of whichever layer supplies one.</summary>
    public BFTerrainDefinition GetTerrain()
    {
        for (int i = 0; i < worlds.Count; ++i)
        {
            if (worlds[i].Terrain != null) return worlds[i].Terrain;
        }
        return null;
    }

    BFSourceRef Ref(string worldName, BFSourceKind kind, string name, int ordinal) =>
        new BFSourceRef(LevelName, worldName, kind, name, ordinal);
}
