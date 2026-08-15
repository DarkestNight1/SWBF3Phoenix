using System;
using System.Collections.Generic;
using System.IO;
using LibSWBF2.Wrappers;

namespace SkelProbe
{
    /// <summary>
    /// Finds whatever a map calls its water.
    /// </summary>
    /// <remarks>
    /// Written after two guesses about Kashyyyk's water turned out to be wrong.
    /// The first was that the terrain chunk carries a water height - it does
    /// not, LibSWBF2's tern INFO stops at grid and texture counts. The second
    /// was that the bed is painted with a water-named terrain layer, so the
    /// level could be measured off the painting - it is not: kas2's three
    /// terrain layers are kas2_main_1..3 and no stock map has a water-named
    /// layer at all.
    ///
    /// So rather than guess a third time, this asks the level what it has:
    /// every model, texture, entity class and world instance whose name reads
    /// as water. Whatever draws the water on Kashyyyk has to be one of those.
    ///
    /// Usage: SkelProbe --water &lt;lvl-or-directory&gt;
    /// </remarks>
    static class WaterProbe
    {
        static readonly string[] Keywords = { "water", "ocean", "river", "lake", "sea", "wet" };

        static bool Reads(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (string k in Keywords)
            {
                if (s.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        public static void Run(string path)
        {
            string name = Path.GetFileName(path);

            Level level;
            try { level = Level.FromFile(path); }
            catch (Exception e)
            {
                Console.WriteLine($"=== {name} : LOAD FAILED ({e.Message})");
                return;
            }
            if (level == null) return;

            var hits = new List<string>();

            try
            {
                foreach (Model m in level.Get<Model>())
                {
                    if (Reads(m.Name)) hits.Add($"  model    {m.Name}");
                }
            }
            catch (Exception e) { Console.WriteLine($"  models unreadable ({e.Message})"); }

            try
            {
                foreach (Texture t in level.Get<Texture>())
                {
                    if (Reads(t.Name)) hits.Add($"  texture  {t.Name}");
                }
            }
            catch (Exception e) { Console.WriteLine($"  textures unreadable ({e.Message})"); }

            try
            {
                foreach (EntityClass c in level.Get<EntityClass>())
                {
                    if (Reads(c.Name) || Reads(c.BaseClassName))
                    {
                        hits.Add($"  class    {c.Name}  (base {c.BaseClassName})");
                    }
                }
            }
            catch (Exception e) { Console.WriteLine($"  classes unreadable ({e.Message})"); }

            try
            {
                foreach (World w in level.Get<World>())
                {
                    foreach (Instance inst in w.GetInstances())
                    {
                        string cls = "";
                        try { cls = inst.EntityClassName; } catch { }
                        if (!Reads(inst.Name) && !Reads(cls)) continue;

                        var p = inst.Position;
                        hits.Add($"  instance '{inst.Name}' class '{cls}' " +
                                 $"at ({p.X:0.##}, {p.Y:0.##}, {p.Z:0.##})  [world {w.Name}]");
                    }
                }
            }
            catch (Exception e) { Console.WriteLine($"  instances unreadable ({e.Message})"); }

            if (hits.Count == 0) return;

            Console.WriteLine($"=== {name} : {hits.Count} water-ish name(s)");
            foreach (string h in hits) Console.WriteLine(h);
            Console.WriteLine();
        }
    }
}
