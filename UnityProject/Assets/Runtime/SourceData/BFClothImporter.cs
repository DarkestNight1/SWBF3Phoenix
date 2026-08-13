using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// Builds the cloth a model's <c>CLTH</c> chunks describe and hangs it off the
/// right bone.
/// </summary>
/// <remarks>
/// The parser in <see cref="BFClothReader"/> reaches data LibSWBF2 cannot -
/// there is no CLTH handler in the native library at all - but a decoded
/// definition renders nothing on its own. This is the half that puts it in the
/// scene: a mesh from the simulation vertices, a Unity <see cref="Cloth"/>
/// component driven by the authored pinned vertices, parented to the bone the
/// chunk names.
///
/// Fifty pieces ship across the side files: Anakin's skirt, the cloaked
/// variants, Royal Guard robes.
///
/// <para><b>Triangulation.</b> A CLTH stores vertices and constraint pairs,
/// not faces - the original renderer built the surface from the same grid the
/// simulation used. The constraint lists give the topology back: the stretch
/// pairs are the weave, so vertices sharing two stretch edges and a cross edge
/// form a quad. That is reconstructed here rather than guessed at, and where
/// it cannot be reconstructed the piece is skipped rather than drawn as
/// scrambled geometry.</para>
/// </remarks>
public static class BFClothImporter
{
    /// <summary>Cloth definitions by owning model name, built once per file.</summary>
    static readonly Dictionary<string, List<BFClothDefinition>> ByModel =
        new Dictionary<string, List<BFClothDefinition>>(System.StringComparer.OrdinalIgnoreCase);

    static readonly HashSet<string> ScannedFiles = new HashSet<string>();

    public static int PiecesAttached { get; private set; }

    public static void Reset()
    {
        ByModel.Clear();
        ScannedFiles.Clear();
        PiecesAttached = 0;
    }

    /// <summary>
    /// Index every cloth piece in a .lvl, keyed by the model that owns it.
    /// </summary>
    /// <remarks>
    /// Reads the file once and caches. Cloth lives in the side files, which
    /// are large, and a per-model scan would re-walk tens of megabytes for
    /// every soldier class the map mounts.
    /// </remarks>
    public static void Scan(string lvlPath)
    {
        if (string.IsNullOrEmpty(lvlPath) || !ScannedFiles.Add(lvlPath)) return;
        if (!File.Exists(lvlPath)) return;

        byte[] data;
        try { data = File.ReadAllBytes(lvlPath); }
        catch { return; }

        // Cheap rejection first. Only the side files carry cloth - fifty
        // pieces across the whole game - and every other .lvl the environment
        // mounts would otherwise pay for a full structured walk to discover it
        // has none. Scanning the raw bytes for the FourCC is a fraction of
        // that, and a file without the literal cannot contain the chunk.
        if (!ContainsFourCC(data, (byte)'C', (byte)'L', (byte)'T', (byte)'H')) return;

        foreach (BFClothDefinition cloth in BFClothReader.Read(data))
        {
            if (!cloth.IsUsable || string.IsNullOrEmpty(cloth.OwnerModel)) continue;

            if (!ByModel.TryGetValue(cloth.OwnerModel, out List<BFClothDefinition> list))
            {
                list = new List<BFClothDefinition>();
                ByModel.Add(cloth.OwnerModel, list);
            }
            list.Add(cloth);
        }
    }

    /// <summary>
    /// Attach every cloth piece belonging to <paramref name="modelName"/>.
    /// </summary>
    /// <param name="root">The built model, whose skeleton holds the bones.</param>
    public static void Attach(GameObject root, string modelName, Material material)
    {
        if (root == null || string.IsNullOrEmpty(modelName)) return;
        if (!ByModel.TryGetValue(modelName, out List<BFClothDefinition> pieces)) return;

        for (int i = 0; i < pieces.Count; ++i)
        {
            AttachOne(root, pieces[i], material);
        }
    }

    static void AttachOne(GameObject root, BFClothDefinition cloth, Material material)
    {
        int[] triangles = Triangulate(cloth);
        if (triangles == null) return;

        Transform parent = FindBone(root.transform, cloth.ParentBone) ?? root.transform;

        var obj = new GameObject(cloth.Name);
        obj.transform.SetParent(parent, false);
        obj.transform.localPosition = cloth.LocalTransform.GetColumn(3);

        var mesh = new Mesh { name = cloth.Name };
        mesh.SetVertices(new List<Vector3>(cloth.Positions));
        mesh.SetUVs(0, new List<Vector2>(cloth.UVs));
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        // SkinnedMeshRenderer, not MeshRenderer: Unity's Cloth component only
        // drives a skinned renderer, and it is the pairing that makes the
        // simulation visible at all.
        var renderer = obj.AddComponent<SkinnedMeshRenderer>();
        renderer.sharedMesh = mesh;
        renderer.sharedMaterial = material;
        renderer.updateWhenOffscreen = false;
        renderer.rootBone = parent;

        var sim = obj.AddComponent<Cloth>();
        sim.coefficients = BuildConstraints(cloth);

        // Damped, and with a little world acceleration scale: a cape on a
        // sprinting soldier is being moved by the character, not by wind, and
        // an undamped sheet driven by that motion oscillates rather than
        // trails.
        sim.damping = 0.4f;
        sim.worldAccelerationScale = 0.6f;
        sim.worldVelocityScale = 0.4f;
        sim.friction = 0.3f;

        ++PiecesAttached;
    }

    /// <summary>
    /// Per-vertex constraints: pinned vertices are fixed, the rest are free.
    /// </summary>
    /// <remarks>
    /// A pinned vertex gets zero max distance, which is what holds a skirt at
    /// the waist and a cloak at the shoulders. Without them the whole sheet is
    /// unconstrained and simply falls off the character.
    /// </remarks>
    static ClothSkinningCoefficient[] BuildConstraints(BFClothDefinition cloth)
    {
        var coefficients = new ClothSkinningCoefficient[cloth.Positions.Length];
        for (int i = 0; i < coefficients.Length; ++i)
        {
            coefficients[i].maxDistance = float.MaxValue;
            coefficients[i].collisionSphereDistance = float.MaxValue;
        }

        for (int i = 0; i < cloth.FixedPoints.Length; ++i)
        {
            int index = cloth.FixedPoints[i];
            if (index < 0 || index >= coefficients.Length) continue;

            coefficients[index].maxDistance = 0f;
        }

        return coefficients;
    }

    /// <summary>
    /// Recover the surface from the constraint graph.
    /// </summary>
    /// <remarks>
    /// Stretch pairs are the weave and cross pairs are the diagonals, so a
    /// cross edge (a,b) plus the stretch edges meeting a and b names a quad's
    /// two triangles. Walking the cross list is therefore enough to rebuild
    /// the faces without the file storing any.
    ///
    /// Returns null when the graph does not yield a surface, so a piece whose
    /// constraints did not decode is left out entirely rather than drawn as
    /// scrambled triangles - absent cloth is a missing cape, scrambled cloth
    /// is a visual fault that looks like a renderer bug.
    /// </remarks>
    static int[] Triangulate(BFClothDefinition cloth)
    {
        if (cloth.CrossConstraints.Length < 2 || cloth.StretchConstraints.Length < 2) return null;

        var neighbours = new Dictionary<int, HashSet<int>>();
        for (int i = 0; i + 1 < cloth.StretchConstraints.Length; i += 2)
        {
            int a = cloth.StretchConstraints[i];
            int b = cloth.StretchConstraints[i + 1];

            if (!neighbours.TryGetValue(a, out HashSet<int> na)) neighbours[a] = na = new HashSet<int>();
            if (!neighbours.TryGetValue(b, out HashSet<int> nb)) neighbours[b] = nb = new HashSet<int>();
            na.Add(b);
            nb.Add(a);
        }

        int vertexCount = cloth.Positions.Length;
        var triangles = new List<int>();

        for (int i = 0; i + 1 < cloth.CrossConstraints.Length; i += 2)
        {
            int a = cloth.CrossConstraints[i];
            int b = cloth.CrossConstraints[i + 1];
            if (a < 0 || b < 0 || a >= vertexCount || b >= vertexCount) continue;

            if (!neighbours.TryGetValue(a, out HashSet<int> na)) continue;
            if (!neighbours.TryGetValue(b, out HashSet<int> nb)) continue;

            // The two vertices adjacent to both ends of the diagonal are the
            // quad's other corners.
            int first = -1, second = -1;
            foreach (int candidate in na)
            {
                if (!nb.Contains(candidate)) continue;

                if (first < 0) first = candidate;
                else { second = candidate; break; }
            }

            if (first < 0 || second < 0) continue;

            triangles.Add(a); triangles.Add(first); triangles.Add(b);
            triangles.Add(b); triangles.Add(second); triangles.Add(a);
        }

        return triangles.Count >= 3 ? triangles.ToArray() : null;
    }

    static bool ContainsFourCC(byte[] data, byte a, byte b, byte c, byte d)
    {
        for (int i = 0; i + 3 < data.Length; ++i)
        {
            if (data[i] == a && data[i + 1] == b && data[i + 2] == c && data[i + 3] == d) return true;
        }
        return false;
    }

    static Transform FindBone(Transform root, string boneName)
    {
        if (string.IsNullOrEmpty(boneName)) return null;

        Transform[] all = root.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < all.Length; ++i)
        {
            if (string.Equals(all[i].name, boneName, System.StringComparison.OrdinalIgnoreCase))
            {
                return all[i];
            }
        }
        return null;
    }
}
