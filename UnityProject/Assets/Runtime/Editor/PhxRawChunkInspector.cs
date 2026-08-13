using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Reads the chunk types LibSWBF2 cannot reach out of the installed game and
/// reports what is there.
/// </summary>
/// <remarks>
/// This exists to make the raw reader checkable against the real files rather
/// than trusted. "Cloth is now reachable" is a claim; "fifty pieces decoded,
/// here are their names, vertex counts and parent bones" is a result - and if
/// the decode is wrong, a skirt with two vertices pinned to a bone that does
/// not exist says so immediately.
/// </remarks>
public static class PhxRawChunkInspector
{
    /// <summary>Chunk types with no route into C# through the native library.</summary>
    static readonly string[] Unreachable = { "CLTH", "plnp", "port", "mcfg", "sanm", "load" };

    [MenuItem("Phoenix/Source Data/Report Unreachable Chunks")]
    public static void Report()
    {
        string root = PhxGame.Instance != null ? PhxGame.Instance.GamePath?.ToString() : null;
        if (string.IsNullOrEmpty(root))
        {
            Debug.LogWarning("[BFSource] No game path known - enter play mode once so the " +
                             "install is detected, then run this again.");
            return;
        }

        string lvlRoot = Path.Combine(root, "GameData/data/_lvl_pc");
        if (!Directory.Exists(lvlRoot))
        {
            Debug.LogWarning($"[BFSource] '{lvlRoot}' does not exist.");
            return;
        }

        var totals = new Dictionary<string, int>();
        var cloths = new List<BFClothDefinition>();

        foreach (string file in Directory.EnumerateFiles(lvlRoot, "*.lvl", SearchOption.AllDirectories))
        {
            byte[] data;
            try { data = File.ReadAllBytes(file); }
            catch { continue; }

            List<BFRawChunkReader.BFRawChunk> found = BFRawChunkReader.Find(data, Unreachable);
            if (found.Count == 0) continue;

            for (int i = 0; i < found.Count; ++i)
            {
                totals.TryGetValue(found[i].Name, out int n);
                totals[found[i].Name] = n + 1;
            }

            cloths.AddRange(BFClothReader.Read(data));
        }

        var sb = new StringBuilder();
        sb.AppendLine("[BFSource] Chunks reached without the native library:");
        foreach (KeyValuePair<string, int> entry in totals)
        {
            sb.Append("  ").Append(entry.Key.PadRight(6)).Append(entry.Value).AppendLine();
        }

        int usable = 0;
        for (int i = 0; i < cloths.Count; ++i) if (cloths[i].IsUsable) ++usable;

        sb.Append("  cloth decoded: ").Append(usable).Append('/').Append(cloths.Count)
          .AppendLine(" piece(s) with usable geometry");

        for (int i = 0; i < cloths.Count && i < 12; ++i)
        {
            sb.Append("    ").AppendLine(cloths[i].ToString());
        }

        Debug.Log(sb.ToString());
    }
}
