using System;
using System.Collections.Generic;
using System.IO;
using LibSWBF2.Wrappers;

namespace SkelProbe
{
    /// <summary>
    /// Counts world instances by entity class and base class, across every
    /// world in a level.
    /// </summary>
    /// <remarks>
    /// A class existing in a level does not mean the map places any, and the
    /// difference decides whether an unimplemented base class is a missing
    /// feature or dead data. Counting per world matters too: a level has a
    /// dozen worlds and foliage does not necessarily live in the same one as
    /// the buildings.
    ///
    /// Usage: SkelProbe --census &lt;lvl&gt; [BASE_FILTER=grasspatch,leafpatch]
    /// </remarks>
    static class CensusProbe
    {
        public static void Run(string path)
        {
            string name = Path.GetFileName(path);
            string filter = Environment.GetEnvironmentVariable("BASE_FILTER");
            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(filter))
            {
                foreach (string s in filter.Split(',')) wanted.Add(s.Trim());
            }

            Level level;
            try { level = Level.FromFile(path); } catch { return; }
            if (level == null) return;

            var baseOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (EntityClass c in level.Get<EntityClass>())
            {
                try { baseOf[c.Name] = c.BaseClassName; } catch { }
            }

            var perClass = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var perBase = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var worldsOf = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (World w in level.Get<World>())
            {
                Instance[] instances;
                try { instances = w.GetInstances(); } catch { continue; }

                foreach (Instance inst in instances)
                {
                    string cls;
                    try { cls = inst.EntityClassName; } catch { continue; }
                    if (string.IsNullOrEmpty(cls)) continue;

                    baseOf.TryGetValue(cls, out string bas);
                    bas ??= "(unknown)";

                    if (wanted.Count > 0 && !wanted.Contains(bas)) continue;

                    perClass.TryGetValue(cls, out int n); perClass[cls] = n + 1;
                    perBase.TryGetValue(bas, out int m); perBase[bas] = m + 1;

                    if (!worldsOf.TryGetValue(cls, out var set))
                    {
                        set = new HashSet<string>(); worldsOf[cls] = set;
                    }
                    set.Add(w.Name);
                }
            }

            Console.WriteLine($"=== {name} : instance census" +
                              (wanted.Count > 0 ? $" (base filter: {filter})" : ""));
            foreach (var kv in perBase) Console.WriteLine($"  base {kv.Key,-24} {kv.Value} instance(s)");
            foreach (var kv in perClass)
            {
                baseOf.TryGetValue(kv.Key, out string bas);
                Console.WriteLine($"    {kv.Key,-34} x{kv.Value,-5} base {bas}  " +
                                  $"worlds [{string.Join(", ", worldsOf[kv.Key])}]");
            }
            Console.WriteLine();
        }
    }
}
