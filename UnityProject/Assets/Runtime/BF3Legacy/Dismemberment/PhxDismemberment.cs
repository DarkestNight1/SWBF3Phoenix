using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Lightsaber dismemberment. On a lethal saber hit, the nearest severable limb
/// bone is found, the limb geometry is extracted from the skinned mesh into a
/// detached physics chunk, and the bone is collapsed on the body - producing a
/// clean sever with a cauterized glow, no blood (matching the films and the
/// gore constraints of the originals).
///
/// Works against any humanoid skeleton by bone-name matching, including the
/// SWBF2 "human" skeleton loaded at runtime (bone_l_forearm, bone_r_calf, ...).
///
/// Usage: PhxDismemberment.TrySever(soldierGameObject, hitPosition)
/// from lightsaber melee kill handling. Honors PhxBF3.Config.GoreLevel.
/// </summary>
public static class PhxDismemberment
{
    // Name fragments identifying severable bones, most specific first.
    // Severing a bone takes its whole child chain with it (forearm takes hand).
    static readonly string[] SeverableBoneFragments =
    {
        "forearm",
        "upperarm",
        "hand",
        "calf",
        "thigh",
        "neck",
        "head",
    };

    // Max distance between hit point and bone to accept a sever
    const float MaxSeverDistance = 0.6f;

    // Cauterized edge glow
    static readonly Color CauterizeColor = new Color(1f, 0.45f, 0.1f);


    /// <summary>
    /// Attempt to sever the limb closest to hitPos. Returns true if a limb came off.
    /// </summary>
    public static bool TrySever(GameObject victim, Vector3 hitPos)
    {
        if (PhxBF3.Config.GoreLevel < 2 || !PhxBF3.Config.Dismemberment)
        {
            return false;
        }

        SkinnedMeshRenderer smr = victim.GetComponentInChildren<SkinnedMeshRenderer>();
        if (smr == null || smr.bones == null || smr.bones.Length == 0)
        {
            return false;
        }

        Transform severBone = FindClosestSeverableBone(smr, hitPos);
        if (severBone == null)
        {
            return false;
        }

        // Bones affected: the sever bone and everything below it
        HashSet<int> severedIndices = CollectBoneChainIndices(smr, severBone);
        if (severedIndices.Count == 0)
        {
            return false;
        }

        Mesh limbMesh = ExtractLimbMesh(smr, severedIndices, severBone);
        if (limbMesh != null)
        {
            SpawnLimbDebris(limbMesh, smr, severBone, hitPos);
        }

        // Collapse the severed chain on the body so the skinned verts vanish
        severBone.localScale = Vector3.zero;

        SpawnCauterizeGlow(severBone.parent != null ? severBone.parent.position : hitPos);
        return true;
    }

    static Transform FindClosestSeverableBone(SkinnedMeshRenderer smr, Vector3 hitPos)
    {
        Transform best = null;
        float bestDist = MaxSeverDistance;

        foreach (Transform bone in smr.bones)
        {
            if (bone == null) continue;
            string lower = bone.name.ToLowerInvariant();

            foreach (string fragment in SeverableBoneFragments)
            {
                if (lower.Contains(fragment))
                {
                    float d = Vector3.Distance(bone.position, hitPos);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        best = bone;
                    }
                    break;
                }
            }
        }
        return best;
    }

    static HashSet<int> CollectBoneChainIndices(SkinnedMeshRenderer smr, Transform severBone)
    {
        HashSet<int> indices = new HashSet<int>();
        for (int i = 0; i < smr.bones.Length; ++i)
        {
            Transform b = smr.bones[i];
            if (b == null) continue;
            if (b == severBone || b.IsChildOf(severBone))
            {
                indices.Add(i);
            }
        }
        return indices;
    }

    /// <summary>
    /// Builds a mesh containing only the triangles whose vertices are dominantly
    /// weighted to the severed bone chain, in the sever bone's local space.
    /// </summary>
    static Mesh ExtractLimbMesh(SkinnedMeshRenderer smr, HashSet<int> severedIndices, Transform severBone)
    {
        Mesh src = smr.sharedMesh;
        if (src == null || !src.isReadable) return null;

        BoneWeight[] weights = src.boneWeights;
        if (weights == null || weights.Length == 0) return null;

        // Bake the current pose so the limb piece matches what's on screen
        Mesh baked = new Mesh();
        smr.BakeMesh(baked);

        Vector3[] verts = baked.vertices;
        Vector3[] normals = baked.normals;
        Vector2[] uvs = src.uv;

        bool[] isSevered = new bool[weights.Length];
        for (int i = 0; i < weights.Length; ++i)
        {
            // dominant weight decides which side of the cut a vertex lands on
            BoneWeight w = weights[i];
            int dominant = w.boneIndex0;
            float max = w.weight0;
            if (w.weight1 > max) { max = w.weight1; dominant = w.boneIndex1; }
            if (w.weight2 > max) { max = w.weight2; dominant = w.boneIndex2; }
            if (w.weight3 > max) { dominant = w.boneIndex3; }
            isSevered[i] = severedIndices.Contains(dominant);
        }

        List<int> newTris = new List<int>();
        int[] remap = new int[verts.Length];
        for (int i = 0; i < remap.Length; ++i) remap[i] = -1;

        List<Vector3> newVerts = new List<Vector3>();
        List<Vector3> newNormals = new List<Vector3>();
        List<Vector2> newUVs = new List<Vector2>();

        int[] tris = src.triangles;
        for (int t = 0; t < tris.Length; t += 3)
        {
            int a = tris[t], b = tris[t + 1], c = tris[t + 2];
            if (!isSevered[a] || !isSevered[b] || !isSevered[c]) continue;

            for (int k = 0; k < 3; ++k)
            {
                int idx = tris[t + k];
                if (remap[idx] < 0)
                {
                    remap[idx] = newVerts.Count;
                    // BakeMesh output is in the renderer's local space
                    Vector3 world = smr.transform.TransformPoint(verts[idx]);
                    newVerts.Add(severBone.InverseTransformPoint(world));
                    if (normals != null && normals.Length == verts.Length)
                    {
                        newNormals.Add(severBone.InverseTransformDirection(
                            smr.transform.TransformDirection(normals[idx])));
                    }
                    if (uvs != null && uvs.Length == verts.Length)
                    {
                        newUVs.Add(uvs[idx]);
                    }
                }
                newTris.Add(remap[idx]);
            }
        }

        Object.Destroy(baked);
        if (newTris.Count == 0) return null;

        Mesh limb = new Mesh();
        limb.name = "SeveredLimb";
        limb.SetVertices(newVerts);
        if (newNormals.Count == newVerts.Count) limb.SetNormals(newNormals);
        if (newUVs.Count == newVerts.Count) limb.SetUVs(0, newUVs);
        limb.SetTriangles(newTris, 0);
        limb.RecalculateBounds();
        return limb;
    }

    static void SpawnLimbDebris(Mesh limbMesh, SkinnedMeshRenderer smr, Transform severBone, Vector3 hitPos)
    {
        GameObject limb = new GameObject("SeveredLimb");
        limb.transform.position = severBone.position;
        limb.transform.rotation = severBone.rotation;

        MeshFilter mf = limb.AddComponent<MeshFilter>();
        mf.mesh = limbMesh;
        MeshRenderer mr = limb.AddComponent<MeshRenderer>();
        mr.sharedMaterials = smr.sharedMaterials;

        // physics: fling away from the cut
        Rigidbody rb = limb.AddComponent<Rigidbody>();
        rb.mass = 4f;
        SphereCollider col = limb.AddComponent<SphereCollider>();
        col.radius = Mathf.Max(limbMesh.bounds.extents.magnitude * 0.5f, 0.05f);

        Vector3 fling = (severBone.position - hitPos).normalized + Vector3.up * 0.5f + Random.insideUnitSphere * 0.3f;
        rb.AddForce(fling.normalized * Random.Range(2.5f, 4.5f), ForceMode.VelocityChange);
        rb.AddTorque(Random.onUnitSphere * Random.Range(4f, 10f), ForceMode.VelocityChange);

        GameObject.Destroy(limb, 30f);
    }

    static void SpawnCauterizeGlow(Vector3 pos)
    {
        GameObject glow = new GameObject("CauterizeGlow");
        glow.transform.position = pos;

        PhxRuntimeAssets.CreatePointLight(glow, CauterizeColor, 0.6f, 400f);
        glow.AddComponent<PhxFadeAndDie>().Duration = 1.5f;
    }
}

/// <summary>Fades a light out over Duration seconds, then destroys the object.</summary>
public class PhxFadeAndDie : MonoBehaviour
{
    public float Duration = 1f;
    float Life;
    float StartIntensity = -1f;

    void Update()
    {
        Light l = GetComponent<Light>();
        if (l != null)
        {
            if (StartIntensity < 0f) StartIntensity = 400f;
            // must go through the helper - HDRP ignores Light.intensity writes
            PhxRuntimeAssets.SetIntensity(l, Mathf.Lerp(StartIntensity, 0f, Life / Duration));
        }
        Life += Time.deltaTime;
        if (Life >= Duration)
        {
            GameObject.Destroy(gameObject);
        }
    }
}
