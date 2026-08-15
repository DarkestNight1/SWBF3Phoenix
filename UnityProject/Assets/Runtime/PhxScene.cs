using System;
using System.Collections.Generic;
using UnityEngine;
using LibSWBF2.Wrappers;
using LibSWBF2.Enums;

#if UNITY_EDITOR
using UnityEditor;
using System.Reflection;
#endif


public struct PhxTransform
{
    public Vector3 Position;
    public Quaternion Rotation;
}

public class PhxScene
{
    public Texture2D MapTexture { get; private set; }

    /// <summary>
    /// The authored data this scene is a projection of.
    ///
    /// Anything that needs to know what the map contains - as opposed to what
    /// Unity happened to build from it - should read this rather than walk the
    /// scene graph.
    /// </summary>
    public BFSourceDatabase Source { get; private set; } = BFSourceDatabase.Active;

    List<PhxInstance>         Instances                = new List<PhxInstance>();

    // Subsets of 'Instances'
    List<IPhxTickable>        TickableInstances        = new List<IPhxTickable>();
    List<IPhxTickablePhysics> TickablePhysicsInstances = new List<IPhxTickablePhysics>();
    List<PhxCommandpost>      CommandPosts             = new List<PhxCommandpost>();
    GameObject                Vehicles                 = new GameObject("Vehicles");

    Dictionary<string, PhxClass> Classes     = new Dictionary<string, PhxClass>();
    Dictionary<string, int>      InstanceMap = new Dictionary<string, int>();

    PhxEnvironment ENV;
    Container EnvCon;
    bool bTerrainImported = false;

    Dictionary<string, GameObject> LoadedSkydomes = new Dictionary<string, GameObject>();
    Dictionary<string, PhxRegion>  Regions  = new Dictionary<string, PhxRegion>();

    List<GameObject>  WorldRoots = new List<GameObject>();

    // Camera positions
    List<PhxTransform> CameraShots = new List<PhxTransform>();
    int CurrCamIdx;
    const float CameraMaxDistance = 15.0f;
    Dictionary<PhxCommandpost, PhxTransform> CPCamPositions = new Dictionary<PhxCommandpost, PhxTransform>();

    PhxProjectiles Projectiles = new PhxProjectiles();
    public readonly PhxEffectsManager EffectsManager = new PhxEffectsManager();

    CraMain Cra;
    int InstanceCounter;


    public PhxSceneAnimator Animator { get; private set; }


    public PhxScene(PhxEnvironment env, Container c)
    {
        ENV = env;
        EnvCon = c;
        Cra = new CraMain();

        ModelLoader.Instance.PhyMat = PhxGame.Instance.GroundPhyMat;
        ENV.OnPostLoad += CalcCPCamPositions;

        // Must be cleared HERE, not in Import(): the map's ScriptInit runs
        // during RunMain() - before CreateScene()/Import() - and that is what
        // calls SpaceAssaultEnable/AddCriticalSystem/AddAIGoal. Resetting in
        // Import would wipe the configuration the script just supplied.
        PhxSpaceAssault.Reset();

        // Class objects do not survive a map load, so the "already scaled" set
        // must not either - a stale entry would leave the next map's identically
        // shaped class unscaled.
        PhxSoldier.ResetHealthScaling();
        PhxAIGoals.Reset();

        // Same reasoning as the two above: EnableSPHeroRules and
        // EnableSPScriptedHeroes are called from the map's ScriptInit, which
        // runs before Import(), so resetting hero state there would discard
        // what the script just configured. Combos are cached per map because
        // the mounted data changes with it.
        PhxHeroRules.Reset();
        PhxComboLoader.Reset();
        PhxAIDirectives.Reset();
        PhxCommandpost.ResetDiagnostics();

        // The bus holds its listener lists in statics, which outlive a match -
        // in a player build they outlive everything short of the process. A
        // listener registered last match is a closure over last match's
        // objects: it keeps them alive, and it gets invoked with destroyed
        // Unity references the moment the same event fires again. Nothing
        // subscribes yet, so this is currently free; it stops being free the
        // first time something does, and that is a failure which would look
        // like a bug in whatever subscribed rather than one here.
        BFEventBus.Reset();

        Animator = new PhxSceneAnimator();
    }

    /// <summary>
    /// Lua's SetProperty, applied to a named instance.
    /// </summary>
    /// <remarks>
    /// Falls back to the instance's odf class when the instance itself has no
    /// such property. In BF2 a property lookup walks the object's inheritance
    /// chain, so a script setting <c>MaxHealth</c> on a command post is setting
    /// something the odf declares even though the runtime instance holds only
    /// its own per-instance fields. Without the fallback every such call was a
    /// warning and did nothing - which is what happens for command post health
    /// on the stock conquest maps, six or more times per load.
    ///
    /// The class is shared, so this does affect every instance of that odf.
    /// That is the same reach the original property has, and it is strictly
    /// better than the previous behaviour of reaching nothing.
    /// </remarks>
    public void SetProperty(string instName, string propName, object propValue)
    {
        PhxInstance inst = FindInstance(instName);
        if (inst == null)
        {
            Debug.LogWarningFormat("Could not find instance '{0}' to set property '{1}'!", instName, propName);
            return;
        }

        if (inst.P.TrySetProperty(propName, propValue)) return;

        PhxClass odf = inst.GetClassRef();
        if (odf != null && odf.P.TrySetProperty(propName, propValue)) return;

        Debug.LogWarningFormat("Instance '{0}' and its class have no property '{1}'!", instName, propName);
    }

    PhxInstance FindInstance(string instName)
    {
        if (string.IsNullOrEmpty(instName)) return null;

        if (InstanceMap.TryGetValue(instName, out int instIdx) && Instances[instIdx] != null)
        {
            return Instances[instIdx];
        }
        if (InstanceMap.TryGetValue(instName.ToLower(), out instIdx) && Instances[instIdx] != null)
        {
            return Instances[instIdx];
        }
        return null;
    }

    public void SetClassProperty(string className, string propName, object propValue)
    {
        if (Classes.TryGetValue(className, out PhxClass cl))
        {
            cl.P.SetProperty(propName, propValue);
            return;
        }
        Debug.LogWarningFormat("Coukd not find odf class '{0}' to set class property '{1}'!", className, propName);
    }

    public bool IsObjectAlive(string instName)
    {
        if (InstanceMap.TryGetValue(instName, out int instIdx))
        {
            PhxInstance inst = Instances[instIdx];
            return inst != null && inst.gameObject.activeSelf;
        }
        return false;
    }

    // Scripts address regions either by name or by an opaque handle from
    // Lua's GetRegion(), and pass that handle straight back into calls such as
    // MapRemoveRegionMarker(GetRegion(...)). This list gives those handles a
    // stable meaning: the handle is the index, so it survives round-tripping.
    readonly List<string> RegionHandles = new List<string>();

    /// <summary>Handle for a region name, or -1 if there is no such region.</summary>
    public int GetRegionHandle(string name)
    {
        if (string.IsNullOrEmpty(name)) return -1;

        string key = name.ToLower();
        if (!Regions.ContainsKey(key)) return -1;

        int idx = RegionHandles.IndexOf(key);
        if (idx < 0)
        {
            RegionHandles.Add(key);
            idx = RegionHandles.Count - 1;
        }
        return idx;
    }

    /// <summary>Region name for a handle, or empty if the handle is unknown.</summary>
    public string GetRegionName(int handle)
    {
        if (handle < 0 || handle >= RegionHandles.Count) return "";
        return RegionHandles[handle];
    }

    public PhxRegion GetRegion(string name)
    {
        if (Regions.TryGetValue(name.ToLower(), out PhxRegion region))
        {
            return region;
        }
        return null;
    }

    public int? GetInstanceIndex(PhxInstance inst)
    {
        int idx = Instances.IndexOf(inst);
        return idx < 0 ? null : new int?(idx);
    }

    /// <summary>
    /// Instance by index, or null if the index is stale. Mission scripts pass
    /// these indices around as "character" handles, so out of range values
    /// arrive routinely and must not throw.
    /// </summary>
    public PhxInstance GetInstance(int idx)
    {
        if (idx < 0 || idx >= Instances.Count)
        {
            return null;
        }
        return Instances[idx];
    }

    public int? GetInstanceIndex(string instName)
    {
        if (InstanceMap.TryGetValue(instName, out int instIdx))
        {
            return instIdx;
        }
        return null;
    }

    public T GetInstance<T>(string instName) where T : PhxInstance
    {
        if (InstanceMap.TryGetValue(instName, out int instIdx))
        {
            return GetInstance<T>(instIdx);
        }

        Debug.LogWarning($"Cannot inf Instance '{instName}'!");
        return null;
    }

    public T GetInstance<T>(int instIdx) where T : PhxInstance
    {
        if (instIdx >= 0 && instIdx < Instances.Count)
        {
            return Instances[instIdx] as T;
        }

        Debug.LogWarning($"Instance index '{instIdx}' is out of bounds ({Instances.Count})!");
        return null;
    }

    public void AddCameraShot(Vector3 position, Quaternion rotation)
    {
        CameraShots.Add(new PhxTransform
        {
            Position = position,
            Rotation = rotation
        });
    }

    public PhxTransform GetNextCameraShot()
    {
        if (CameraShots.Count == 0)
        {
            return new PhxTransform
            {
                Position = Vector3.zero,
                Rotation = Quaternion.identity
            };
        }

        PhxTransform camShot = CameraShots[CurrCamIdx++];
        if (CurrCamIdx >= CameraShots.Count)
        {
            CurrCamIdx = 0;
        }
        return camShot;
    }


    public void FireProjectile(IPhxWeapon WeaponOfOrigin, 
                                PhxOrdnanceClass OrdnanceClass,
                                Vector3 Pos, Quaternion Rot)
    {
        Projectiles.FireProjectile(WeaponOfOrigin, OrdnanceClass, Pos, Rot);
    }


    public void Import(World[] worldLayers)
    {
        if (WorldRoots.Count > 0)
        {
            Debug.LogError("Create a new RuntimeScene instance!");
            return;
        }

        // Kept, despite PhxEnvironment.Run() having already reset the loaders
        // earlier in this same load. ModelLoader.ResetDB() also rebuilds
        // ModelDBRoot, and the previous Unity scene is torn down with an
        // ASYNC UnloadSceneAsync that can complete after Run() - so the root
        // Run() created may already be destroyed by the time we get here.
        // Removing this call cost us every imported model. The reset is only
        // safe to repeat because ResetDB() drops references without
        // destroying the assets themselves (see TextureLoader.ResetDB).
        Loader.ResetAllLoaders();

        WorldLoader.Instance.TerrainAsMesh = true;

        // Capture the authored data before anything is converted.
        //
        // Everything below builds Unity objects, and a Unity object cannot
        // answer "what was I made from" or "what should have been made and
        // wasn't". Reading the source into typed records first means the
        // scene becomes a view of the map rather than the only copy of it,
        // and gives the validation pass at the end of this method something
        // to compare against.
        Level worldLevel = ENV.GetWorldLevel();
        BFImportDiagnostics.Reset();

        // Presentation caches are keyed by the previous map's collider and
        // texture identities. Cleared here rather than on a load-completed
        // event because the terrain surface map is rebuilt during this very
        // import, and clearing afterwards would discard it.
        BFPresentation.ResetForMapChange();
        Source = BFSourceDatabase.BeginCapture(worldLevel == null ? "" : worldLevel.Name);

        var worldDefinitions = new List<BFWorldDefinition>(worldLayers.Length);
        foreach (World world in worldLayers)
        {
            worldDefinitions.Add(Source.CaptureWorld(world));
        }
        Source.CapturePlanning(worldLevel);

        // BF3 Legacy: import the original game's AI navigation data
        // (planning graph, barriers, hint nodes) before instances, so AI
        // spawned during load already has routes available.
        PhxNavGraph.Reset();
        PhxHintNodes.Reset();
        PhxAIDirector.ResetAll();
        PhxDestructionRegistry.Reset();
        PhxNavGraph.Instance.LoadPlanning(Source);

        foreach (BFWorldDefinition world in worldDefinitions)
        {
            PhxNavGraph.Instance.LoadBarriers(world);
            PhxHintNodes.Load(world);
        }

        for (int layerIdx = 0; layerIdx < worldLayers.Length; ++layerIdx)
        {
            World world = worldLayers[layerIdx];
            BFWorldDefinition worldDef = worldDefinitions[layerIdx];
            if (MapTexture == null)
            {
                MapTexture = TextureLoader.Instance.ImportUITexture(world.Name + "_map", false);
            }

            GameObject worldRoot = new GameObject(world.Name);
            Source.RegisterImported(worldRoot, worldDef?.Source);

            //Regions
            //Import before instances, since instances will reference regions
            var regionsRoot = WorldLoader.Instance.ImportRegions(world.GetRegions());
            regionsRoot.transform.parent = worldRoot.transform;
            // Walk the regions THIS world just created, not WorldLoader's
            // shared name->collider dictionary. That dictionary accumulates
            // across every world in the map and is only cleared on Reset(), so
            // iterating it re-walked earlier worlds' regions on each pass (the
            // "already registered" spam) and - the actual bug - ImportRegions
            // drops a later world's region whenever an earlier one already
            // claimed the name. A dropped region never got a PhxRegion, so its
            // command post had no capture trigger and could never be taken:
            // 'cp3capture' collides exactly this way on the BF3 Legacy maps.
            //
            // Names stay first-wins in Regions, which is what Lua's GetRegion
            // expects; the difference is that every region object now works as
            // a trigger whether or not it won the name.
            //
            // ImportRegions builds one child per source region, in order, so
            // the child index addresses the record it came from - which is how
            // each region object gets a link back to its source.
            int regionIdx = 0;
            foreach (Transform regionChild in regionsRoot.transform)
            {
                if (regionIdx < worldDef.Regions.Count)
                {
                    Source.RegisterImported(regionChild.gameObject, worldDef.Regions[regionIdx].Source);
                }
                ++regionIdx;

                if (regionChild.GetComponent<Collider>() == null) continue;

                string regName = regionChild.name.ToLower();
                PhxRegion reg = regionChild.gameObject.AddComponent<PhxRegion>();

                // invoke Lua events
                reg.OnEnter += (IPhxControlableInstance obj) => PhxLuaEvents.InvokeParameterized(PhxLuaEvents.Event.OnEnterRegion, regName, regName, obj.GetInstance().name);
                reg.OnLeave += (IPhxControlableInstance obj) => PhxLuaEvents.InvokeParameterized(PhxLuaEvents.Event.OnLeaveRegion, regName, regName, obj.GetInstance().name);

                if (!Regions.ContainsKey(regName))
                {
                    Regions.Add(regName, reg);
                }
            }

            //Instances
            GameObject instancesRoot = new GameObject("Instances");
            instancesRoot.transform.parent = worldRoot.transform;

            List<GameObject> instances = ImportInstances(world.GetInstances(), worldDef);
            foreach (GameObject instanceObject in instances)
            {
                instanceObject.transform.SetParent(instancesRoot.transform);
            }

            BatchStaticInstances(instances, instancesRoot);

            //Terrain
            var terrain = world.GetTerrain();
            if (terrain != null && !bTerrainImported)
            {
                GameObject terrainGameObject;
                terrainGameObject = WorldLoader.Instance.ImportTerrainAsMeshHDRP(terrain);

                terrainGameObject.transform.parent = worldRoot.transform;
                terrainGameObject.layer = LayerMask.NameToLayer("TerrainAll");
                // Only claim the terrain slot once one actually exists. Setting
                // this before a successful import would let a failed layer
                // suppress the terrain of every layer after it.
                bTerrainImported = true;
                Source.RegisterImported(terrainGameObject, worldDef.Terrain?.Source);

                Debug.Log($"[Terrain] Final state after parenting: world pos " +
                          $"{terrainGameObject.transform.position}, layer {terrainGameObject.layer} " +
                          $"({LayerMask.LayerToName(terrainGameObject.layer)}), active " +
                          $"{terrainGameObject.activeInHierarchy}, worldRoot pos {worldRoot.transform.position}");
            }


            //Lighting
            // Lighting and the skydome are cosmetic and independent of each
            // other and of the instances above: losing one must not cost the
            // rest of the layer, let alone the layers after it.
            try
            {
                Config lightingConfig = EnvCon.FindConfig(EConfigType.Lighting, world.Name);
                Source.CaptureLighting(world.Name, lightingConfig);

                var lightingRoots = WorldLoader.Instance.ImportLights(lightingConfig);
                foreach (var lightingRoot in lightingRoots)
                {
                    lightingRoot.transform.parent = worldRoot.transform;
                    LinkLights(lightingRoot, world.Name);
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"Lighting for world layer '{world.Name}' failed to import: {e}");
            }


            //Skydome, check if already loaded first
            if (!LoadedSkydomes.ContainsKey(world.SkydomeName))
            {
                try
                {
                    var skyRoot = WorldLoader.Instance.ImportSkydome(EnvCon.FindConfig(EConfigType.Skydome, world.SkydomeName));
                    if (skyRoot != null)
                    {
                        skyRoot.transform.parent = worldRoot.transform;
                    }

                    LoadedSkydomes[world.SkydomeName] = skyRoot;
                }
                catch (Exception e)
                {
                    Debug.LogError($"Skydome '{world.SkydomeName}' failed to import: {e}");
                }
            }

            WorldRoots.Add(worldRoot);
        }

        Animator.InitializeWorldAnimations(worldLayers);

        // Push every collider we just created and re-parented into PhysX.
        //
        // Physics.autoSyncTransforms is off in this project (see
        // ProjectSettings/DynamicsManager.asset), so transform writes are not
        // visible to raycasts and overlap tests until the next physics step.
        // Anything spawning in the same frame the level finished importing -
        // the first wave of AI - would otherwise query colliders still sitting
        // at their pre-placement poses: the spawn's ground ray finds nothing,
        // the soldier reads as not grounded, and it drops into the fall state
        // immediately. One call per map load, at the point everything is
        // finally in place.
        Physics.SyncTransforms();

        // Everything the map authored has now either become something or been
        // dropped. Say which, in a form that can be diffed run to run.
        BFImportValidator.Emit(Source, ENV.GetWorldName());
    }

    public SWBFPath GetPath(string pathName)
    {
        Level level = ENV.GetWorldLevel();
        if (level != null)
        {
            SWBFPath path = WorldLoader.Instance.ImportPath(level, pathName);
            return path;
        }
        return null;
    }

    public void Destroy()
    {
        Cra.Destroy();
        Cra = null;
        Projectiles.Destroy();
        Instances.Clear();
        Classes.Clear();
        UnityEngine.Object.Destroy(Vehicles);
        for (int i = 0; i < WorldRoots.Count; ++i)
        {
            UnityEngine.Object.Destroy(WorldRoots[i]);
        }
        WorldRoots.Clear();

        // The HUD unsubscribes itself in OnDestroy, but these are static events
        // that outlive any single map - clearing them here means a subscriber
        // that somehow survives teardown can't keep firing into the next map.
        PhxHUDEvents.Reset();

        // Reclaim the previous map's converted meshes/textures/materials.
        // Nothing else ever unloaded them, so GPU memory grew with every map
        // change until the process died.
        Resources.UnloadUnusedAssets();
    }

    // TODO: implement object pooling
    public PhxInstance CreateInstance(PhxClass cl, string instName, Vector3 position, Quaternion rotation, bool withCollision=true, Transform parent =null)
    {
        GameObject obj = CreateInstance(cl.EntityClass, instName, position, rotation, withCollision, parent);
        return obj == null ? null : obj.GetComponent<PhxInstance>();
    }

    public PhxInstance CreateInstance(PhxClass cl, bool withCollision=true, Transform parent=null)
    {
        GameObject obj = CreateInstance(cl.EntityClass, cl.Name + InstanceCounter++, Vector3.zero, Quaternion.identity, withCollision, parent);
        return obj == null ? null : obj.GetComponent<PhxInstance>();
    }

    public void DestroyInstance(PhxInstance instance)
    {
        int idx = Instances.IndexOf(instance);
        if (idx < 0) return;

        UnityEngine.Object.Destroy(instance.gameObject);

        // Tombstone instead of RemoveAt: instance indices are handed to Lua as
        // character handles and cached in InstanceMap, so compacting the list
        // silently retargets every index above idx. The name is released so a
        // future instance can reuse it.
        InstanceMap.Remove(instance.name);
        Instances[idx] = null;

        if (instance is IPhxTickable)
        {
            TickableInstances.Remove((IPhxTickable)instance);
        }
        if (instance is IPhxTickablePhysics)
        {
            TickablePhysicsInstances.Remove((IPhxTickablePhysics)instance);
        }
    }

    public PhxClass GetClass(string odfClassName)
    {
        if (Classes.TryGetValue(odfClassName, out PhxClass odf))
        {
            return odf;
        }
        EntityClass ec = ENV.Find<EntityClass>(odfClassName);
        if (ec != null)
        {
            return GetClass(ec);
        }
        return null;
    }

    /// <summary>
    /// Why <see cref="GetClass(string)"/> returned null, in words.
    /// </summary>
    /// <remarks>
    /// The two causes look identical to a caller and need completely different
    /// fixes: either the odf was never mounted (a missing or wrong data path),
    /// or it was mounted and its base class has no runtime implementation (a
    /// gap in <see cref="PhxClassRegister"/>). "Could not find PhxClass X" said
    /// neither.
    /// </remarks>
    public string DescribeMissingClass(string odfClassName)
    {
        EntityClass ec = ENV.Find<EntityClass>(odfClassName);
        if (ec == null)
        {
            return "its odf is not in any mounted level (check the addon/game data paths)";
        }

        EntityClass root = ClassLoader.GetRootClass(ec);
        string baseName = root == null ? "<unresolved>" : root.BaseClassName;
        return $"its odf loaded but base class '{baseName}' has no runtime implementation";
    }

    public PhxCommandpost[] GetCommandPosts()
    {
        return CommandPosts.ToArray();
    }

    public bool GetCPCameraTransform(PhxCommandpost cp, out PhxTransform camTransform)
    {
        return CPCamPositions.TryGetValue(cp, out camTransform);
    }

    public void Tick(float deltaTime)
    {
        Projectiles.Tick(deltaTime);
        Cra?.Tick();

        // Update instances AFTER animation update!
        // Instances might adapt some bone transformations (e.g. PhxSoldier)
        for (int i = 0; i < TickableInstances.Count; ++i)
        {
            TickableInstances[i].Tick(deltaTime);
        }
    }

    public void TickPhysics(float deltaTime)
    {
        Projectiles.TickPhysics(deltaTime);
        for (int i = 0; i < TickablePhysicsInstances.Count; ++i)
        {
            TickablePhysicsInstances[i].TickPhysics(deltaTime);
        }
    }

    PhxClass GetClass(EntityClass ec)
    {
        PhxClass odf = null;
        if (!Classes.TryGetValue(ec.Name, out odf))
        {
            EntityClass rootClass = ClassLoader.GetRootClass(ec);
            Type classType = PhxClassRegister.GetPhxClassType(rootClass.BaseClassName);
            if (classType != null)
            {
                odf = (PhxClass)Activator.CreateInstance(classType);
                odf.InitClass(ec);
                Classes.Add(ec.Name, odf);

                // An odf class counts as imported once it has a live PhxClass -
                // that is the point at which its authored properties are
                // actually available to the game.
                BFEntityClassDefinition definition = Source.GetClass(ec.Name);
                if (definition != null)
                {
                    Source.MarkImported(definition.Source);
                }
            }
        }
        return odf;
    }

    GameObject CreateInstance(ISWBFProperties instOrClass, string instName, Vector3 position, Quaternion rotation, bool withCollision=true, Transform parent =null)
    {
        if (InstanceMap.ContainsKey(instName))
        {
            Debug.LogWarningFormat("Instance with name: {0} already created!", instName);
            return null;
        }

        if (instOrClass == null)
        {
            Debug.LogWarning("Called 'CreateInstance' with NULL!");
            return null;
        }

        EntityClass ec = instOrClass.GetType() == typeof(Instance) ? ((Instance)instOrClass).EntityClass : ((EntityClass)instOrClass);
        if (ec == null)
        {
            // this can only happen if 'instOrClass' is an instance!
            Instance inst = (Instance)instOrClass;
            Debug.LogWarning($"Cannot find EnityClass '{inst.EntityClassName}' of given Instance '{inst.Name}'!");
            BFImportDiagnostics.Missing(BFSourceKind.EntityClass, inst.EntityClassName, inst.Name);
            return null;
        }

        EntityClass rootClass = ClassLoader.GetRootClass(ec);
        if (rootClass == null)
        {
            Debug.LogWarning($"Could not find root class of '{ec.Name}'!");
            return null;
        }

        GameObject instanceObject = ClassLoader.Instance.Instantiate(instOrClass, instName);
        instanceObject.transform.SetParent(parent);
        instanceObject.transform.localRotation = rotation;
        instanceObject.transform.localPosition = position;
        instanceObject.transform.localScale = new Vector3(1.0f, 1.0f, 1.0f);

        Type instType = PhxClassRegister.GetPhxInstanceType(rootClass.BaseClassName);

        if (instType != null)
        {
            PhxClass odf = GetClass(ec);
            PhxInstance script = (PhxInstance)instanceObject.AddComponent(instType);
            script.InitInstance(instOrClass, odf);

            Instances.Add(script);

            if (!string.IsNullOrEmpty(instanceObject.name))
            {
                if (!InstanceMap.ContainsKey(instanceObject.name))
                {
                    InstanceMap.Add(instanceObject.name, Instances.Count - 1);
                }
                else
                {
                    Debug.LogError($"Instance with name '{instanceObject.name}' is already registered in scene!");
                }
            }
            else
            {
                Debug.LogWarning($"Encountered instance of type '{odf.Name}' with no Name!");
            }

            if (script is IPhxTickable)
            {
                TickableInstances.Add((IPhxTickable)script);
            }
            if (script is IPhxTickablePhysics)
            {
                TickablePhysicsInstances.Add((IPhxTickablePhysics)script);
            }
            if (script is PhxCommandpost)
            {
                CommandPosts.Add((PhxCommandpost)script);
            }
            if(script is PhxVehicle)
            {
                instanceObject.transform.parent = Vehicles.transform;
            }
        }
        else if (!PhxClassRegister.IsRegistered(rootClass.BaseClassName))
        {
            // The odf parsed and the geometry may even have loaded, but with no
            // registry entry for its root base class nothing will ever tick,
            // shoot, be usable or take damage - the object exists as scenery.
            // Left silent this is the quietest possible failure mode, so it is
            // counted here and listed in the import report.
            //
            // Only genuinely unregistered classes count: several registered
            // ones (bolt, missile, explosion) deliberately have no instance
            // type because projectiles are pooled elsewhere, and reporting
            // those would bury the real gaps.
            BFImportDiagnostics.Missing(BFSourceKind.EntityClass,
                                        rootClass.BaseClassName, "no runtime class registered");
        }

        return instanceObject;
    }

    /// <summary>
    /// Build every instance of a world layer, tolerating individual failures.
    /// </summary>
    /// <remarks>
    /// A map is thousands of instances of twenty-year-old third-party data, so
    /// partial failure is normal: a missing model, an odf referencing a class
    /// that never got mounted, a command post whose hologram geometry isn't
    /// there. Without isolation the FIRST such instance throws out of this
    /// loop, out of Import, and out of CreateScene - taking every instance
    /// after it with it. That is how a single command post with no hologram
    /// produced a map with no command posts, no spawn UI and no HUD, which
    /// read as "spawning is completely broken" rather than "one post has no
    /// icon".
    ///
    /// One bad prop must cost exactly that prop.
    /// </remarks>
    List<GameObject> ImportInstances(Instance[] instances, BFWorldDefinition worldDef)
    {
        List<GameObject> instanceObjects = new List<GameObject>();
        for (int i = 0; i < instances.Length; ++i)
        {
            Instance inst = instances[i];

            // Same enumeration order as the capture pass, so the index
            // addresses this instance's source record.
            BFSourceRef sourceRef = worldDef != null && i < worldDef.Instances.Count
                ? worldDef.Instances[i].Source
                : null;

            GameObject instGO = null;
            try
            {
                instGO = CreateInstance(
                    inst,
                    inst.Name,
                    UnityUtils.Vec3FromLibWorld(inst.Position),
                    UnityUtils.QuatFromLibWorld(inst.Rotation)
                );
            }
            catch (Exception e)
            {
                // Name both the instance and its class: the instance says where
                // on the map to look, the class says which odf is at fault.
                Debug.LogError($"Instance '{inst.Name}' (class '{inst.EntityClassName}') failed to " +
                               $"import and was skipped: {e}");
                BFImportDiagnostics.HardFailure(
                    $"instance '{inst.Name}' ({inst.EntityClassName})", e.Message);
                continue;
            }

            if (instGO != null)
            {
                Source.RegisterImported(instGO, sourceRef);
                instanceObjects.Add(instGO);
            }
        }
        return instanceObjects;
    }

    /// <summary>
    /// Prepare the layer's immovable props for static batching.
    /// </summary>
    /// <remarks>
    /// Unity only static-batches objects that were marked static <i>before the
    /// scene was built</i>, which never applies to a level assembled at runtime
    /// from the user's own game files - so nothing here was ever batched, no
    /// matter that <c>ClassLoader</c> diligently sets <c>isStatic</c>.
    /// <c>StaticBatchingUtility.Combine</c> is the runtime equivalent and has
    /// to be called explicitly.
    ///
    /// It matters more here than in most projects because of how the source
    /// data is shaped: a SWBF2 model is split into one renderer per bone, and
    /// a map places hundreds of props, so a level arrives as many thousands of
    /// individually-drawn objects.
    ///
    /// Only instances whose root is static are included, and that restriction
    /// is not cosmetic: combining an object welds its vertices into a shared
    /// buffer in world space, so anything that later moves would smear across
    /// the map. Command posts, vehicles, doors and animated props are all
    /// excluded by the same <c>isStatic</c> flag the importer already sets.
    /// </remarks>
    void BatchStaticInstances(List<GameObject> instances, GameObject instancesRoot)
    {
        var batchable = new List<GameObject>(instances.Count);
        for (int i = 0; i < instances.Count; ++i)
        {
            GameObject instance = instances[i];
            if (instance == null || !instance.isStatic) continue;

            // A static root that nonetheless carries a tickable instance is
            // something that can change at runtime - a destructible building
            // swapping to its ruin, a prop with attached effects. Leave those
            // addressable.
            if (instance.GetComponent<PhxInstance>() is IPhxTickable) continue;

            batchable.Add(instance);
        }

        if (batchable.Count == 0) return;

        try
        {
            StaticBatchingUtility.Combine(batchable.ToArray(), instancesRoot);
            Debug.Log($"[Phoenix] Static-batched {batchable.Count} of {instances.Count} instances " +
                      $"in '{instancesRoot.transform.parent?.name}'.");
        }
        catch (Exception e)
        {
            // Batching is an optimisation; losing it must not lose the level.
            Debug.LogWarning($"Static batching failed for '{instancesRoot.name}': {e.Message}");
        }
    }

    /// <summary>
    /// Give every imported light object a link back to the record it came
    /// from. Matched by name because <see cref="WorldLoader.ImportLights"/>
    /// skips lights whose type it cannot handle, so positions in the two lists
    /// do not correspond.
    /// </summary>
    void LinkLights(GameObject lightsRoot, string worldName)
    {
        if (lightsRoot == null) return;

        BFWorldDefinition worldDef = null;
        for (int i = 0; i < Source.Worlds.Count; ++i)
        {
            if (string.Equals(Source.Worlds[i].Name, worldName, StringComparison.OrdinalIgnoreCase))
            {
                worldDef = Source.Worlds[i];
                break;
            }
        }
        if (worldDef == null) return;

        foreach (Transform child in lightsRoot.transform)
        {
            BFLightDefinition light = worldDef.FindLight(child.name);
            if (light != null)
            {
                Source.RegisterImported(child.gameObject, light.Source);
            }
        }
    }

    void CalcCPCamPositions()
    {
        for (int i = 0; i < CommandPosts.Count; ++i)
        {
            Transform t = CommandPosts[i].transform;
            Vector3 direction = (2.0f * -t.forward + t.up).normalized;
            Vector3 right = -t.right;

            float closestDistance = CameraMaxDistance;
            if (Physics.Raycast(t.position, direction, out RaycastHit info, CameraMaxDistance))
            {
                closestDistance = info.distance;
            }

            CPCamPositions.Add(CommandPosts[i], new PhxTransform 
            {
                Position = t.position + direction * closestDistance,
                Rotation = Quaternion.LookRotation(-direction, Vector3.Cross(right, -direction))
            });
        }
    }

#if UNITY_EDITOR
    void DrawIcon(GameObject gameObject, int idx)
    {
        var largeIcons = GetTextures("sv_label_", string.Empty, 0, 8);
        var icon = largeIcons[idx];
        var egu = typeof(EditorGUIUtility);
        var flags = BindingFlags.InvokeMethod | BindingFlags.Static | BindingFlags.NonPublic;
        var args = new object[] { gameObject, icon.image };
        var setIcon = egu.GetMethod("SetIconForObject", flags, null, new Type[] { typeof(UnityEngine.Object), typeof(Texture2D) }, null);
        setIcon.Invoke(null, args);
    }
    GUIContent[] GetTextures(string baseName, string postFix, int startIndex, int count)
    {
        GUIContent[] array = new GUIContent[count];
        for (int i = 0; i < count; i++)
        {
            array[i] = EditorGUIUtility.IconContent(baseName + (startIndex + i) + postFix);
        }
        return array;
    }
#endif

    // Scene statistics. These are plain runtime counters with no editor
    // dependency, but they used to sit inside the #if UNITY_EDITOR block above
    // alongside the gizmo-icon helpers. Runtime code calls them - PhxUIMapMarkers
    // walks the instance list through GetInstanceCount/GetInstance to place its
    // class markers - so in the Editor everything compiled while a standalone
    // player build would have failed on a missing method.
    public int GetInstanceCount()
    {
        return Instances.Count;
    }
    // The projectile counters stay editor-only: PhxProjectiles' own
    // GetActiveCount/GetTotalCount are themselves inside its #if UNITY_EDITOR
    // block, so exposing these at runtime would just move the build break.
#if UNITY_EDITOR
    public int GetActiveProjectileCount()
    {
        return Projectiles.GetActiveCount();
    }
    public int GetTotalProjectileCount()
    {
        return Projectiles.GetTotalCount();
    }
#endif

    public int GetTickableCount()
    {
        return TickableInstances.Count;
    }
    public int GetTickablePhysicsCount()
    {
        return TickablePhysicsInstances.Count;
    }
}
