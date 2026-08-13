using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A piece of authored cloth: Anakin's skirt, a cloak, a hanging banner.
/// </summary>
/// <remarks>
/// <c>CLTH</c> is graphical data the stock game ships and nothing in this
/// project could see. LibSWBF2 has no handler for it - not a stub, no entry in
/// <c>EConfigType</c>, nothing - so every cape and robe in the game was being
/// imported as whatever static geometry the model happened to also contain,
/// or not at all. Fifty of them ship across the side files.
///
/// The layout is a small chunk tree:
/// <list type="bullet">
/// <item><c>INFO</c> - the model this cloth belongs to.</item>
/// <item><c>NAME</c> - the cloth piece itself.</item>
/// <item><c>PRNT</c> - the bone it hangs from.</item>
/// <item><c>XFRM</c> - a 4x3 transform relative to that bone.</item>
/// <item><c>DATA</c> - texture name, then the simulation mesh: positions,
/// UVs, which vertices are pinned, and the constraint pairs that hold the
/// cloth together.</item>
/// </list>
/// </remarks>
public sealed class BFClothDefinition
{
    /// <summary>Model this cloth belongs to, e.g. <c>rep_inf_ep3anakin</c>.</summary>
    public string OwnerModel;

    /// <summary>This piece, e.g. <c>rep_inf_ep3anakin_skirt</c>.</summary>
    public string Name;

    /// <summary>Bone the cloth hangs from, e.g. <c>bone_root</c>.</summary>
    public string ParentBone;

    public Matrix4x4 LocalTransform = Matrix4x4.identity;

    public string TextureName;

    public Vector3[] Positions = Array.Empty<Vector3>();
    public Vector2[] UVs = Array.Empty<Vector2>();

    /// <summary>
    /// Vertices pinned to the parent bone rather than simulated.
    /// </summary>
    /// <remarks>
    /// These are what stops a cape falling off the character - the waistband
    /// of a skirt, the shoulders of a cloak. Without them the whole piece is
    /// free and simply drops.
    /// </remarks>
    public int[] FixedPoints = Array.Empty<int>();

    /// <summary>Index pairs holding the sheet together along its weave.</summary>
    public int[] StretchConstraints = Array.Empty<int>();

    /// <summary>Diagonal pairs, which resist shear.</summary>
    public int[] CrossConstraints = Array.Empty<int>();

    /// <summary>Pairs spanning two edges, which resist folding.</summary>
    public int[] BendConstraints = Array.Empty<int>();

    /// <summary>Whether enough decoded to build a mesh from.</summary>
    public bool IsUsable => Positions.Length > 0 && Positions.Length == UVs.Length;

    public override string ToString() =>
        $"{Name} on {OwnerModel} @ {ParentBone}: {Positions.Length} vert, " +
        $"{FixedPoints.Length} pinned";
}

/// <summary>Decodes <c>CLTH</c> chunks out of a raw <c>.lvl</c>.</summary>
public static class BFClothReader
{
    /// <summary>Every cloth piece in a level file.</summary>
    public static List<BFClothDefinition> Read(byte[] data)
    {
        var cloths = new List<BFClothDefinition>();

        List<BFRawChunkReader.BFRawChunk> chunks = BFRawChunkReader.Find(data, "CLTH");
        for (int i = 0; i < chunks.Count; ++i)
        {
            BFClothDefinition cloth = ReadOne(data, chunks[i].Start, chunks[i].Length);
            if (cloth != null) cloths.Add(cloth);
        }

        return cloths;
    }

    static BFClothDefinition ReadOne(byte[] data, int start, int length)
    {
        var cloth = new BFClothDefinition();
        int end = start + length;
        int cursor = start;

        while (cursor + 8 <= end)
        {
            string name = System.Text.Encoding.ASCII.GetString(data, cursor, 4);
            int size = BitConverter.ToInt32(data, cursor + 4);
            if (size < 0 || cursor + 8 + size > end) break;

            int payload = cursor + 8;
            switch (name)
            {
                case "INFO": cloth.OwnerModel = BFRawChunkReader.ReadString(data, payload, size); break;
                case "NAME": cloth.Name = BFRawChunkReader.ReadString(data, payload, size); break;
                case "PRNT": cloth.ParentBone = BFRawChunkReader.ReadString(data, payload, size); break;
                case "XFRM": ReadTransform(data, payload, size, cloth); break;
                case "DATA": ReadGeometry(data, payload, size, cloth); break;
            }

            cursor = ((payload + size) + 3) & ~3;
        }

        return string.IsNullOrEmpty(cloth.Name) ? null : cloth;
    }

    /// <summary>
    /// The 4x3 row-major transform, converted to Unity's handedness.
    /// </summary>
    /// <remarks>
    /// SWBF2 is right-handed with +X mirrored relative to Unity, the same
    /// convention the model importer already applies - so cloth lands on the
    /// same bone, the same way round, as the mesh it hangs from.
    /// </remarks>
    static void ReadTransform(byte[] data, int at, int size, BFClothDefinition cloth)
    {
        if (size < 48) return;

        var m = new float[12];
        for (int i = 0; i < 12; ++i) m[i] = BitConverter.ToSingle(data, at + i * 4);

        var matrix = new Matrix4x4();
        matrix.SetColumn(0, new Vector4(m[0], m[1], m[2], 0f));
        matrix.SetColumn(1, new Vector4(m[3], m[4], m[5], 0f));
        matrix.SetColumn(2, new Vector4(m[6], m[7], m[8], 0f));
        matrix.SetColumn(3, new Vector4(-m[9], m[10], m[11], 1f));

        cloth.LocalTransform = matrix;
    }

    /// <summary>
    /// The simulation mesh and its constraints.
    /// </summary>
    /// <remarks>
    /// Every block is length-prefixed, so each count is checked against the
    /// bytes actually remaining before it is trusted. A layout surprise then
    /// stops the decode at the last block that made sense and keeps what came
    /// before it, rather than reading adjacent data as geometry - a wrong
    /// vertex count would otherwise produce a cape made of noise, which is
    /// harder to notice than one that is simply absent.
    /// </remarks>
    static void ReadGeometry(byte[] data, int at, int size, BFClothDefinition cloth)
    {
        int end = at + size;
        int cursor = at;

        cloth.TextureName = BFRawChunkReader.ReadString(data, cursor, size);
        cursor += cloth.TextureName.Length + 1;

        if (!TryCount(data, ref cursor, end, 12, out int vertexCount)) return;

        var positions = new Vector3[vertexCount];
        for (int i = 0; i < vertexCount; ++i, cursor += 12)
        {
            // Negated X, matching the model importer's handedness flip.
            positions[i] = new Vector3(
                -BitConverter.ToSingle(data, cursor),
                BitConverter.ToSingle(data, cursor + 4),
                BitConverter.ToSingle(data, cursor + 8));
        }
        cloth.Positions = positions;

        if (cursor + vertexCount * 8 > end) return;

        var uvs = new Vector2[vertexCount];
        for (int i = 0; i < vertexCount; ++i, cursor += 8)
        {
            uvs[i] = new Vector2(BitConverter.ToSingle(data, cursor),
                                 BitConverter.ToSingle(data, cursor + 4));
        }
        cloth.UVs = uvs;

        cloth.FixedPoints = ReadIndices(data, ref cursor, end, 1);

        // Fixed points may be followed by the bone names they are pinned to.
        // Skipping them by their own length keeps the cursor aligned for the
        // constraint blocks, which are the part worth having.
        SkipStringTable(data, ref cursor, end);

        cloth.StretchConstraints = ReadIndices(data, ref cursor, end, 2);
        cloth.CrossConstraints = ReadIndices(data, ref cursor, end, 2);
        cloth.BendConstraints = ReadIndices(data, ref cursor, end, 2);
    }

    /// <summary>A count that must fit in the bytes left, at the given stride.</summary>
    static bool TryCount(byte[] data, ref int cursor, int end, int stride, out int count)
    {
        count = 0;
        if (cursor + 4 > end) return false;

        int value = BitConverter.ToInt32(data, cursor);
        if (value < 0 || cursor + 4 + (long)value * stride > end) return false;

        cursor += 4;
        count = value;
        return true;
    }

    static int[] ReadIndices(byte[] data, ref int cursor, int end, int perEntry)
    {
        if (!TryCount(data, ref cursor, end, perEntry * 4, out int count)) return Array.Empty<int>();

        var indices = new int[count * perEntry];
        for (int i = 0; i < indices.Length; ++i, cursor += 4)
        {
            indices[i] = BitConverter.ToInt32(data, cursor);
        }
        return indices;
    }

    static void SkipStringTable(byte[] data, ref int cursor, int end)
    {
        if (cursor + 4 > end) return;

        int count = BitConverter.ToInt32(data, cursor);
        if (count < 0 || count > 4096) return;

        int probe = cursor + 4;
        for (int i = 0; i < count && probe < end; ++i)
        {
            string entry = BFRawChunkReader.ReadString(data, probe, end - probe);
            probe += entry.Length + 1;
        }

        if (probe <= end) cursor = probe;
    }
}
