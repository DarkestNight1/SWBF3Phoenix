using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Turns an object's authored CHUNKSECTIONs into physical wreckage at the
/// moment it dies.
/// </summary>
/// <remarks>
/// A section names its geometry one of two ways and they mean different
/// things:
///
/// <list type="bullet">
/// <item><c>ChunkGeometryName</c> - a standalone model in the mounted data
/// (a hull half, a turret ring). Loaded like any other model.</item>
/// <item><c>ChunkNodeName</c> - a node of the dying object's *own* model. The
/// piece is already on screen attached to the vehicle; destruction detaches it
/// and hands it to physics. Reusing the live node is what makes the break-up
/// line up with the vehicle instead of a second copy appearing inside
/// it.</item>
/// </list>
///
/// Falling back from one to the other would be wrong in both directions, so a
/// section that names neither is skipped and counted rather than guessed at.
/// </remarks>
public static class PhxChunkSpawner
{
    /// <summary>
    /// Build every chunk of <paramref name="section"/> for a dying object.
    /// </summary>
    /// <param name="section">The object's parsed CHUNKSECTION list.</param>
    /// <param name="origin">Transform of the object being destroyed.</param>
    /// <param name="inheritedVelocity">Its velocity at the moment of death.</param>
    /// <returns>How many chunks were actually created.</returns>
    public static int Spawn(PhxPropertySection section, Transform origin, Vector3 inheritedVelocity)
    {
        if (section == null || origin == null) return 0;

        Vector3 centre = origin.position;
        int spawned = 0;

        foreach (Dictionary<string, IPhxPropRef> chunk in section)
        {
            if (chunk == null) continue;

            GameObject piece = BuildPiece(chunk, origin);
            if (piece == null) continue;

            // Chunks outlive the wreck, so they cannot stay parented to it -
            // the vehicle's own GameObject is destroyed moments later and
            // would take the whole break-up with it.
            piece.transform.SetParent(null, true);
            piece.SetActive(true);

            PreparePhysics(piece);
            piece.AddComponent<PhxChunk>().Init(chunk, centre, inheritedVelocity);
            ++spawned;
        }

        return spawned;
    }

    static GameObject BuildPiece(Dictionary<string, IPhxPropRef> chunk, Transform origin)
    {
        string nodeName = ReadString(chunk, "ChunkNodeName");
        if (!string.IsNullOrEmpty(nodeName))
        {
            Transform node = UnityUtils.FindChildTransform(origin, nodeName);
            if (node != null)
            {
                return node.gameObject;
            }
            // Named a node the model does not have. Worth reporting: it means
            // either the odf and the model disagree, or the model failed to
            // import fully, and both produce a visibly incomplete break-up.
            BFImportDiagnostics.Missing(BFSourceKind.Model, nodeName,
                                        $"chunk node of '{origin.name}'");
            return null;
        }

        string geometryName = ReadString(chunk, "ChunkGeometryName");
        if (string.IsNullOrEmpty(geometryName)) return null;

        GameObject piece = ModelLoader.Instance.GetGameObjectFromModel(geometryName, null);
        if (piece == null)
        {
            BFImportDiagnostics.Missing(BFSourceKind.Model, geometryName,
                                        $"chunk of '{origin.name}'");
            return null;
        }

        piece.transform.SetPositionAndRotation(origin.position, origin.rotation);
        return piece;
    }

    /// <summary>
    /// Give a chunk a body and collision it can actually simulate with.
    /// </summary>
    /// <remarks>
    /// Imported collision meshes are frequently concave, and PhysX refuses to
    /// attach a non-convex MeshCollider to a moving Rigidbody - it throws, and
    /// the chunk is lost. Convex hulls of the authored meshes are close enough
    /// for debris and are what the vehicle code already does for its own
    /// non-static colliders.
    /// </remarks>
    static void PreparePhysics(GameObject piece)
    {
        MeshCollider[] meshColliders = piece.GetComponentsInChildren<MeshCollider>(true);
        bool hasCollider = false;

        for (int i = 0; i < meshColliders.Length; ++i)
        {
            MeshCollider coll = meshColliders[i];
            coll.convex = true;
            coll.isTrigger = false;
            hasCollider = true;
        }
        if (!hasCollider)
        {
            hasCollider = piece.GetComponentInChildren<Collider>(true) != null;
        }

        if (!hasCollider)
        {
            // A chunk with no collision never reports a terrain hit, so it
            // would never settle and would fall forever. Give it a hull sized
            // to whatever geometry it has.
            Bounds bounds = UnityUtils.GetMaxBounds(piece);
            BoxCollider box = piece.AddComponent<BoxCollider>();
            box.center = piece.transform.InverseTransformPoint(bounds.center);
            box.size = bounds.size;
        }

        Rigidbody body = piece.GetComponent<Rigidbody>();
        if (body == null)
        {
            body = piece.AddComponent<Rigidbody>();
        }
        body.isKinematic = false;
    }

    static string ReadString(Dictionary<string, IPhxPropRef> chunk, string name)
    {
        if (chunk != null && chunk.TryGetValue(name, out IPhxPropRef prop) && prop is PhxProp<string> s)
        {
            return s.Get();
        }
        return null;
    }
}
