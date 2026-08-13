using System;
using System.IO;
using System.Globalization;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

using UnityEngine;
using UnityEngine.Rendering;
using Unity.Collections;
#if !LVLIMPORT_NO_EDITOR
using UnityEditor;
#endif

using LibSWBF2.Wrappers;
using LibSWBF2.Enums;

using LibMaterial = LibSWBF2.Wrappers.Material;
using UMaterial = UnityEngine.Material;
using LibBone = LibSWBF2.Wrappers.Bone;


public class ModelLoader : Loader {

    public static ModelLoader Instance { get; private set; } = null;


    Dictionary<string, SWBFModel> ModelMappingDB = new Dictionary<string, SWBFModel>();
    Dictionary<string, GameObject> ModelDB = new Dictionary<string, GameObject>();

    // Models already reported for having zero-filled collision primitives, so
    // the note is made once rather than once per primitive.
    readonly HashSet<string> ReportedUnknownCollision = new HashSet<string>();


    GameObject ModelDBRoot = new GameObject("ModelDBRoot");


    public PhysicMaterial PhyMat;



    static ModelLoader()
    {
        Instance = new ModelLoader();
    }

    public void ResetDB()
    {
        ModelDB.Clear();

        GameObject.DestroyImmediate(ModelDBRoot);
        ModelDBRoot = new GameObject("ModelDBRoot");

        ModelMappingDB.Clear();
    }

    // Cylinder collision mesh as substitute for cylinder primitive.
    // Perhaps a gameobject with three children, each having a box collider, rotated to 
    // form a 6 sided cylinder would be more performant?  
    public readonly static Mesh CylinderCollision = Resources.Load<Mesh>("CylinderCollider");



    // Really only needed by EffectsLoader when an effect uses geometry.   
    public bool GetMeshesAndMaterialsFromSegments(string name, out List<Mesh> Meshes, out List<UMaterial> Mats)
    {
        Meshes = null;
        Mats = null;

        Model model = null;
        try {
            model = container.Get<Model>(name);
        }
        catch 
        { 
            return false; 
        }

        Mats = new List<UMaterial>();
        Meshes = new List<Mesh>();
        foreach (Segment seg in model.GetSegments())
        {
            Mats.Add(MaterialLoader.Instance.LoadMeshEffectMaterial(seg.Material));
            Meshes.Add(GetMeshFromSegments(new Segment[]{seg}, name, false));
        }  

        return true;     
    }


    

    /*
    Extracts static mesh data from array of segments
    */

    private Mesh GetMeshFromSegments(Segment[] segments, string meshName = "", bool flipXCoords = true)
    {
        Mesh mesh = new Mesh();
        mesh.name = meshName;

#if !LVLIMPORT_NO_EDITOR
        if (SaveAssets && !String.IsNullOrEmpty(meshName))
        {
            AssetDatabase.CreateAsset(mesh, Path.Combine(SaveDirectory, meshName + ".mesh")); 
        }
#endif
        mesh.subMeshCount = segments.Length;

        int totalLength = (int) segments.Sum(item => item.GetVertexBufferLength());

        Vector3[] positions = new Vector3[totalLength];
        Vector3[] normals = new Vector3[totalLength];
        Vector2[] texcoords = new Vector2[totalLength];
        int[] offsets = new int[segments.Length];

        int dataOffset = 0, i = 0;
        foreach (Segment seg in segments)
        {
            int vBufLength = (int) seg.GetVertexBufferLength();

            UnityUtils.ConvertSpaceAndFillVec3(seg.GetVertexBuffer<Vector3>(), positions, dataOffset, flipXCoords);
            UnityUtils.ConvertSpaceAndFillVec3(seg.GetNormalsBuffer<Vector3>(), normals, dataOffset, flipXCoords);
            Array.Copy(seg.GetUVBuffer<Vector2>(), 0, texcoords, dataOffset, vBufLength);

            offsets[i++] = dataOffset;
            dataOffset += vBufLength;
        }

        // A 16-bit index buffer tops out at 65535 vertices and silently
        // truncates past that, which shows up as a model missing most of
        // itself rather than as an error.
        if (totalLength > 65535)
        {
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        }

        mesh.SetVertices(positions);
        mesh.SetNormals(normals);
        mesh.SetUVs(0,texcoords);

        i = 0;
        foreach (Segment seg in segments)
        {
            mesh.SetTriangles(UnityUtils.ReverseWinding(seg.GetIndexBuffer()), i, true, offsets[i]);
            i++;
        }

        // Tangents, without which normal mapping does not work.
        //
        // The MSH format carries positions, normals and UVs but no tangents,
        // and nothing was generating them - so every normal-mapped surface in
        // the game was being lit against an undefined tangent basis. That
        // affects the bump maps the source itself ships (ApplyBumpMap) as well
        // as anything derived, and it is the kind of failure that reads as
        // "the lighting looks wrong somehow" rather than as anything
        // identifiable.
        //
        // Must come after both UVs and triangles: the tangent basis is derived
        // from UV derivatives across each face, so neither alone is enough.
        mesh.RecalculateTangents();

        return mesh;
    } 


    // Straightforward
    private bool AddSkeleton(GameObject newObject, Model model, out Dictionary<string, Transform> skeleton)
    {
        skeleton = new Dictionary<string, Transform>();

        ReadOnlyCollection<LibBone> hierarchy = model.Skeleton;
        if (hierarchy == null) return false;

        foreach (var node in hierarchy)
        {
            var nodeTransform = new GameObject(node.Name.ToLower()).transform;
            nodeTransform.localRotation = UnityUtils.QuatFromLibSkel(node.Rotation);
            nodeTransform.localPosition = UnityUtils.Vec3FromLibSkel(node.Location);
            skeleton[node.Name] = nodeTransform;
        }

        foreach (var node in hierarchy)
        {   
            if (node.ParentName.Equals(""))
            {
                skeleton[node.Name].SetParent(newObject.transform, false);
            }
            else 
            {
                skeleton[node.Name].SetParent(skeleton[node.ParentName], false);   
            }
        }

        return true;
    }



    /*
    Will keep vertex weights separate from static mesh handling until the 
    various edge cases (see git issue about sarlacc, ATTE, and Jabba) regarding weights and
    skeletons are sorted out.
    */

    private int AddWeights(GameObject obj, Model model, Mesh mesh)
    {
        var segments = (from segment in model.GetSegments() where segment.BoneName.Equals("") select segment).ToArray(); 

        int totalLength = (int) segments.Sum(item => item.GetVertexBufferLength());
        int txStatus = segments.Sum(item => item.IsPretransformed ? 1 : 0);

        if (txStatus != 0 && txStatus != segments.Length)
        {
            Debug.LogWarningFormat("Model {0} has heterogeneous pretransformation!  Please tell devs about this!!", model.Name);
            return 0;
        }

        byte bonesPerVert = (byte) (txStatus == 0 ? 3 : 1);  
        bool broken = model.IsSkeletonBroken;

        BoneWeight1[] weights = new BoneWeight1[totalLength * bonesPerVert];

        int dataOffset = 0;
        foreach (Segment seg in segments)
        {           
            UnityUtils.FillBoneWeights(seg.GetVertexWeights(), weights, dataOffset, broken ? -1 : 0);            
            dataOffset += (int) seg.GetVertexBufferLength() * bonesPerVert;
        }
        var weightsArray = new NativeArray<BoneWeight1>(weights, Allocator.Temp);

        byte[] bonesPerVertex = Enumerable.Repeat<byte>(bonesPerVert, totalLength).ToArray();
        var bonesPerVertexArray = new NativeArray<byte>(bonesPerVertex, Allocator.Temp);

        mesh.SetBoneWeights(bonesPerVertexArray, weightsArray);

        return (int) bonesPerVert;
    }


    /*
    Gathers segments by their node in the model's skeleton and creates a non-weighted mesh for each
    node with attached segments.
    */

    /// <summary>
    /// Give a renderable node its source identity.
    /// </summary>
    /// <remarks>
    /// Idempotent: a node can be visited by both the static and the skinned
    /// pass, and re-adding the component would leave two on one object with no
    /// way to say which is authoritative.
    /// </remarks>
    static void AttachSegmentIdentity(GameObject node, string modelName, string nodeName,
                                      string tag, bool isSkinned)
    {
        if (node == null) return;

        BFSegmentIdentity identity = node.GetComponent<BFSegmentIdentity>();
        if (identity == null)
        {
            identity = node.AddComponent<BFSegmentIdentity>();
        }

        identity.ModelName = modelName ?? "";
        identity.NodeName = nodeName ?? "";
        identity.Tag = (tag ?? "").ToLowerInvariant();
        identity.IsSkinned = isSkinned;
        identity.Role = BFSegmentRoles.Infer(identity.Tag, identity.NodeName);
    }

    /// <summary>Model/bone pairs already reported, so the warning fires once.</summary>
    static readonly HashSet<string> UnmappedBones = new HashSet<string>();

    /// <summary>
    /// A material for a model's cloth: the character's own.
    /// </summary>
    /// <remarks>
    /// A CLTH names a texture, but that texture is virtually always the one
    /// the character already wears - the skirt is part of the same sheet as
    /// the body. Reusing the built material keeps the cloth in the same
    /// variant as the unit it belongs to, which a separately loaded texture
    /// would not: the override that gives the 501st its markings lives on the
    /// material, not the texture name.
    /// </remarks>
    static UMaterial ClothMaterialFor(GameObject modelRoot)
    {
        Renderer source = modelRoot.GetComponentInChildren<SkinnedMeshRenderer>(true);
        if (source == null) source = modelRoot.GetComponentInChildren<Renderer>(true);

        return source != null ? source.sharedMaterial : null;
    }

    /// <param name="overrideTexture">
    /// The unit's own skin, if its odf named one.
    /// </param>
    /// <remarks>
    /// This used to pass null for the override while the skinned pass used it,
    /// which splits a character in two: the deforming body wears the variant's
    /// texture and every bone-attached piece - helmet, backpack, shoulder bell
    /// - keeps the base one. On a clone trooper that is the difference between
    /// a 501st soldier and a 501st soldier wearing a plain white helmet.
    /// </remarks>
    private List<SWBFSegment> AddStaticMeshes(GameObject newObject, Model model, Dictionary<string, Transform> skeleton, bool shadowSensitive, bool unlit, string overrideTexture = null)
    {
        List<Segment> segments = (from segment in model.GetSegments() where !segment.BoneName.Equals("") select segment).ToList();
        Dictionary<string, List<Segment>> segmentMap = new Dictionary<string, List<Segment>>();
        foreach (var segment in segments)
        {
            string boneName = segment.BoneName;

            if (boneName.Equals("")) continue;

            if (!segmentMap.ContainsKey(boneName))
            {
                segmentMap[boneName] = new List<Segment>();
            }

            segmentMap[boneName].Add(segment);
        }

        List<SWBFSegment> SWBFSegments = new List<SWBFSegment>(); 

        foreach (string boneName in segmentMap.Keys)
        {
            // A segment naming a bone this model's own skeleton does not have
            // is attached to the model root instead of throwing.
            //
            // It happens for real: a LOD mesh's segments carry the bone names
            // of the model they are a LOD *of*, which are not in the LOD's own
            // hierarchy. Indexing the dictionary directly turned that into a
            // KeyNotFoundException that propagated all the way out of
            // GetGameObjectFromModel, so one unresolved bone name did not cost
            // a segment - it cost the entire instance, mesh and collision
            // both, and the player walked through the hole where a building
            // used to be.
            if (!skeleton.TryGetValue(boneName, out Transform bone))
            {
                if (UnmappedBones.Add($"{model.Name}/{boneName}"))
                {
                    Debug.LogWarning($"Model '{model.Name}': segment bone '{boneName}' is not " +
                                     "in this model's skeleton; attaching to the model root.");
                }
                bone = newObject.transform;
            }

            GameObject boneObj = bone.gameObject;
            List<Segment> mappedSegments = segmentMap[boneName];

            for (int i = 0; i < mappedSegments.Count; i++)
            {
                SWBFSegments.Add(new SWBFSegment(i, boneObj, mappedSegments[i].Tag));
            }

            // Record what this part of the model IS, while the tag and the
            // bone are both in hand. The importer has always reconstructed the
            // segment structure and then thrown the semantics away, leaving
            // the finished object a hierarchy of renderers that nothing can
            // ask "which of you is the turret".
            //
            // One identity per node rather than per submesh: the submeshes on
            // a node share its bone and its tag is the same, so they are the
            // same part of the machine as far as anything downstream cares.
            AttachSegmentIdentity(boneObj, model.Name, boneName,
                                  mappedSegments.Count > 0 ? mappedSegments[0].Tag : "", false);

            MeshFilter filter = boneObj.AddComponent<MeshFilter>();
            filter.sharedMesh = GetMeshFromSegments(mappedSegments.ToArray(), model.Name + "_" + boneName);

            MeshRenderer renderer = boneObj.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = (from segment in mappedSegments select MaterialLoader.Instance.LoadMaterial(segment.Material, overrideTexture, unlit)).ToArray();
            renderer.shadowCastingMode = shadowSensitive ? ShadowCastingMode.On : ShadowCastingMode.Off;
            renderer.receiveShadows = shadowSensitive;
        }

        return SWBFSegments;
    }


    /*
    Finds segments which are not attached to skeleton nodes, ie ones that are skinned,
    and creates a weighted mesh from them. 
    */

    private List<SWBFSegment> AddSkinningComponents(GameObject newObject, Model model, Dictionary<string, Transform> skeleton, string overrideTexture)
    {
        Segment[] skinnedSegments = (from segment in model.GetSegments() where segment.BoneName.Equals("") select segment).ToArray();
        
        List<SWBFSegment> SkinnedSWBFSegments = new List<SWBFSegment>();
        for (int i = 0; i < skinnedSegments.Length; i++)
        {
            SkinnedSWBFSegments.Add(new SWBFSegment(i, newObject, skinnedSegments[i].Tag, true));
        }

        // Skinned segments all share one SkinnedMeshRenderer on the root, so
        // there is one identity for the whole skin rather than one per part -
        // which is honest about what the geometry actually is. Per-limb
        // resolution for a character comes from the skeleton and its
        // colliders, not from the mesh split.
        if (skinnedSegments.Length > 0)
        {
            AttachSegmentIdentity(newObject, model.Name, "", skinnedSegments[0].Tag, true);
        }


        Mesh mesh = GetMeshFromSegments(skinnedSegments.ToArray(), model.Name + "_skin");
        UMaterial[] mats = (from segment in skinnedSegments select MaterialLoader.Instance.LoadMaterial(segment.Material, overrideTexture)).ToArray();

        int skinType = AddWeights(newObject, model, mesh);
        if (skinType == 0)
        {
            //Debug.LogWarning("Failed to add weights....");
        }

        //Below, we handle 
        SkinnedMeshRenderer skinRenderer = newObject.AddComponent<SkinnedMeshRenderer>();
        ReadOnlyCollection<LibBone> bonesSWBF = model.Skeleton;

        /*
        Set bones
        */
        Transform[] bones = new Transform[bonesSWBF.Count];
        for (int boneNum = 0; boneNum < bonesSWBF.Count; boneNum++)
        {
            var curBoneSWBF = bonesSWBF[boneNum];
            bones[boneNum] = skeleton[curBoneSWBF.Name];

            //Messy, will fix once skeleton edge cases are sorted out
            //bones[boneNum].SetParent(curBoneSWBF.parentName != null && curBoneSWBF.parentName != "" && !curBoneSWBF.parentName.Equals(curBoneSWBF.Name) ? skeleton[curBoneSWBF.parentName] : newObject.transform, false);
        }

        /*
        Set bindposes...
        */
        Matrix4x4[] bindPoses = new Matrix4x4[bonesSWBF.Count];
        for (int boneNum = 0; boneNum < bonesSWBF.Count; boneNum++)
        {
            if (skinType == 1)
            {
                //For pretransformed skins...
                bindPoses[boneNum] = Matrix4x4.identity;
            }
            else 
            {
                bindPoses[boneNum] = bones[boneNum].worldToLocalMatrix * bones[0].parent.localToWorldMatrix;
            }
            //But what works for sarlacctentacle?
        }

        mesh.bindposes = bindPoses;

        skinRenderer.bones = bones;
        skinRenderer.sharedMesh = mesh;
        skinRenderer.sharedMaterials = mats;

        // Bindposes above are best-effort (see the sarlacctentacle note); when
        // they are off, the renderer's computed bounds can land far from the
        // object and the mesh gets frustum-culled - a soldier that is invisible
        // yet still collidable. Recomputing bounds from the live skin each
        // frame costs a little but can never cull a visible unit.
        skinRenderer.updateWhenOffscreen = true;

        for (int boneNum = 0; boneNum < bonesSWBF.Count; boneNum++)
        {
            var curBoneSWBF = bonesSWBF[boneNum];
            skeleton[curBoneSWBF.Name].localRotation = UnityUtils.QuatFromLibSkel(curBoneSWBF.Rotation);
            skeleton[curBoneSWBF.Name].localPosition = UnityUtils.Vec3FromLibSkel(curBoneSWBF.Location);
        }

        return SkinnedSWBFSegments;
    }



    public GameObject GetGameObjectFromModel(string modelName, string overrideTexture, bool shadowSensitive=true, bool unlit=false)
    {   
        Model model = container.Get<LibSWBF2.Wrappers.Model>(modelName);

        if (model == null)
        {
            BFImportDiagnostics.Missing(BFSourceKind.Model, modelName);
            return null;
        }

        // The cache key has to include the texture override.
        //
        // SWBF2 builds its unit variants from one mesh and a different texture
        // per unit - the 501st, a commander and a plain trooper are all
        // rep_inf_ep3trooper with an override naming their skin. Keying the
        // prototype on the model name alone meant whichever variant loaded
        // first baked its texture into the cache, and every other variant
        // sharing that geometry was handed a copy wearing the wrong one.
        string cacheKey = string.IsNullOrEmpty(overrideTexture)
            ? model.Name
            : model.Name + "|" + overrideTexture;

        if (!ModelDB.ContainsKey(cacheKey))
        {
            GameObject ModelObj = new GameObject(model.Name);
            SWBFModel ModelMapping = new SWBFModel(ModelObj);

            if (!AddSkeleton(ModelObj, model, out Dictionary<string, Transform> skeleton))
            {
                BFImportDiagnostics.Missing(BFSourceKind.Model, modelName, "skeleton conversion failed");
                return null;
            }

            List<SWBFSegment> Segments = AddStaticMeshes(ModelObj, model, skeleton, shadowSensitive, unlit, overrideTexture);
            ModelMapping.AddSegments(Segments);

            if (model.IsSkinned)
            {
                List<SWBFSegment> SkinnedSegments = AddSkinningComponents(ModelObj, model, skeleton, overrideTexture);
                ModelMapping.AddSegments(SkinnedSegments);

                // Authored cloth - skirts, cloaks, robes - hangs off the
                // skeleton, so it goes on once the bones exist and only for
                // models that have them. Wrapped for the same reason the LOD
                // pass is: this is an addition, and an addition must not be
                // able to cost the character it is decorating.
                try
                {
                    BFClothImporter.Attach(ModelObj, model.Name, ClothMaterialFor(ModelObj));
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"Model '{model.Name}': cloth could not be built " +
                                     $"({e.GetType().Name}: {e.Message}); the model is unaffected.");
                }
            }

            List<SWBFCollider> Colliders = AddCollision(ModelObj, model);
            ModelMapping.AddColliders(Colliders);

            AttachLevelOfDetail(ModelObj, model, skeleton, shadowSensitive, unlit);

            ModelDB[cacheKey] = ModelObj;
            ModelObj.transform.SetParent(ModelDBRoot.transform);
            ModelObj.SetActive(false);

            // Lower-cased: GetModelMapping looks names up with ToLower(), so a
            // mixed-case model name would silently return no mapping.
            ModelMappingDB[model.Name.ToLower()] = ModelMapping;
        }

        GameObject Copy = GameObject.Instantiate(ModelDB[cacheKey]);
        Copy.SetActive(true);

        BFImportDiagnostics.Resolved(BFSourceKind.Model, modelName);
        return Copy;
    }


    /// <summary>
    /// Give a model its authored low-detail mesh, if the map shipped one.
    /// </summary>
    /// <remarks>
    /// SWBF2 stores level-of-detail as ordinary models with the level appended
    /// to the base name - <c>cor1_prop_buldings_facade_1</c> is accompanied by
    /// <c>cor1_prop_buldings_facade_1LOWD</c> - and a <c>gmod</c> chunk naming
    /// the chain. The chunk is not worth reading: it is skipped by the native
    /// library and exposes nothing to C#, while the models it points at are
    /// already in the container under names that can be derived. So this
    /// derives them.
    ///
    /// Until now nothing ever asked for them by name, so the artists' own
    /// distance geometry sat in the container unused and every prop drew at
    /// full detail from any range. Note the scale before expecting much: cor1
    /// ships seven of these against seventy-nine models, so this is honouring
    /// authored intent rather than a large saving.
    ///
    /// The last level deliberately never culls. These are mostly skyline
    /// buildings and facades, and the authored intent is that they are visible
    /// from across the map - dropping them at a distance threshold would open
    /// holes in the skybox, which is far worse than drawing a cheap mesh.
    /// </remarks>
    /// <remarks>
    /// Wrapped so that a failure here can only cost the low mesh.
    ///
    /// It could not, before. This is an optional enhancement sitting inside
    /// GetGameObjectFromModel, and when it threw - a LOD mesh naming bones
    /// from its parent model - the exception unwound through the model loader
    /// and out through instance creation, so the base model was lost too.
    /// Seven models on Coruscant took every copy of themselves with them:
    /// facades, sky floors and a hall, all missing, all walk-through. An
    /// extra detail level is never worth a building.
    /// </remarks>
    void AttachLevelOfDetail(GameObject ModelObj, Model model,
                             Dictionary<string, Transform> skeleton,
                             bool shadowSensitive, bool unlit)
    {
        try
        {
            TryAttachLevelOfDetail(ModelObj, model, shadowSensitive, unlit);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"Model '{model.Name}': level of detail could not be built " +
                             $"({e.GetType().Name}: {e.Message}); the model itself is unaffected.");
        }
    }

    void TryAttachLevelOfDetail(GameObject ModelObj, Model model,
                                bool shadowSensitive, bool unlit)
    {
        // Skinned meshes are animated characters; LOWD is authored for static
        // props, and swapping a skinned renderer for an unskinned one mid-
        // animation is not something the format is asking for.
        if (model.IsSkinned) return;

        Model lowDetail = container.Get<Model>(model.Name + LowDetailSuffix);
        if (lowDetail == null) return;

        // Gather the full-detail renderers before the low mesh is parented in,
        // or they end up sharing a level and both draw.
        Renderer[] high = ModelObj.GetComponentsInChildren<Renderer>(true);
        if (high.Length == 0) return;

        GameObject lowObj = new GameObject(lowDetail.Name);
        lowObj.transform.SetParent(ModelObj.transform, false);

        if (!AddSkeleton(lowObj, lowDetail, out Dictionary<string, Transform> lowSkeleton))
        {
            GameObject.Destroy(lowObj);
            return;
        }

        AddStaticMeshes(lowObj, lowDetail, lowSkeleton, shadowSensitive, unlit);

        Renderer[] low = lowObj.GetComponentsInChildren<Renderer>(true);
        if (low.Length == 0)
        {
            GameObject.Destroy(lowObj);
            return;
        }

        LODGroup group = ModelObj.AddComponent<LODGroup>();
        group.SetLODs(new[]
        {
            new LOD(HighDetailScreenHeight, high),
            new LOD(0f, low),
        });
        group.RecalculateBounds();

        ++LODGroupsBuilt;
    }

    /// <summary>
    /// How many models got an authored low-detail mesh this session, reported
    /// by the render budget rather than logged per model.
    /// </summary>
    public static int LODGroupsBuilt { get; private set; }

    /// <summary>Suffix SWBF2 appends to a model name for its low-detail mesh.</summary>
    const string LowDetailSuffix = "LOWD";

    /// <summary>
    /// Fraction of screen height below which the low mesh takes over. Roughly
    /// a tenth of the frame - close enough that the swap is off-screen-centre
    /// when it happens, far enough that it is not visible on approach.
    /// </summary>
    const float HighDetailScreenHeight = 0.09f;

    public SWBFModel GetModelMapping(GameObject Root, string ModelName)
    {
        if (ModelName != null && Root != null && 
            ModelMappingDB.ContainsKey(ModelName.ToLower()))
        {
            return new SWBFModel(ModelMappingDB[ModelName.ToLower()], Root);
        }

        return null;
    }


    /*
    // Will be needed when/if the geometry is changed from Lua/or 
    // attached as part of some state change e.g. destructable buildings, pooled missiles 
    public SWBFModel AttachModelToGameObject(GameObject Root, string ModelName)
    {
        GameObject DupObj = GetGameObjectFromModel(ModelName);

        if (DupObj.)
        {

        }   

        SWBFModel ModelMapping = GetModelMapping(Root, ModelName);
    }
    */



    /*
    COLLISION ATTACHMENT FUNCTIONS BELOW
    */


    /*
    Adds collision primitives.  If a set of specific collider names are
    passed (see ClassLoader.LoadGeneralClass), they will be chosen 
    from all the model's colliders.  If not, the model's ordinance
    collision primitives will be used. 
    */ 

    public List<SWBFCollider> AddCollision(GameObject newObject, Model model)
    {
        // Contains all primitives and collision mesh
        List<SWBFCollider> SWBFColliders = new List<SWBFCollider>();

        //Get list of primitives, requested or found.
        CollisionPrimitive[] prims = model.GetPrimitivesMasked();
       
        // Instantiate and attach converted primitives
        foreach (CollisionPrimitive prim in prims) 
        {
            string parentBone = prim.ParentName;
            Transform boneTx = null;

            if (parentBone.Equals(""))
            {
                boneTx = newObject.transform;
            }
            else 
            {
                boneTx = UnityUtils.FindChildTransform(newObject.transform, parentBone);
            }

            if (boneTx == null) continue;

            GameObject primObj = new GameObject(prim.Name);
            primObj.transform.localPosition = UnityUtils.Vec3FromLibSkel(prim.Position);
            primObj.transform.localRotation = UnityUtils.QuatFromLibSkel(prim.Rotation);
            primObj.transform.SetParent(boneTx, false);

            switch (prim.PrimitiveType)
            {
                case ECollisionPrimitiveType.Cube:
                    BoxCollider boxColl = primObj.AddComponent<BoxCollider>();
                    if (prim.GetCubeDims(out float x, out float y, out float z))
                    {
                        boxColl.size = new Vector3(2.0f*x,2.0f*y,2.0f*z);
                    }
                    boxColl.sharedMaterial = PhyMat;
                    boxColl.name = prim.Name;

                    SWBFColliders.Add(new SWBFCollider(prim.MaskFlags, SWBFColliderType.Cube, primObj));

                    break;

                // Instantiate cylinder asset and use in convex mesh collider
                case ECollisionPrimitiveType.Cylinder:
                    if (prim.GetCylinderDims(out float r, out float h))
                    {
                        MeshCollider meshColl = primObj.AddComponent<MeshCollider>();
                        meshColl.sharedMesh = CylinderCollision;
                        meshColl.sharedMaterial = PhyMat;
                        meshColl.convex = true;
                        meshColl.name = prim.Name;
                        primObj.transform.localScale = new UnityEngine.Vector3(r,h,r);
                    
                        SWBFColliders.Add(new SWBFCollider(prim.MaskFlags, SWBFColliderType.Cylinder, primObj));
                    }
                    break;
                
                case ECollisionPrimitiveType.Sphere:
                    SphereCollider sphereColl = primObj.AddComponent<SphereCollider>();
                    if (prim.GetSphereRadius(out float rad))
                    {
                        sphereColl.radius = rad;
                    }
                    sphereColl.sharedMaterial = PhyMat;
                    sphereColl.name = prim.Name;

                    SWBFColliders.Add(new SWBFCollider(prim.MaskFlags, SWBFColliderType.Sphere, primObj));

                    break;
                
                // This happens, not sure what to make of it, but
                // all prims of this type have zeroed fields.
                //
                // Skipping them is correct - there is no geometry to build -
                // so this is not a defect, just something to note. It used to
                // warn per primitive, which put hundreds of identical lines in
                // the log and buried the messages that do matter; report each
                // model once and at Log level instead.
                default:
                    if (ReportedUnknownCollision.Add(model.Name))
                    {
                        Debug.Log($"{model.Name}: collision primitive of an unhandled (zero-filled) type, skipped");
                    }
                    break;
            }
        }

        // Get CollisionMesh if present, add to root and collider list
        CollisionMesh collMesh = null;
        try {
            collMesh = model.GetCollisionMesh();
        }
        catch 
        {
            Debug.LogWarning(model.Name + ": Error in process of CollisionMesh fetch...");
        }

        if (collMesh != null)
        {
            ushort[] indBuffer = collMesh.GetIndices();

            try {
                if (indBuffer.Length > 2)
                {
                    Mesh collMeshUnity = new Mesh();
                    
                    Vector3[] positions = collMesh.GetVertices<Vector3>();
                    UnityUtils.ConvertSpaceAndFillVec3(positions,positions,0);
                    collMeshUnity.vertices = positions;
                    collMeshUnity.SetTriangles(indBuffer, 0);

                    GameObject MeshColliderObj = new GameObject("collisionmesh");

                    MeshCollider meshCollider = MeshColliderObj.AddComponent<MeshCollider>();
                    meshCollider.name = "collisionmesh";
                    meshCollider.sharedMesh = collMeshUnity;
                    meshCollider.sharedMaterial = PhyMat;
                    MeshColliderObj.transform.SetParent(newObject.transform);

                    SWBFColliders.Add(new SWBFCollider(collMesh.MaskFlags, SWBFColliderType.Mesh, MeshColliderObj));
                }
            } 
            catch
            {
                Debug.LogWarning(model.Name + ": Error while creating mesh collider...");
            }
        }

        return SWBFColliders;
    }
}
