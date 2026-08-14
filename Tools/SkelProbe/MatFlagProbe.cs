using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using LibSWBF2.Wrappers;
using LibSWBF2.Enums;
using LibSWBF2;

namespace SkelProbe
{
    /// <summary>
    /// Count material flags across a map's real geometry.
    /// </summary>
    /// <remarks>
    /// The HDRP import path handles Glow, Transparent, BumpMap, Specular and
    /// Doublesided, but not Hardedged (alpha cutout) or Additive - both of
    /// which are handled only in the legacy non-HDRP branch. Alpha cutout is
    /// what fences, grates, ladders, railings and foliage cards are made of,
    /// so if it is not applied those all render as solid rectangles.
    ///
    /// "Some geometry is affected" is not a reason to take on a risky shader
    /// change. A count is. This walks every segment of every model, since a
    /// material only matters in proportion to the geometry that uses it, and
    /// reports both distinct materials and the segments drawn with them.
    /// </remarks>
    static class MatFlagProbe
    {
        static readonly (EMaterialFlags Flag, string Name)[] Tracked =
        {
            (EMaterialFlags.Hardedged, "Hardedged (alpha cutout)"),
            (EMaterialFlags.Transparent, "Transparent"),
            (EMaterialFlags.Additive, "Additive"),
            (EMaterialFlags.Glow, "Glow"),
            (EMaterialFlags.BumpMap, "BumpMap"),
            (EMaterialFlags.Specular, "Specular"),
            (EMaterialFlags.Doublesided, "Doublesided"),
            (EMaterialFlags.EnvMap, "EnvMap"),
        };

        public static void Run(string path)
        {
            Container con = new Container();
            SWBF2Handle h = con.AddLevel(path);

            string extra = Environment.GetEnvironmentVariable("EXTRA_LVL");
            if (!string.IsNullOrEmpty(extra))
            {
                foreach (string e in extra.Split(';'))
                {
                    if (e.Length > 0 && File.Exists(e)) con.AddLevel(e);
                }
            }

            con.LoadLevels();

            for (int i = 0; i < 2400; ++i)
            {
                ELoadStatus s = con.GetStatus(h);
                if (s == ELoadStatus.Loaded || s == ELoadStatus.Failed) break;
                Thread.Sleep(50);
            }

            Level lvl = con.GetLevel(h, true);
            if (lvl == null)
            {
                Console.WriteLine($"=== {Path.GetFileName(path)}: not loaded");
                return;
            }

            Model[] models;
            try { models = lvl.Get<Model>(); }
            catch (Exception e)
            {
                Console.WriteLine($"=== {Path.GetFileName(path)}: {e.Message}");
                return;
            }

            var segmentsWith = new Dictionary<string, int>();
            var materialsWith = new Dictionary<string, HashSet<string>>();
            foreach (var t in Tracked)
            {
                segmentsWith[t.Name] = 0;
                materialsWith[t.Name] = new HashSet<string>();
            }

            int totalSegments = 0;
            var allMaterials = new HashSet<string>();

            foreach (Model model in models)
            {
                if (model == null) continue;

                Segment[] segments;
                try { segments = model.GetSegments(); } catch { continue; }
                if (segments == null) continue;

                foreach (Segment seg in segments)
                {
                    if (seg == null) continue;
                    LibSWBF2.Wrappers.Material mat = seg.Material;
                    if (mat == null) continue;

                    ++totalSegments;

                    // Identify a material by its first texture plus its flags -
                    // there is no material name in the format, and this is what
                    // distinguishes one from another for import purposes.
                    string key = (mat.Textures.Count > 0 ? mat.Textures[0] : "<none>")
                               + "|" + (uint)mat.MaterialFlags;
                    allMaterials.Add(key);

                    foreach (var t in Tracked)
                    {
                        if ((mat.MaterialFlags & t.Flag) == 0) continue;
                        segmentsWith[t.Name]++;
                        materialsWith[t.Name].Add(key);
                    }
                }
            }

            Console.WriteLine($"=== {Path.GetFileName(path)}: {models.Length} model(s), " +
                              $"{totalSegments} segment(s), {allMaterials.Count} distinct material(s)");

            foreach (var t in Tracked)
            {
                int segs = segmentsWith[t.Name];
                int mats = materialsWith[t.Name].Count;
                if (segs == 0 && mats == 0) continue;

                double pct = totalSegments == 0 ? 0 : 100.0 * segs / totalSegments;
                Console.WriteLine($"    {t.Name,-26} {mats,4} material(s)  {segs,5} segment(s)  {pct,5:F1}%");
            }
        }
    }
}
