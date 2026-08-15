using System;
using System.IO;
using System.Collections.Generic;

using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
#if !LVLIMPORT_NO_EDITOR
using UnityEditor;
#endif

using LibSWBF2.Wrappers;
using LibSWBF2.Enums;
using LibSWBF2.Utils;
using LibSWBF2;

using LibTerrain = LibSWBF2.Wrappers.Terrain;
using UMaterial = UnityEngine.Material;
using LibVec3 = LibSWBF2.Types.Vector3;
using LibVec2 = LibSWBF2.Types.Vector2;
using ULight = UnityEngine.Light;
using SWBFRegion = LibSWBF2.Wrappers.Region;

public class WorldLoader : Loader
{
    public bool ImportTerrain = true;
    public bool TerrainAsMesh = false;
    public static bool UseHDRP;
    public static WorldLoader Instance { get; private set; } = null;


    Dictionary<string, GameObject> LoadedSkydomes;
    Dictionary<string, SWBFPath> LoadedPaths;
    public Dictionary<string, Collider> LoadedRegions { get; private set; }

    string[] BlendUniforms = new string[4]
    {
        "Texture2D_e354f4a9e36f4302a8feaefa8efd534f",   // Blend0
        "Texture2D_495af9007a884b93af8983df8b78ffb8",   // Blend1
        "Texture2D_174e3d4b45f741aaa690bfad9266cec2",   // Blend2
        "Texture2D_0661f9003d2e44729888f3e8310ba999",   // Blend3
    };


    static WorldLoader()
    {
        Instance = new WorldLoader();
    }

    private WorldLoader()
    {
        LoadedSkydomes = new Dictionary<string, GameObject>();
        LoadedRegions = new Dictionary<string, Collider>();
        LoadedPaths = new Dictionary<string, SWBFPath>();
    }

    public void Reset()
    {
        LoadedSkydomes.Clear();
        LoadedRegions.Clear();
        LoadedPaths.Clear();
    }


    public IEnumerator<LoadStatus> ImportWorldBatch(Level[] levels)
    {
        bool LoadedTerrain = false;
        
        foreach (Level level in levels)
        {
            foreach (World world in level.Get<World>())
            {
                float BatchProgress = 0.0f;

                GameObject worldRoot = new GameObject(world.Name);

                //Instances
                GameObject instancesRoot = new GameObject("Instances");
                instancesRoot.transform.parent = worldRoot.transform;

                BFPracticalLights.ResetReport();

                Instance[] instances = world.GetInstances();
                for (int i = 0; i < instances.Length; i++)
                {
                    BatchProgress = 0.6f * (((float)i) / instances.Length); 
                    yield return new LoadStatus(BatchProgress,world.Name + ": Instances");

                    GameObject instanceObject = ImportInstance(instances[i]);
                    if (instanceObject != null)
                    {
                       instanceObject.transform.parent = instancesRoot.transform; 
                    }
                }
               
                ReportUnbuiltInstances();
                BFPracticalLights.Report();

                BatchProgress = 0.6f;
                yield return new LoadStatus(BatchProgress,world.Name + ": Terrain");

                //Terrain
                var terrain = world.GetTerrain();
                if (terrain != null && !LoadedTerrain)
                {
                    GameObject terrainGameObject;
                    if (TerrainAsMesh)
                    {
                        terrainGameObject = ImportTerrainAsMesh(terrain, world.Name);
                    }
                    else 
                    {
                        terrainGameObject = ImportTerrainAsUnity(terrain, world.Name);
                    }

                    terrainGameObject.transform.parent = worldRoot.transform;
                }


                BatchProgress = 0.7f;
                yield return new LoadStatus(BatchProgress,world.Name + ": Lighting");        



                //Lighting
                var lightingRoots = ImportLights(container.FindConfig(EConfigType.Lighting, world.Name)); 
                foreach (var lightingRoot in lightingRoots)
                {
                    lightingRoot.transform.parent = worldRoot.transform;
                }

                BatchProgress = 0.8f;
                yield return new LoadStatus(BatchProgress,world.Name + ": Regions");        


                //Regions
                var regionsRoot = ImportRegions(world.GetRegions());
                regionsRoot.transform.parent = worldRoot.transform;

                BatchProgress = 0.9f;
                yield return new LoadStatus(BatchProgress,world.Name + ": Skydome");        


                //Skydome, check if already loaded first
                if (!LoadedSkydomes.ContainsKey(world.SkydomeName))
                {
                    var skyRoot = ImportSkydome(container.FindConfig(EConfigType.Skydome, world.SkydomeName));
                    if (skyRoot != null)
                    {
                        skyRoot.transform.parent = worldRoot.transform;
                    }

                    LoadedSkydomes[world.SkydomeName] = skyRoot;
                }
            }
        }       
    }




    /// <summary>
    /// Base classes the match creates, so placing them from the world file
    /// would double them up.
    /// </summary>
    /// <remarks>
    /// Deliberately short. A class earns a place here by being spawned
    /// elsewhere - soldiers come from command posts, ordnance from weapons -
    /// not by being unfamiliar. Anything not listed is map furniture, and map
    /// furniture that fails to appear is a bug the player walks through.
    /// </remarks>
    static readonly HashSet<string> SpawnedByGameplay = new HashSet<string>
    {
        "soldier", "weapon", "ordnance", "explosion", "missile", "beam",
        "bolt", "grenade", "mine", "powerup", "shell", "sticky",
    };

    /// <summary>Punctual shadow casters the .lgt marked Static, so cached.</summary>
    static int StaticShadowCasters;

    /// <summary>Punctual shadow casters that must re-render every frame.</summary>
    static int DynamicShadowCasters;

    /// <summary>Base classes seen but not built, and how many of each.</summary>
    static readonly Dictionary<string, int> UnbuiltByBase = new Dictionary<string, int>();
    static readonly Dictionary<string, string> UnbuiltExample = new Dictionary<string, string>();

    static void NoteUnbuilt(string baseName, string className)
    {
        string key = string.IsNullOrEmpty(baseName) ? "<no base class>" : baseName;

        UnbuiltByBase.TryGetValue(key, out int seen);
        UnbuiltByBase[key] = seen + 1;

        if (!UnbuiltExample.ContainsKey(key)) UnbuiltExample[key] = className;
    }

    /// <summary>
    /// One line per base class that produced no object, after the world is in.
    /// </summary>
    /// <remarks>
    /// Reported in aggregate rather than per instance: a map drops these in
    /// the hundreds when something is wrong, and a warning each would bury the
    /// console without making the pattern any clearer. The pattern is the
    /// point - "forty of base class X missing" names the class to fix.
    /// </remarks>
    public static void ReportUnbuiltInstances()
    {
        if (UnbuiltByBase.Count == 0) return;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("[BFImport] World instances that produced no object:");
        foreach (KeyValuePair<string, int> entry in UnbuiltByBase)
        {
            sb.Append("  ").Append(entry.Key.PadRight(24))
              .Append(entry.Value.ToString().PadLeft(5))
              .Append("   e.g. ").AppendLine(UnbuiltExample[entry.Key]);
        }
        sb.Append("  These have no mesh and no collision - the player walks through them.");

        Debug.LogWarning(sb.ToString());

        UnbuiltByBase.Clear();
        UnbuiltExample.Clear();
    }

    private GameObject ImportInstance(Instance inst)
    {
        string entityClassName = inst.EntityClassName;
        string baseName = ClassLoader.Instance.GetBaseClassName(entityClassName);

        GameObject instanceObject = null;

        // Anything the designer placed gets built, unless the gameplay layer
        // owns it.
        //
        // This used to be a whitelist of eight base classes with a bare
        // `default: break`, so every other instance in the world was dropped
        // in silence - no mesh, no collision, no warning. That is the "missing
        // buildings you can walk through" everyone hits: a turret, an ammo
        // droid, a grass patch or a bridge whose odf happened to derive from
        // something not on the list simply was not there.
        //
        // The list was never a requirement either way. LoadGeneralClass does
        // not branch on the base class at all - it reads GeometryName off the
        // entity class and builds the model and its collision - so the right
        // default is to build, and the exceptions are the handful of classes
        // that exist in the world file but are spawned by the match rather
        // than placed by the map.
        if (!SpawnedByGameplay.Contains(baseName))
        {
            instanceObject = ClassLoader.Instance.LoadGeneralClass(entityClassName, true);
        }

        if (instanceObject == null)
        {
            if (!SpawnedByGameplay.Contains(baseName)) NoteUnbuilt(baseName, entityClassName);
            return null;
        }

        if (!inst.Name.Equals(""))
        {
            instanceObject.name = inst.Name;
        }

        instanceObject.transform.rotation = UnityUtils.QuatFromLibWorld(inst.Rotation);
        instanceObject.transform.position = UnityUtils.Vec3FromLibWorld(inst.Position);
        instanceObject.transform.localScale = new Vector3(1.0f,1.0f,1.0f);

        // After the transform is final: the light is placed from the model's
        // world bounds, which are meaningless until the instance is where it
        // belongs.
        BFPracticalLights.TryAttach(instanceObject, entityClassName);

        return instanceObject;
    }


    // The source data is mirrored along Z. This used to be corrected with a
    // (1,1,-1) localScale on the terrain object, but a negative-determinant
    // scale flips the collision mesh's triangle winding: the walkable side
    // becomes backfacing, and with "Queries Hit Backfaces" off, raycasts and
    // physics sweeps leak straight through the floor. Bake the mirror into the
    // mesh data instead and keep the transform identity.
    static void BuildTerrainMesh(Mesh mesh, LibTerrain terrain)
    {
        Vector3[] positions = terrain.GetPositionsBuffer<Vector3>();
        int[] indices = Array.ConvertAll(terrain.GetIndexBuffer(), s => ((int)s));

        // An empty buffer here is not a mesh with "no geometry to see" - it's
        // a MeshCollider with sharedMesh set to a valid, zero-triangle Mesh,
        // which Unity accepts silently. Every entity that should be standing
        // on this terrain then has nothing solid beneath it and falls through
        // with no error anywhere in the chain. Fail loud instead.
        if (positions.Length == 0 || indices.Length == 0)
        {
            Debug.LogError($"Terrain import produced {positions.Length} vertices / " +
                           $"{indices.Length} indices - this map's terrain will have NO " +
                           "collision and nothing will be able to stand on it. Native " +
                           "buffer retrieval failed or returned empty.");
        }

        for (int i = 0; i < positions.Length; ++i)
        {
            positions[i].z = -positions[i].z;
        }
        // Mirroring inverts winding; swap two indices per triangle to restore it.
        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            int tmp = indices[i + 1];
            indices[i + 1] = indices[i + 2];
            indices[i + 2] = tmp;
        }
        mesh.vertices = positions;
        mesh.triangles = indices;

        // The authored normals follow the patch layout the artists built;
        // RecalculateNormals smooths across the patch seams instead and loses
        // the terrain's original shading.
        Vector3[] normals = terrain.GetNormalsBuffer<Vector3>();
        if (normals != null && normals.Length == positions.Length)
        {
            for (int i = 0; i < normals.Length; ++i)
            {
                normals[i].z = -normals[i].z;
            }
            mesh.normals = normals;
        }
        else
        {
            mesh.RecalculateNormals();
        }

        // Baked per-vertex terrain lighting/AO from the original renderer.
        // Without it imported terrain reads uniformly flat compared to the
        // real game. The shader multiplies it into base color.
        byte[] rawColors = terrain.GetColorBuffer();
        if (rawColors != null && rawColors.Length == positions.Length * 4)
        {
            Color32[] colors = new Color32[positions.Length];
            for (int i = 0; i < colors.Length; ++i)
            {
                int b = i * 4;
                colors[i] = new Color32(rawColors[b], rawColors[b + 1], rawColors[b + 2], rawColors[b + 3]);
            }
            mesh.colors32 = colors;
        }
    }

    /// <summary>
    /// World-space edge length of the terrain, used both for the shader's
    /// blend-map UV and for projecting baked vertex lighting onto it.
    /// </summary>
    /// <remarks>
    /// This was <c>dim * dimScale</c> straight out of <c>GetHeightMap</c>. Both
    /// come back as uint32_t, and dim is genuinely an integer - but the native
    /// side fills dimScale with a C cast of tern INFO's GridUnitSize, which is
    /// a float:
    ///
    ///     dimScale = (uint32_t) info -> m_GridUnitSize;
    ///
    /// So a map authored at 2.5 metres per grid unit arrives as 2, and a map
    /// authored at anything under a metre arrives as ZERO. Neither case is
    /// visible from the C# side - the number looks like a plausible grid size
    /// either way.
    ///
    /// Truncation alone misregisters the blend map against the ground by the
    /// ratio of the error. Zero would be worse and fails twice over:
    /// <see cref="BuildBlendLightingScale"/> rejects a bound of zero and returns
    /// null, so the map silently loses its baked terrain lighting, and the
    /// shader's world UV - (worldPos.xz + bound/2) / bound in
    /// BlendTerrainLayers.hlsl - divides by zero, so every layer samples a
    /// single texel.
    ///
    /// This is defensive, not a fix for anything shipped. Probing all twelve
    /// stock terrains (Tools/SkelProbe --terrain) says every one of them is
    /// authored at 4 or 8 metres per grid unit, whole in every case, so the
    /// stated and measured extents agree everywhere and this guard never fires
    /// on stock data. It is kept because the cost is one comparison at import
    /// and the failure it prevents is silent, and because addon and mod terrain
    /// is not bound by the convention the shipped maps happen to follow.
    ///
    /// The mesh already knows the answer. <see cref="BuildTerrainMesh"/> places
    /// every vertex in world space centred on the origin, so its bounds are the
    /// terrain's true extent at full float precision, with no native rebuild
    /// needed to get at it.
    ///
    /// The integer product is still preferred when the two agree, so maps with
    /// a whole-number grid unit - which is most of them, and all the ones that
    /// look right today - keep the exact value they already had. The mesh only
    /// takes over when the two disagree by more than a couple of grid cells,
    /// which is the signature of a truncation rather than of the half-cell
    /// slack between "span of the vertices" and "span of the grid".
    /// </remarks>
    static float TerrainWorldBound(Mesh terrainMesh, uint dim, uint dimScale)
    {
        float stated = (float)(dim * dimScale);

        if (terrainMesh == null || terrainMesh.vertexCount == 0)
        {
            return stated;
        }

        Vector3 size = terrainMesh.bounds.size;
        float measured = Mathf.Max(size.x, size.z);
        if (measured <= 0.0001f)
        {
            return stated;
        }

        // Slack of two grid cells. Sized off the measured extent rather than
        // off dimScale, which is the quantity under suspicion - and which is
        // zero in exactly the case this needs to catch.
        float cell = dim > 0 ? measured / dim : measured;
        if (Mathf.Abs(measured - stated) <= cell * 2f)
        {
            return stated;
        }

        Debug.LogWarning(
            $"[Terrain] Grid unit size arrived truncated: dim={dim} dimScale={dimScale} " +
            $"gives a {stated}m bound, but the built mesh spans {measured}m. Using the " +
            "mesh. The native GetHeightMap casts a float GridUnitSize to an integer, so " +
            "a fractional grid unit rounds down and a sub-metre one becomes zero.");

        return measured;
    }

    /// <summary>
    /// Per-blend-map-texel darkening factor derived from the mesh's baked
    /// vertex colours, or null if the mesh has none (older native library,
    /// or a map with no colour block - the same guard BuildTerrainMesh uses).
    ///
    /// Indexed exactly like the blend Color[] arrays in
    /// ImportTerrainAsMeshHDRP (row-major, y*blendDim+x), so callers can use
    /// the same index they already compute for writing a blend pixel.
    /// </summary>
    static float[] BuildBlendLightingScale(Vector3[] positions, Color32[] colors, int blendDim, float bound)
    {
        if (colors == null || colors.Length != positions.Length || blendDim <= 0 || bound <= 0f)
        {
            return null;
        }

        // -1 marks "no vertex landed here yet" so the dilation pass below can
        // tell an unvisited texel apart from one whose baked colour is
        // genuinely black. The vertex grid is slightly denser than the blend
        // map (patches overlap by one row/column), so most texels get
        // several contributions; a few - mostly along patch seams - get
        // none.
        float[] sum = new float[blendDim * blendDim];
        float[] count = new float[blendDim * blendDim];

        for (int i = 0; i < positions.Length; ++i)
        {
            Vector3 p = positions[i];
            // Same world-space UV the shader samples blend textures with
            // (BlendTerrainLayers.hlsl: (worldPos.xz + bound/2) / bound), on
            // the post-mirror positions BuildTerrainMesh already wrote -
            // no further axis correction needed.
            int px = Mathf.Clamp((int)(((p.x + bound * 0.5f) / bound) * blendDim), 0, blendDim - 1);
            int pz = Mathf.Clamp((int)(((p.z + bound * 0.5f) / bound) * blendDim), 0, blendDim - 1);
            int idx = pz * blendDim + px;

            Color32 c = colors[i];
            float luma = (0.299f * c.r + 0.587f * c.g + 0.114f * c.b) / 255f;

            sum[idx] += luma;
            count[idx] += 1f;
        }

        float[] scale = new float[blendDim * blendDim];
        for (int i = 0; i < scale.Length; ++i)
        {
            scale[i] = count[i] > 0f ? sum[i] / count[i] : -1f;
        }

        // One dilation pass over a copy: a texel the vertex grid never
        // touched inherits the average of its filled neighbours instead of
        // darkening that patch of terrain to black. Reading from the
        // pre-dilation copy keeps the result independent of scan order.
        float[] source = (float[])scale.Clone();
        for (int y = 0; y < blendDim; ++y)
        {
            for (int x = 0; x < blendDim; ++x)
            {
                int idx = y * blendDim + x;
                if (source[idx] >= 0f) continue;

                float neighborSum = 0f;
                int neighborCount = 0;
                for (int dy = -1; dy <= 1; ++dy)
                {
                    for (int dx = -1; dx <= 1; ++dx)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || nx >= blendDim || ny < 0 || ny >= blendDim) continue;
                        float v = source[ny * blendDim + nx];
                        if (v < 0f) continue;
                        neighborSum += v;
                        ++neighborCount;
                    }
                }
                // Fully isolated (should not happen in practice): no darkening.
                scale[idx] = neighborCount > 0 ? neighborSum / neighborCount : 1f;
            }
        }

        // Normalise into a darkening band, per map, instead of using the baked
        // luma as an absolute multiplier.
        //
        // The gamma step that used to live here is gone. Its comment said the
        // blend textures are "created without requesting a linear read
        // (Texture2D(w,h) defaults to sRGB)" - but both terrain paths now build
        // them with the linear flag set (see the RGBA32 constructors above), so
        // the compensation was correcting a condition that no longer holds and
        // simply darkened every map by an extra power of 2.2. On Kashyyyk that
        // took a mean luma of 0.38 down to 0.11.
        //
        // Using the value absolutely was the deeper fault, and it fails at both
        // ends. Measured mean baked luma across the shipped terrains:
        //
        //     nab2 0.007   yav1 0.99   hot1 0.99   geo1 0.96
        //     fel1 0.21    pol1 0.26   kas2 0.38   dag1 0.75
        //
        // Naboo's 0.007 multiplies its ground to black. Hoth, Yavin and
        // Geonosis sit so close to white that the term carries no variation at
        // all, which is the "terrain reads flat" report. Both are the same
        // mistake: treating an artist's absolute exposure choice from 2005 as a
        // physical occlusion factor.
        //
        // What the data genuinely contains is RELATIVE structure - which parts
        // of this map are shaded relative to the rest of it. So each map is
        // normalised against its own range: its darkest ground takes the full
        // darkening, its lightest takes none, everything else lands between.
        // Occlusion only ever darkens, so the band tops out at 1.
        NormaliseToBand(scale);

        return scale;
    }

    /// <summary>Strongest darkening the baked terrain term may apply.</summary>
    /// <remarks>
    /// Occlusion, not exposure - so this is a floor on brightness rather than a
    /// free multiplier. 0.55 is dark enough to read as shade under a canopy and
    /// short of the black the raw values produced on Naboo.
    /// </remarks>
    const float TerrainBakedFloor = 0.55f;

    /// <summary>
    /// Remap a per-texel darkening array onto [<see cref="TerrainBakedFloor"/>, 1]
    /// using the map's own 5th and 95th percentiles.
    /// </summary>
    /// <remarks>
    /// Percentiles rather than min/max: a single stray texel at either end
    /// would otherwise set the scale for the whole map, and the dilation pass
    /// above can leave outliers along patch seams.
    ///
    /// A map whose baked lighting is genuinely uniform has no structure to
    /// recover, and stretching near-zero range would amplify quantisation noise
    /// into visible banding. That case returns no darkening at all, which is
    /// honest: the data says nothing, so the term says nothing.
    /// </remarks>
    static void NormaliseToBand(float[] scale)
    {
        if (scale == null || scale.Length == 0) return;

        float[] sorted = (float[])scale.Clone();
        Array.Sort(sorted);

        float low = sorted[Mathf.Clamp(Mathf.RoundToInt((sorted.Length - 1) * 0.05f),
                                       0, sorted.Length - 1)];
        float high = sorted[Mathf.Clamp(Mathf.RoundToInt((sorted.Length - 1) * 0.95f),
                                        0, sorted.Length - 1)];

        float span = high - low;
        if (span < 0.01f)
        {
            for (int i = 0; i < scale.Length; ++i) scale[i] = 1f;
            return;
        }

        for (int i = 0; i < scale.Length; ++i)
        {
            float t = Mathf.Clamp01((scale[i] - low) / span);
            scale[i] = Mathf.Lerp(TerrainBakedFloor, 1f, t);
        }
    }

    public GameObject ImportTerrainAsMesh(LibTerrain terrain, string name)
    {
        Mesh terrainMesh = new Mesh();

#if !LVLIMPORT_NO_EDITOR
        if (SaveAssets)
        {
            AssetDatabase.CreateAsset(terrainMesh, Path.Combine(SaveDirectory, name + "_terrain.mesh"));
        }
#endif
        terrainMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        BuildTerrainMesh(terrainMesh, terrain);

        GameObject terrainObj = new GameObject("Terrain");
        terrainObj.isStatic = true;

        MeshFilter filter = terrainObj.AddComponent<MeshFilter>();
        filter.sharedMesh = terrainMesh;

        MeshCollider coll = terrainObj.AddComponent<MeshCollider>();
        coll.sharedMesh = terrainMesh;
        coll.sharedMaterial = ModelLoader.Instance.PhyMat;

        // Terrain belongs on TerrainAll, not Default.
        //
        // WorldLoader set no collision layer at all, so terrain stayed on
        // layer 0. Infantry survived that by accident - SoldierAll happens to
        // collide with Default - which is why "wrong layer" was investigated
        // once and ruled out. Vehicles do not survive it. Decoding the physics
        // matrix, VehicleTerrain (layer 20) collides with TerrainAll and
        // NOTHING else, and TerrainAll does not collide with Default either.
        // So every collider BF2 authors specifically for vehicle-to-ground
        // contact was colliding with nothing at all.
        int terrainLayer = LayerMask.NameToLayer("TerrainAll");
        if (terrainLayer >= 0) terrainObj.layer = terrainLayer;

        // TERR tile ranges belong to the standalone Mod Tools TERR format,
        // not the munged LVL tern/INFO wrapper used at runtime. Keep the
        // source metadata hook, but do not fabricate values from a different
        // format; the HDRP shader therefore retains its documented legacy
        // fallback repeat distance for now.
        float[] tileRanges = Array.Empty<float>();
        terrainObj.AddComponent<BFTerrainMetadata>().Initialize(
            terrainObj.name, terrain.LayerTextures.Count, tileRanges,
            terrainMesh.colors32 != null && terrainMesh.colors32.Length == terrainMesh.vertexCount);

        UMaterial terrainMat = new UMaterial(MaterialLoader.Instance.GetDefaultTerrainMaterial());

#if !LVLIMPORT_NO_EDITOR
        if (SaveAssets)
        {
            AssetDatabase.CreateAsset(terrainMat, Path.Combine(SaveDirectory, name + "_terrain.mat"));
        }
#endif
        MeshRenderer renderer = terrainObj.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = terrainMat;

        int i = 0;
        foreach (string texName in terrain.LayerTextures)
        {
            Texture2D tex = TextureLoader.Instance.ImportTexture(texName);
            string layerTexName = "_LayerXXTex".Replace("XX", i.ToString());

            if (tex != null)
            {
                renderer.sharedMaterial.SetTexture(layerTexName, tex);
            }
            i++;
        }

        terrain.GetBlendMap(out uint blendDim, out uint numLayers, out byte[] blendMapRaw);

        for (i = 0; i < 4; i++)
        {
            // Linear, and no mip chain. These channels are per-layer blend
            // WEIGHTS, not colour: reading them through the sRGB curve turns a
            // half weight into about a fifth, which misblends every layer
            // transition - and BuildBlendLightingScale bakes per-vertex
            // ambient occlusion into the same channels, so that was being
            // gamma-curved too. Mips on a weight map only muddy transitions.
            Texture2D blendTex = new Texture2D((int)blendDim, (int)blendDim,
                                               TextureFormat.RGBA32, false, true);

            Color[] colors = blendTex.GetPixels(0);

            for (int w = 0; w < blendDim; w++)
            {
                for (int h = 0; h < blendDim; h++)
                {
                    Color col = Color.black;
                    int baseIndex = (int)(numLayers * (w * blendDim + h));
                    int offset = i * 4;

                    for (int z = 0; z < 4; z++)
                    {
                        if (offset + z < numLayers)
                        {
                            col[z] = ((float)blendMapRaw[baseIndex + offset + z]) / 255.0f;
                        }
                    }

                    colors[(blendDim - w - 1) * blendDim + h] = col;
                }
            }

            blendTex.SetPixels(colors, 0);
            blendTex.Apply();

#if !LVLIMPORT_NO_EDITOR
            if (SaveAssets)
            {
                string blendSlicePath = SaveDirectory + "/blendmap_slice_" + i.ToString() + ".png";
                File.WriteAllBytes(blendSlicePath, blendTex.EncodeToPNG());
                AssetDatabase.ImportAsset(blendSlicePath, ImportAssetOptions.Default);
                blendTex = (Texture2D)AssetDatabase.LoadAssetAtPath(blendSlicePath, typeof(Texture2D));
            }
#endif
            renderer.sharedMaterial.SetTexture("_BlendMap" + i.ToString(), blendTex);
        }

        terrain.GetHeightMap(out uint dim, out uint dimScale, out float[] heightsRaw);
        float bound = TerrainWorldBound(terrainMesh, dim, dimScale);
        renderer.sharedMaterial.SetFloat("_XBound", bound);
        renderer.sharedMaterial.SetFloat("_ZBound", bound);

#if !LVLIMPORT_NO_EDITOR
        if (SaveAssets)
        {
            PrefabUtility.SaveAsPrefabAssetAndConnect(terrainObj, SaveDirectory + "/" + name + "_terrain.prefab", InteractionMode.UserAction);
        }
#endif
        return terrainObj;
    }


    public GameObject ImportTerrainAsMeshHDRP(LibTerrain terrain)
    {
        Mesh terrainMesh = new Mesh();

        terrainMesh.indexFormat = IndexFormat.UInt32;
        BuildTerrainMesh(terrainMesh, terrain);

        GameObject terrainObj = new GameObject("Terrain");
        terrainObj.isStatic = true;

        MeshFilter filter = terrainObj.AddComponent<MeshFilter>();
        filter.sharedMesh = terrainMesh;

        UMaterial terrainMat = new UMaterial(MaterialLoader.Instance.GetDefaultTerrainMaterial());

        MeshRenderer renderer = terrainObj.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = terrainMat;

        MeshCollider coll = terrainObj.AddComponent<MeshCollider>();
        coll.sharedMesh = terrainMesh;
        coll.sharedMaterial = ModelLoader.Instance.PhyMat;

        // Terrain belongs on TerrainAll, not Default.
        //
        // WorldLoader set no collision layer at all, so terrain stayed on
        // layer 0. Infantry survived that by accident - SoldierAll happens to
        // collide with Default - which is why "wrong layer" was investigated
        // once and ruled out. Vehicles do not survive it. Decoding the physics
        // matrix, VehicleTerrain (layer 20) collides with TerrainAll and
        // NOTHING else, and TerrainAll does not collide with Default either.
        // So every collider BF2 authors specifically for vehicle-to-ground
        // contact was colliding with nothing at all.
        int terrainLayer = LayerMask.NameToLayer("TerrainAll");
        if (terrainLayer >= 0) terrainObj.layer = terrainLayer;

        // Every prior hypothesis for "falls through terrain" (empty buffers,
        // triangle winding, convex-mesh vertex cap, wrong layer name) has been
        // individually ruled out without being able to see this map's actual
        // numbers. Log them unconditionally so the next test run answers the
        // question directly instead of adding another guess to the list.
        Debug.Log($"[Terrain] '{terrainObj.name}' built: {terrainMesh.vertexCount} verts, " +
                  $"bounds center {terrainMesh.bounds.center}, size {terrainMesh.bounds.size}, " +
                  $"transform pos {terrainObj.transform.position}, " +
                  $"collider.sharedMesh null={coll.sharedMesh == null}, " +
                  $"PhyMat null={ModelLoader.Instance.PhyMat == null}");

        string[] layerNames = new string[terrain.LayerTextures.Count];
        terrain.LayerTextures.CopyTo(layerNames, 0);
        Texture2DArray layers = TextureLoader.Instance.ImportTextures(layerNames, out float[] xAbsDims);
        terrainMat.SetTexture("Texture2DArray_7458b9063e46411289f9d5f3dc012ed7", layers);
        terrainMat.SetFloat("Vector1_665d2bac01fe4570bbe0622def4f5bce", layers.depth);

        // Needed both for the blend-lighting scale below and for the shader's
        // own Bound uniform further down - computed once here instead of
        // twice.
        terrain.GetHeightMap(out uint dim, out uint dimScale, out float[] heightsRaw);
        float bound = TerrainWorldBound(terrainMesh, dim, dimScale);

        terrain.GetBlendMap(out uint blendDim, out uint numLayers, out byte[] blendMapRaw);

        // The blend map also says what the terrain IS, per position - which
        // layer dominates a given patch of ground. Collapsing that to a
        // surface type here is the only point where the raw weights and the
        // layer names are both in hand; everything downstream (footsteps,
        // impacts, deformation, snow) reads the result rather than the terrain.
        BFTerrainSurfaceMap.Build(blendDim, numLayers, blendMapRaw,
                                  new List<string>(terrain.LayerTextures), bound);

        // Bake the original renderer's per-vertex terrain lighting/AO (see
        // BuildTerrainMesh) into the layer blend weights. The shader graph
        // custom-function output is `sum(layerColour_i * blendWeight_i)`
        // (BlendTerrainLayers.hlsl), so darkening every layer's blend weight
        // by the same per-texel factor darkens the final terrain colour by
        // that factor too - baked AO with no shader-graph edit. It is
        // necessarily luma-only: blend channels are layer weights, not
        // colour, so any tint in the baked vertex colour is lost. If the
        // shader graph is ever given its own VertexColor multiply (see
        // docs/BF2DataFeatures.md), this must be removed first or the
        // lighting gets applied twice.
        float[] blendLightingScale = BuildBlendLightingScale(
            terrainMesh.vertices, terrainMesh.colors32, (int)blendDim, bound);

        for (int i = 0; i < 4; i++)
        {
            // Linear, and no mip chain. These channels are per-layer blend
            // WEIGHTS, not colour: reading them through the sRGB curve turns a
            // half weight into about a fifth, which misblends every layer
            // transition - and BuildBlendLightingScale bakes per-vertex
            // ambient occlusion into the same channels, so that was being
            // gamma-curved too. Mips on a weight map only muddy transitions.
            Texture2D blendTex = new Texture2D((int)blendDim, (int)blendDim,
                                               TextureFormat.RGBA32, false, true);

            Color[] colors = blendTex.GetPixels(0);

            for (int w = 0; w < blendDim; w++)
            {
                for (int h = 0; h < blendDim; h++)
                {
                    Color col = Color.black;
                    int baseIndex = (int)(numLayers * (w * blendDim + h));
                    int offset = i * 4;

                    for (int z = 0; z < 4; z++)
                    {
                        if (offset + z < numLayers)
                        {
                            col[z] = ((float)blendMapRaw[baseIndex + offset + z]) / 255.0f;
                        }
                    }

                    int pixelIdx = (int)(blendDim - w - 1) * (int)blendDim + h;
                    if (blendLightingScale != null)
                    {
                        float scale = blendLightingScale[pixelIdx];
                        col.r *= scale;
                        col.g *= scale;
                        col.b *= scale;
                        col.a *= scale;
                    }

                    colors[pixelIdx] = col;
                }
            }

            blendTex.SetPixels(colors, 0);
            blendTex.Apply();

            renderer.sharedMaterial.SetTexture(BlendUniforms[i], blendTex);
        }

        renderer.sharedMaterial.SetFloat("Vector1_49103558bb1244ff8ac124e1bd984b90", bound);

        Vector4[] layerTexDims = new Vector4[4];
        float absDimMax = 0.0f;
        Debug.Assert(xAbsDims.Length <= 16);
        for (int i = 0; i < xAbsDims.Length; ++i)
        {
            absDimMax = Mathf.Max(absDimMax, xAbsDims[i]);
        }
        for (int i = 0; i < xAbsDims.Length; ++i)
        {
            layerTexDims[i / 4][i % 4] = xAbsDims[i] / absDimMax;
        }

        renderer.sharedMaterial.SetVector("Vector4_1e6425e6507a4b929dc007ed28cce2a1", layerTexDims[0]);
        renderer.sharedMaterial.SetVector("Vector4_bb80c51fa149447d9ecd026c6a02f191", layerTexDims[1]);
        renderer.sharedMaterial.SetVector("Vector4_0d4ad4ff048e46a8aa2f1787736e6b9f", layerTexDims[2]);
        renderer.sharedMaterial.SetVector("Vector4_bbab58e8dc3648e2951866e087bc80dd", layerTexDims[3]);

        return terrainObj;
    }


    private GameObject ImportTerrainAsUnity(LibTerrain terrain, string name)
    {
        //Read heightmap
        terrain.GetHeightMap(out uint dim, out uint dimScale, out float[] heightsRaw);
        float floor = terrain.HeightLowerBound;
        float ceiling = terrain.HeightUpperBound;

        TerrainData terData = new TerrainData();

#if !LVLIMPORT_NO_EDITOR
        if (SaveAssets)
        {
            AssetDatabase.CreateAsset(terData, SaveDirectory + "/" + name + "_terrain_data.asset");
        }
#endif
        // dimScale is the native side's integer cast of a float grid unit size
        // (see TerrainWorldBound), so a sub-metre grid unit arrives as zero,
        // which makes a TerrainData with no horizontal extent at all - one that
        // accepts SetHeights without complaint and renders nothing. There is no
        // built mesh to measure against on this path, so fall back to a metre
        // per grid unit rather than to nothing.
        uint safeScale = dimScale > 0 ? dimScale : 1;
        if (dimScale == 0)
        {
            Debug.LogWarning($"[Terrain] Grid unit size truncated to zero for '{name}'; " +
                             "assuming 1m per grid unit. Import as mesh for the exact extent.");
        }

        // Equal height bounds leave no vertical range to normalise into, and
        // every height then lands on a terrain of zero height - flat, and
        // flat is indistinguishable from a map that genuinely is.
        float heightRange = ceiling - floor;
        if (heightRange <= 0f) heightRange = 1f;

        terData.heightmapResolution = (int)dim + 1;
        terData.size = new Vector3(dim * safeScale, heightRange, dim * safeScale);
        terData.baseMapResolution = 512;
        terData.SetDetailResolution(512, 8);

        float[,] heights = new float[dim, dim];
        bool[,] holes = new bool[dim, dim];

        for (int x = 0; x < dim; x++)
        {
            for (int y = 0; y < dim; y++)
            {
                float h = heightsRaw[(dim - 1 - x) * dim + y];
                heights[x, y] = h < -0.1 ? 0 : h;
                holes[x, y] = h < -0.1 ? false : true;
            }
        }
        terData.SetHeights(0, 0, heights);
        terData.SetHoles(0, 0, holes);


        //Get list of textures used
        List<Texture2D> terTextures = new List<Texture2D>();
        foreach (string texName in terrain.LayerTextures)
        {
            Texture2D tex = TextureLoader.Instance.ImportTexture(texName);
            if (tex != null)
            {
                terTextures.Add(tex);
            }
        }

        terrain.GetBlendMap(out uint blendDim, out uint numLayers, out byte[] blendMapRaw);


        //Assign layers
        TerrainLayer[] terrainLayers = new TerrainLayer[numLayers];

        for (int i = 0; i < numLayers && i < terTextures.Count; i++)
        {
            TerrainLayer newLayer = new TerrainLayer();
            newLayer.diffuseTexture = terTextures[i];
            newLayer.tileSize = new Vector2(32, 32);
            terrainLayers[i] = newLayer;
        }

#if !LVLIMPORT_NO_EDITOR
        terData.SetTerrainLayersRegisterUndo(terrainLayers, "Undo");
#endif
        //Read blendmap
        float[,,] blendMap = new float[blendDim, blendDim, numLayers];

        for (int y = 0; y < blendDim; y++)
        {
            for (int x = 0; x < blendDim; x++)
            {
                int baseIndex = (int)(numLayers * (y * blendDim + x));
                for (int z = 0; z < numLayers; z++)
                {
                    blendMap[blendDim - y - 1, x, z] = ((float)blendMapRaw[baseIndex + z]) / 255.0f;
                }
            }
        }

        terData.alphamapResolution = (int)blendDim;
        terData.SetAlphamaps(0, 0, blendMap);
        terData.SetBaseMapDirty();


        //Save terrain/create gameobj
        GameObject terrainObj = UnityEngine.Terrain.CreateTerrainGameObject(terData);
        int dimOffset = -1 * ((int)(dimScale * dim)) / 2;
        terrainObj.transform.position = new Vector3(dimOffset, floor, dimOffset);

#if !LVLIMPORT_NO_EDITOR
        if (SaveAssets)
        {
            PrefabUtility.SaveAsPrefabAssetAndConnect(terrainObj, SaveDirectory + "/" + name + "_terrain.prefab", InteractionMode.UserAction);
        }
#endif
        return terrainObj;
    }



    /// <summary>
    /// Reference brightness for a light whose authored colour is plain white.
    /// </summary>
    /// <remarks>
    /// A .lgt states brightness as an unclamped colour, not in any physical
    /// unit, so there is nothing to convert from - only a reference to scale
    /// against. These are that reference: an authored colour of (1,1,1) lands
    /// here and everything else is proportional to it, which preserves the
    /// level designer's relative lighting exactly while putting the result in
    /// a range HDRP's auto-exposure handles sensibly.
    /// </remarks>
    const float ReferenceSunLux = 100000f;

    /// <remarks>
    /// Was 10000, which is a stadium floodlight - four to eight times a bright
    /// ceiling fixture. At that level a single interior practical outruns the
    /// sun and the ambient together, so whatever colour it happens to be
    /// becomes the only colour in the room. Sized to a bright practical
    /// instead, so a room lit by six of them reads as a lit room rather than
    /// as one enormous lamp.
    /// </remarks>
    const float ReferencePunctualLumen = 1200f;

    /// <summary>Range for a local light whose .lgt states none.</summary>
    const float DefaultLocalLightRange = 12f;

    /// <summary>
    /// Pull a light colour toward white until it is no more saturated than
    /// <see cref="MaxLightSaturation"/>, keeping its hue and its brightness.
    /// </summary>
    static Color LimitSaturation(Color color)
    {
        float max = Mathf.Max(color.r, Mathf.Max(color.g, color.b));
        float min = Mathf.Min(color.r, Mathf.Min(color.g, color.b));
        if (max <= 0.0001f) return Color.white;

        float saturation = (max - min) / max;
        if (saturation <= MaxLightSaturation) return color;

        // Lerp toward white by exactly the excess, so a light that is only
        // slightly over the ceiling is barely touched and a primary is pulled
        // right back to a tint.
        float t = 1f - (MaxLightSaturation / saturation);
        Color limited = Color.Lerp(color, Color.white, t);
        limited.a = 1f;
        return limited;
    }

    /// <summary>
    /// How far an authored light colour may be from neutral.
    /// </summary>
    /// <remarks>
    /// Splitting the authored colour into a hue and a magnitude divides by the
    /// largest channel, which pins that channel to exactly 1.0 - so every
    /// coloured light in the game arrives at maximum saturation. A soft green
    /// practical authored as (0.15, 0.60, 0.20) becomes (0.25, 1.00, 0.33),
    /// and a surface lit by it can only reflect green.
    ///
    /// The ratio between channels is preserved by that division, so in a 2005
    /// renderer - clamped, ambient-dominated, no tonemapping - it would look
    /// the same. Under HDRP it does not: the light is now in physical units,
    /// it dwarfs the ambient, and ACES pushes saturated hues further still.
    /// The result was interiors drowned in monochrome green.
    ///
    /// Real coloured light sources are tinted, not primaries. This is the
    /// ceiling on how far from white an imported light may sit; brightness is
    /// unaffected, because that lives in the magnitude.
    /// </remarks>
    const float MaxLightSaturation = 0.55f;

    /*
    Lighting.

    Position note: the Z flip below plus Vec3FromLibWorld's own flip cancel
    out, which makes this Vec3FromLib - i.e. lights, like Paths, do NOT carry
    the (X, Y, -Z) mirror that World-chunk data does. That is the same finding
    documented at length on ImportPath: a config chunk never goes through the
    World munge step. The original "still don't know why Z has to be reversed"
    was this, arrived at empirically. The +0.2 on Y is a separate nudge to lift
    lights off coincident surfaces and is left as-is.
    */
    public List<GameObject> ImportLights(Config lightingConfig, bool SetAmbient = false)
    {
        List<GameObject> lightObjects = new List<GameObject>();

        if (lightingConfig == null) return lightObjects;


        GameObject globalLightsRoot = new GameObject("GlobalLights");
        lightObjects.Add(globalLightsRoot);

        string light1Name = "", light2Name = "";
        Field globalLighting = lightingConfig.GetField("GlobalLights");
        if (globalLighting != null)
        {
            Scope gl = globalLighting.Scope;
            light1Name = gl.GetField("Light1").GetString();
            light2Name = gl.GetField("Light2").GetString();

            Color topColor = UnityUtils.ColorFromLib(gl.GetField("Top").GetVec3(), true);
            Color bottomColor = UnityUtils.ColorFromLib(gl.GetField("Bottom").GetVec3(), true);

            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientGroundColor = bottomColor;
            RenderSettings.ambientSkyColor = topColor;
        }


        Field[] lightFields = lightingConfig.GetFields("Light");

        GameObject localLightsRoot = new GameObject("LocalLights");
        lightObjects.Add(localLightsRoot);

        bool sunFound = false;
        HDAdditionalLightData sunLight = null;
        float brightestSun = 0f;

        // Whether the directional currently holding the shadow slot got it
        // because the map asked for it, rather than by being the brightest.
        // Once an authored caster exists, an unmarked light cannot displace it.
        bool sunCastsAuthored = false;

        StaticShadowCasters = 0;
        DynamicShadowCasters = 0;

        foreach (Field light in lightFields)
        {
            string lightName = light.GetString();
            Scope sl = light.Scope;

            bool IsGlobal = string.Equals(lightName, light1Name, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(lightName, light2Name, StringComparison.OrdinalIgnoreCase);


            GameObject lightObj = new GameObject(lightName);

            lightObj.transform.rotation = UnityUtils.QuatFromLibLGT(sl.GetVec4("Rotation"));

            LibVec3 lightPos = sl.GetVec3("Position");

            lightPos.Z *= -1.0f;
            lightPos.Y += .2f;
            lightObj.transform.position = UnityUtils.Vec3FromLibWorld(lightPos);

            float ltype = sl.GetFloat("Type");
            float range = sl.GetFloat("Range");


            if (UseHDRP)
            {
                HDAdditionalLightData lightComp = null;

                // The authored brightness lives in the Colour vector.
                //
                // A .lgt colour is not clamped to 0..1 - the original renderer
                // multiplied it straight into the surface, so a bright light
                // is authored as a colour with components above one and a dim
                // one below. Splitting that into a normalised hue and a
                // magnitude recovers the level designer's relative lighting,
                // which was being thrown away twice over: the colour was
                // clamped into a Unity Color, and the intensity was replaced
                // by the same constant for every light on every map.
                //
                // That is why lighting looked flat and uniform no matter what
                // the map said, and why no amount of tuning further downstream
                // could fix it - the differences between the map's own lights
                // were gone before anything else ran.
                LibVec3 authoredColor = sl.GetVec3("Color");
                float authoredMagnitude = Mathf.Max(authoredColor.X,
                                          Mathf.Max(authoredColor.Y, authoredColor.Z));

                Color hue = authoredMagnitude > 0.001f
                    ? new Color(authoredColor.X / authoredMagnitude,
                                authoredColor.Y / authoredMagnitude,
                                authoredColor.Z / authoredMagnitude)
                    : Color.white;

                hue = LimitSaturation(hue);

                // A colour of exactly white (1,1,1) is the common "no
                // particular brightness stated" case and must land on the
                // reference intensity, not on a dim one.
                float brightness = authoredMagnitude > 0.001f ? authoredMagnitude : 1f;

                if (ltype == 2.0f)
                {
                    lightComp = lightObj.AddHDLight(HDLightTypeAndShape.Point);
                    lightComp.intensity = ReferencePunctualLumen * brightness;
                    lightComp.range = range > 0.01f ? range : DefaultLocalLightRange;
                }
                else if (ltype == 3.0f)
                {
                    lightComp = lightObj.AddHDLight(HDLightTypeAndShape.ConeSpot);
                    lightComp.intensity = ReferencePunctualLumen * brightness;
                    lightComp.range = range > 0.01f ? range : DefaultLocalLightRange;

                    // The authored cone. Never applied under HDRP before, so
                    // every spot light in the game was whatever HDRP defaults
                    // to rather than the shape the designer placed.
                    LibVec2 cone = sl.GetVec2("Cone");
                    if (cone.X > 0.001f)
                    {
                        float outer = Mathf.Clamp(cone.X * Mathf.Rad2Deg, 1f, 179f);
                        lightComp.SetSpotAngle(outer);
                    }
                }
                else if (ltype == 1.0f)
                {
                    lightComp = lightObj.AddHDLight(HDLightTypeAndShape.Directional);
                    lightComp.intensity = ReferenceSunLux * brightness;

                    if (!String.IsNullOrEmpty(sl.GetString("Region")))
                    {
                        Debug.LogWarningFormat("Directional light {0} is linked to region {1}, deactivating it by default...", lightObj.name, sl.GetString("Region"));
                        lightObj.SetActive(false);
                    }
                }

                if (lightComp == null)
                {
                    Debug.LogWarning($"Light '{lightName}' has unhandled type {ltype}; skipped.");
                    UnityEngine.Object.Destroy(lightObj);
                    continue;
                }

                lightComp.EnableColorTemperature(false);
                lightComp.color = hue;

                // Local lights stop being evaluated at a distance. Without
                // this every light on the map is shaded from anywhere on it,
                // which on levels that author dozens of them is a large and
                // completely invisible cost.
                if (ltype != 1.0f)
                {
                    lightComp.fadeDistance = Mathf.Max(60f, lightComp.range * 6f);
                    lightComp.shadowFadeDistance = lightComp.fadeDistance;
                }

                // The .lgt says which lights cast shadows and which produce
                // speculars. Both are zero-or-one argument fields whose mere
                // presence is the statement, and neither was ever read - so
                // shadow casting was decided by heuristic (brightest
                // directional, nearest few punctuals) while the level designer
                // had already answered the question per light.
                //
                // That is why shadows did not agree with the map: a lamp
                // authored purely as fill light was casting, and a light
                // authored to define a doorway was not.
                bool authoredCastShadow = sl.GetField("CastShadow") != null;
                bool authoredCastSpecular = sl.GetField("CastSpecular") != null;

                // Static is stated on 349 of the game's 406 lights - by far the
                // most common field in any .lgt, and until now unread. It says
                // the light never moves, which is exactly the precondition for
                // rendering its shadow map once and caching it instead of
                // re-rendering it every frame. A cached punctual shadow is six
                // face renders paid at load rather than sixty times a second
                // for the whole match.
                bool authoredStatic = sl.GetField("Static") != null;

                lightComp.affectSpecular = authoredCastSpecular;

                bool isDirectional = ltype == 1.0f;
                if (isDirectional)
                {
                    // Still only one directional may cast - HDRP supports
                    // exactly one, and a second silently costs an atlas slot
                    // without producing anything. Among the lights the map
                    // marked as casters, the brightest wins; if the map marked
                    // none, the brightest overall still casts, because a map
                    // with no sun shadow at all reads as broken rather than as
                    // authored.
                    bool better = authoredCastShadow
                        ? !sunCastsAuthored || brightness > brightestSun
                        : !sunFound && !sunCastsAuthored;

                    if (better)
                    {
                        if (sunLight != null) sunLight.EnableShadows(false);

                        lightComp.EnableShadows(true);
                        lightComp.shadowUpdateMode = ShadowUpdateMode.EveryFrame;
                        sunLight = lightComp;
                        brightestSun = brightness;
                        sunFound = true;
                        sunCastsAuthored |= authoredCastShadow;
                    }
                }
                else
                {
                    // Punctual lights: the map states the intent, the budget
                    // decides how many of those intents are affordable this
                    // frame. A light the map never marked will not cast at any
                    // distance, which is the cheap half of this.
                    lightComp.EnableShadows(authoredCastShadow);
                    if (authoredCastShadow)
                    {
                        // Cached only where the map says the light is static.
                        //
                        // OnEnable renders the shadow map once, into the
                        // cached atlas, and never again - which is right for a
                        // fixture bolted to a wall and wrong for anything that
                        // moves, because the shadow would freeze in the pose
                        // it had when it was switched on. Applying it to every
                        // casting punctual, as this did, was correct only
                        // because most of them happen to be static; the .lgt
                        // says which, so there is no need to assume.
                        lightComp.shadowUpdateMode = authoredStatic
                            ? ShadowUpdateMode.OnEnable
                            : ShadowUpdateMode.EveryFrame;

                        if (authoredStatic) ++StaticShadowCasters;
                        else ++DynamicShadowCasters;
                    }

                    // Volumetric contribution is decided here rather than left
                    // to the budget to apply later. Volumetric fog scatters a
                    // light's colour through the whole camera volume, and
                    // unlike the light itself that scattering is not stopped
                    // by geometry - so one tinted practical fogs an entire
                    // level in its own colour. Only lights big enough to make
                    // a visible shaft earn it.
                    //
                    // Registration cannot be the place this happens: Register
                    // returns silently when the budget component does not
                    // exist yet, and the world imports before it is
                    // guaranteed to. A light that slipped through kept HDRP's
                    // default of contributing, which is the worst case.
                    lightComp.affectsVolumetric = lightComp.range >= 15f;

                    BFLightBudget.Register(lightComp, authoredCastShadow);
                }
            }
            else
            {
                ULight lightComp = lightObj.AddComponent<ULight>();
                lightComp.color = UnityUtils.ColorFromLib(sl.GetVec3("Color"));

                if (ltype == 2.0f)
                {
                    lightComp.type = UnityEngine.LightType.Point;
                    lightComp.range = range;
                    lightComp.intensity = 4.0f;
                }
                else if (ltype == 3.0f)
                {
                    lightComp.type = UnityEngine.LightType.Spot;
                    lightComp.range = range;
                    lightComp.spotAngle = sl.GetVec2("Cone").X * Mathf.Rad2Deg;
                    lightComp.intensity = IsGlobal ? 2.0f : 0.5f;
                }
                else if (ltype == 1.0f)
                {
                    lightComp.type = UnityEngine.LightType.Directional;
                    lightComp.intensity = IsGlobal ? 1.0f : 0.3f;

                    if (!String.IsNullOrEmpty(sl.GetString("Region")))
                    {
                        Debug.LogWarningFormat("Directional light {0} is linked to region {1}, deactivating it by default...", lightObj.name, sl.GetString("Region"));
                        lightObj.SetActive(false);
                    }
                    //lightComp.range = light.range;
                    //lightComp.spotAngle = light.spotAngles.X * Mathf.Rad2Deg;   
                }
                else
                {
                    Debug.LogWarning("Cant handle light type for " + light.GetName() + " yet");
                    continue;
                }

                lightComp.shadows = LightShadows.Soft;
#if !LVLIMPORT_NO_EDITOR
                lightComp.lightmapBakeType = LightmapBakeType.Realtime;
#endif
            }

            if (IsGlobal)
            {
                lightObj.transform.SetParent(globalLightsRoot.transform, false);
            }
            else
            {
                lightObj.transform.SetParent(localLightsRoot.transform, false);
            }

        }

        Debug.Log($"[BFImport] Lights: {StaticShadowCasters} cached shadow caster(s) " +
                  $"(.lgt Static), {DynamicShadowCasters} re-rendering every frame.");

        return lightObjects;
    }


    /// <summary>
    /// Remove every collider from an imported object.
    ///
    /// ModelLoader.GetGameObjectFromModel builds collision for whatever it
    /// loads, which is right for props and buildings and wrong for the sky:
    /// the dome is an enclosing shell scaled to (-300, 300, 300), so its lower
    /// half sits below the terrain and is solid. Soldiers could stand on the
    /// inside of the skybox, and any physics query - spawn placement, weapon
    /// fire, AI ground probes - could hit the sky instead of the world.
    /// A skydome is scenery; it should never be collidable.
    /// </summary>
    static void StripColliders(GameObject obj)
    {
        if (obj == null) return;

        Collider[] colliders = obj.GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < colliders.Length; ++i)
        {
            // Disable before destroying: Object.Destroy is deferred to the end
            // of the frame, and the rest of world import runs physics queries
            // (spawn settling among them) inside that same frame.
            colliders[i].enabled = false;
            UnityEngine.Object.Destroy(colliders[i]);
        }
    }

    public GameObject ImportSkydome(Config skydomeConfig)
    {
        if (skydomeConfig == null) return null;


        GameObject skyRoot = new GameObject("Skydome");

        // Record the map's authored atmosphere (fog, sun, ambient) for the
        // runtime to apply - the render-pipeline side of this is policy that
        // does not belong in the importer.
        SWBFSkyProperties.Reset();
        Field skyInfo = skydomeConfig.GetField("SkyInfo");
        if (skyInfo != null)
        {
            Scope sSky = skyInfo.Scope;
            SWBFSkyProperties.HasSkyInfo = true;
            if (sSky.GetVec3("FogColor", out LibVec3 fogCol))
            {
                // .sky colors are 0-255
                SWBFSkyProperties.FogColor = new Color(fogCol.X / 255f, fogCol.Y / 255f, fogCol.Z / 255f);
            }
            if (sSky.GetVec2("FogRange", out LibVec2 fogRange))
            {
                SWBFSkyProperties.FogRange = new Vector2(fogRange.X, fogRange.Y);
            }
            Field pc = sSky.GetField("PC");
            if (pc != null && pc.Scope.GetVec2("FarSceneRange", out LibVec2 farRange))
            {
                SWBFSkyProperties.FarSceneRange = new Vector2(farRange.X, farRange.Y);
            }
        }

        Field sunInfo = skydomeConfig.GetField("SunInfo");
        if (sunInfo != null)
        {
            Scope sSun = sunInfo.Scope;
            SWBFSkyProperties.HasSunInfo = true;
            if (sSun.GetVec2("Angle", out LibVec2 sunAngle))
            {
                SWBFSkyProperties.SunAngle = new Vector2(sunAngle.X, sunAngle.Y);
            }
            if (sSun.GetVec3("Color", out LibVec3 sunCol))
            {
                SWBFSkyProperties.SunColor = new Color(sunCol.X / 255f, sunCol.Y / 255f, sunCol.Z / 255f);
            }
            if (sSun.GetVec2("BackAngle", out LibVec2 backAngle))
            {
                SWBFSkyProperties.SunBackAngle = new Vector2(backAngle.X, backAngle.Y);
            }
            if (sSun.GetVec3("BackColor", out LibVec3 backCol))
            {
                SWBFSkyProperties.SunBackColor = new Color(backCol.X / 255f, backCol.Y / 255f, backCol.Z / 255f);
            }
        }

        //Import dome
        GameObject domeRoot = new GameObject("Dome");
        domeRoot.transform.parent = skyRoot.transform;

        Field domeInfo = skydomeConfig.GetField("DomeInfo");
        if (domeInfo != null)
        {
            Scope sDi = domeInfo.Scope;

            if (sDi.GetVec3("Ambient", out LibVec3 domeAmbient))
            {
                SWBFSkyProperties.HasDomeAmbient = true;
                SWBFSkyProperties.DomeAmbient = new Color(domeAmbient.X / 255f, domeAmbient.Y / 255f, domeAmbient.Z / 255f);
            }

            Field[] domeModelFields = sDi.GetFields("DomeModel");
            foreach (Field domeModelField in domeModelFields)
            {
                Scope sD = domeModelField.Scope;
                string geometryName = sD.GetString("Geometry");

                GameObject domeModelObj = ModelLoader.Instance.GetGameObjectFromModel(geometryName, null, false, true);
                if (domeModelObj == null)
                {
                    continue;
                }

                domeModelObj.name = geometryName;

#if !LVLIMPORT_NO_EDITOR
                if (SaveAssets)
                {
                    PrefabUtility.SaveAsPrefabAssetAndConnect(domeModelObj, SaveDirectory + "/dome_model_" + geometryName + ".prefab", InteractionMode.UserAction);
                }
#endif
                StripColliders(domeModelObj);

                domeModelObj.transform.localScale = new Vector3(-300, 300, 300);
                domeModelObj.transform.parent = domeRoot.transform;

                // The authored dome (very nearly) follows the camera; fixed in
                // the world it reads as a walk-up-to-able prop.
                SWBFSkydomeFollow follow = domeModelObj.AddComponent<SWBFSkydomeFollow>();
                follow.BasePosition = new Vector3(0f, sD.GetFloat("Offset"), 0f);
                if (sD.GetFloat("MovementScale", out float moveScale) && moveScale > 0f)
                {
                    follow.MovementScale = moveScale;
                }

                if (!UseHDRP)
                {
                    MaterialLoader.Instance.PatchMaterial(domeModelObj, "skydome");
                }
            }
        }


        //Import dome objects, one of each for now
        GameObject domeObjectsRoot = new GameObject("SkyObjects");
        domeObjectsRoot.transform.parent = skyRoot.transform;

        Field[] domeObjectFields = skydomeConfig.GetFields("SkyObject");
        foreach (Field domeObjectField in domeObjectFields)
        {
            string geometryName = domeObjectField.Scope.GetString("Geometry");

            GameObject domeObject = ModelLoader.Instance.GetGameObjectFromModel(geometryName, null, false);
            if (domeObject == null)
            {
                continue;
            }

            domeObject.name = geometryName;

#if !LVLIMPORT_NO_EDITOR
            if (SaveAssets)
            {
                PrefabUtility.SaveAsPrefabAssetAndConnect(domeObject, SaveDirectory + "/dome_object_" + geometryName + ".prefab", InteractionMode.UserAction);
            }
#endif
            StripColliders(domeObject);

            domeObject.transform.parent = domeObjectsRoot.transform;
            domeObject.transform.localPosition = new Vector3(0, domeObjectField.Scope.GetVec2("Height").X, 0);

            if (!UseHDRP)
            {                              
                MaterialLoader.Instance.PatchMaterial(domeObject, "skydome");
            }
        }

        return skyRoot;
    }



    public GameObject ImportRegion(SWBFRegion region)
    {
        string GetRealRegionName(SWBFRegion reg)
        {
            string name = reg.Name;
            reg.GetProperties(out uint[] props, out string[] values);
            for (int i = 0; i < props.Length; i++)
            {
                if (props[i] == HashUtils.GetFNV("Name"))
                {
                    name = values[i];
                }
            }
            return name;
        }

        GameObject regionObj = new GameObject(GetRealRegionName(region));
        regionObj.transform.position = UnityUtils.Vec3FromLibWorld(region.Position);
        regionObj.transform.rotation = UnityUtils.QuatFromLibWorld(region.Rotation);

        LibVec3 sz = region.Size;

        Collider collider = null;
        if (region.Type == "box")
        {
            BoxCollider coll = regionObj.AddComponent<BoxCollider>();
            coll.size = 2f * new Vector3(sz.X, sz.Y, sz.Z);
            collider = coll;
        }
        else if (region.Type == "sphere")
        {
            SphereCollider coll = regionObj.AddComponent<SphereCollider>();
            coll.radius = sz.X;
            collider = coll;
        }
        else if (region.Type == "cylinder")
        {
            MeshCollider coll = regionObj.AddComponent<MeshCollider>();
            coll.convex = true;
            coll.sharedMesh = ModelLoader.CylinderCollision;

            float r = Mathf.Sqrt(sz.X * sz.X + sz.Z * sz.Z);
            regionObj.transform.localScale = new Vector3(r, 2f * sz.Y, r);
            collider = coll;
        }
        else
        {
            throw new Exception(string.Format("Region implementation needed for '{0}'!", region.Type));
        }

        collider.isTrigger = true;

        return regionObj;
    }



    public GameObject ImportRegions(SWBFRegion[] regions)
    {
        GameObject regionsRoot = new GameObject("Regions");

        foreach (SWBFRegion region in regions)
        {
            GameObject regionObj = ImportRegion(region);
            regionObj.transform.parent = regionsRoot.transform;

            if (!LoadedRegions.ContainsKey(regionObj.name))
            {
                LoadedRegions.Add(regionObj.name, regionObj.GetComponent<Collider>());
            }
        }

        return regionsRoot;
    }

    public SWBFPath ImportPath(string pathName)
    {
        if (LoadedPaths.TryGetValue(pathName, out SWBFPath foundPath))
        {
            return foundPath;
        }

        SWBF2Handle[] handles = container.GetLoadedLevels();
        for (int i = 0; i < handles.Length; ++i)
        {
            Level level = container.GetLevel(handles[i]);
            SWBFPath path = ImportPath(level, pathName);
            if (path != null)
            {
                return path;
            }
        }

        return null;
    }

    public SWBFPath ImportPath(Level level, string pathName)
    {
        if (LoadedPaths.TryGetValue(pathName, out SWBFPath foundPath))
        {
            return foundPath;
        }

        uint PathHash = HashUtils.GetFNV("Path");

        Config[] configs = level.GetConfigs(EConfigType.Path);
        for (int i = 0; i < configs.Length; ++i)
        {
            Field[] paths = configs[i].GetFields(PathHash);
            for (int j = 0; j < paths.Length; ++j)
            {
                if (paths[j].GetString().ToLower() == pathName.ToLower())
                {
                    SWBFPath path = new SWBFPath(pathName);

                    Field nodesParent = paths[j].Scope.GetField("Nodes");
                    Field[] nodes = nodesParent.Scope.GetFields("Node");

                    path.Nodes = new SWBFPath.Node[nodes.Length];
                    for (int k = 0; k < nodes.Length; ++k)
                    {
                        Field pos = nodes[k].Scope.GetField("Position");
                        Field rot = nodes[k].Scope.GetField("Rotation");
                        Field knot = nodes[k].Scope.GetField("Knot");
                        Field time = nodes[k].Scope.GetField("Time");
                        Field pauseTime = nodes[k].Scope.GetField("PauseTime");
                        // Vec3FromLib, NOT Vec3FromLibWorld: a "Path" chunk
                        // never goes through the World-instance munge step
                        // that regions, lights and placed instances get, so it
                        // doesn't carry that step's (X, Y, -Z) mirror. Logging
                        // every command post's spawn path next to the post's
                        // own (independently-verified-correct) transform proved
                        // it: converted with the World flip, every node on
                        // e.g. cor1's CP5SpawnPath landed ~220m away, mirrored
                        // to the opposite side of the map from CP5 itself
                        // (post at Z=+111, nodes at Z=-105..-117); dropping
                        // the flip puts every one of them within a few metres
                        // of the post, exactly where an authored spawn point
                        // should be.
                        path.Nodes[k].Position = UnityUtils.Vec3FromLib(pos.GetVec3());

                        // The rotation needs the same un-mirroring, but it can
                        // NOT simply use QuatFromLib. The two converters are not
                        // a mirror pair - they use different component
                        // orderings, because lib's XFRM decomposition shuffles
                        // quaternion components (see the note on
                        // QuatFromLibWorld). In these Vector4s the real part is
                        // lib's Y, not lib's W, so QuatFromLib's straight
                        // (X,Y,Z,W) passthrough yields an essentially arbitrary
                        // orientation - which is exactly what it did: soldiers
                        // spawned upside down, their capsule (centred at local
                        // +0.9) hanging below the floor with nothing under the
                        // transform origin to stand on, so they read as not
                        // grounded and fell out of the world.
                        //
                        // So take QuatFromLibWorld's correct ordering and undo
                        // only its Z mirror. Mirroring z maps a rotation
                        // (x,y,z,w) to (-x,-y,z,w): the axis' z component is
                        // preserved while the handedness flip negates the angle.
                        // Deriving it this way keeps the two conversions
                        // provably consistent instead of hand-expanding
                        // components a second time.
                        UnityEngine.Quaternion world = UnityUtils.QuatFromLibWorld(rot.GetVec4());
                        path.Nodes[k].Rotation = new UnityEngine.Quaternion(-world.x, -world.y, world.z, world.w);
                        path.Nodes[k].Knot = knot.GetFloat();
                        path.Nodes[k].Time = time.GetFloat();
                        path.Nodes[k].PauseTime = pauseTime.GetFloat();
                    }

                    LoadedPaths.Add(pathName, path);

                    // Paths are resolved on demand rather than during the world
                    // walk, so they enter the source record here - at the only
                    // point the importer knows one exists.
                    BFPathNode[] sourceNodes = new BFPathNode[path.Nodes.Length];
                    for (int n = 0; n < path.Nodes.Length; ++n)
                    {
                        sourceNodes[n] = new BFPathNode(path.Nodes[n].Position, path.Nodes[n].Rotation,
                                                        path.Nodes[n].Knot, path.Nodes[n].Time,
                                                        path.Nodes[n].PauseTime);
                    }
                    BFPathDefinition definition = BFSourceDatabase.Active.CapturePath(pathName, sourceNodes);
                    BFSourceDatabase.Active.MarkImported(definition?.Source);

                    return path;
                }
            }
        }

        return null;
    }
}

public class SWBFPath
{
    public struct Node
    {
        public Vector3 Position;
        public Quaternion Rotation;
        public float Knot;
        public float Time;
        public float PauseTime;
    }

    public string Name { get; private set; }
    public Node[] Nodes;

    public SWBFPath(string name)
    {
        Name = name;
    }

    public Node GetRandom()
    {
        if (Nodes == null || Nodes.Length == 0)
        {
            // Missing '$' meant this printed the literal text "{Name}" instead
            // of the path's actual name - silent even when it did fire.
            Debug.LogError($"Path '{Name}' has no nodes!");
            return default;
        }

        int idx = UnityEngine.Random.Range(0, Nodes.Length);
        return Nodes[idx];
    }
}
