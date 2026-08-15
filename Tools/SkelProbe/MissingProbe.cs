using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LibSWBF2.Wrappers;

namespace SkelProbe
{
    /// <summary>
    /// What every map places that Phoenix has no runtime type for.
    /// </summary>
    /// <remarks>
    /// A world instance is only built if <c>PhxClassRegister</c> knows its base
    /// class. When it does not, the instance is skipped and whatever it was
    /// supposed to be simply is not in the level - with no error, because from
    /// the importer's side nothing went wrong. Kashyyyk's grass is the case
    /// that prompted this: grasspatch has no entry, so every instance of one is
    /// silently dropped.
    ///
    /// Base classes are declared in the level that defines the entity class,
    /// which for anything named com_* is common.lvl rather than the map. Those
    /// have to be loaded alongside or two thirds of every census comes back as
    /// "(unknown)" - which is a probe that cannot read, not a game that cannot
    /// build.
    ///
    /// <see cref="Registered"/> is a copy of PhxClassRegister's keys and has to
    /// be kept in step with it by hand. That is a real cost, and it buys the
    /// only view of the question that does not require running Unity.
    ///
    /// Usage: SkelProbe --missing &lt;map.lvl ...&gt;
    /// </remarks>
    static class MissingProbe
    {
        /// <summary>
        /// Base classes PhxClassRegister has a runtime type for, as of writing.
        /// </summary>
        static readonly HashSet<string> Registered = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "prop", "door", "animatedprop", "destructablebuilding", "armedbuilding",
            "building", "animatedbuilding", "mine", "beacon", "remoteterminal",
            "detonator", "repair", "leafpatch", "soundambiencestatic", "dusteffect",
            "rumbleeffect", "commandpost", "hologram", "soldier", "powerupstation", "grasspatch",
            "hover", "commandhover", "flyer", "commandflyer", "walker", "commandwalker",
            "vehiclespawn", "turret", "weapon", "grenade", "launcher", "cannon",
            "melee", "missile", "sticky", "shell", "beam", "bolt", "bullet", "explosion",
        };

        /// <summary>class name -> base class, across every level loaded so far.</summary>
        static readonly Dictionary<string, string> BaseOf =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>base class -> instances, summed over every map.</summary>
        static readonly Dictionary<string, int> TotalByBase =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// <summary>base class -> the classes seen using it, and their counts.</summary>
        static readonly Dictionary<string, Dictionary<string, int>> ClassesByBase =
            new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>base class -> maps that place one.</summary>
        static readonly Dictionary<string, SortedSet<string>> MapsByBase =
            new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);

        static bool LibraryLoaded;

        /// <summary>
        /// Load the shared levels that define com_* and other cross-map classes.
        /// </summary>
        static void LoadLibrary(string anyMapPath)
        {
            if (LibraryLoaded) return;
            LibraryLoaded = true;

            // _lvl_pc/<map>/<map>N.lvl -> _lvl_pc
            string root = Path.GetDirectoryName(Path.GetDirectoryName(anyMapPath));
            if (root == null) return;

            foreach (string shared in new[] { "common.lvl", "ingame.lvl", "mission.lvl", "core.lvl" })
            {
                string p = Path.Combine(root, shared);
                if (!File.Exists(p)) continue;
                Harvest(p);
            }

            // Side levels: the per-planet <abc>.lvl beside the map folders, which
            // carry that planet's shared props.
            foreach (string p in Directory.GetFiles(root, "*.lvl", SearchOption.TopDirectoryOnly))
            {
                Harvest(p);
            }

            Console.WriteLine($"[library] {BaseOf.Count} entity class(es) resolved from shared levels");
            Console.WriteLine();
        }

        static void Harvest(string path)
        {
            try
            {
                Level lv = Level.FromFile(path);
                if (lv == null) return;
                foreach (EntityClass c in lv.Get<EntityClass>())
                {
                    try { BaseOf[c.Name] = c.BaseClassName; } catch { }
                }
            }
            catch { }
        }

        public static void Run(string path)
        {
            LoadLibrary(path);

            string map = Path.GetFileNameWithoutExtension(path);

            Level level;
            try { level = Level.FromFile(path); }
            catch (Exception e)
            {
                Console.WriteLine($"=== {map} : LOAD FAILED ({e.Message})");
                return;
            }
            if (level == null) return;

            // The map's own classes win over anything the library had.
            foreach (EntityClass c in level.Get<EntityClass>())
            {
                try { BaseOf[c.Name] = c.BaseClassName; } catch { }
            }

            var missing = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
            var unresolved = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int total = 0, built = 0;

            foreach (World w in level.Get<World>())
            {
                Instance[] instances;
                try { instances = w.GetInstances(); } catch { continue; }

                foreach (Instance inst in instances)
                {
                    string cls;
                    try { cls = inst.EntityClassName; } catch { continue; }
                    if (string.IsNullOrEmpty(cls)) continue;

                    ++total;

                    if (!BaseOf.TryGetValue(cls, out string bas) || string.IsNullOrEmpty(bas))
                    {
                        unresolved.TryGetValue(cls, out int u);
                        unresolved[cls] = u + 1;
                        continue;
                    }

                    if (Registered.Contains(bas)) { ++built; continue; }

                    if (!missing.TryGetValue(bas, out var byClass))
                    {
                        byClass = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                        missing[bas] = byClass;
                    }
                    byClass.TryGetValue(cls, out int n);
                    byClass[cls] = n + 1;

                    TotalByBase.TryGetValue(bas, out int t);
                    TotalByBase[bas] = t + 1;

                    if (!ClassesByBase.TryGetValue(bas, out var all))
                    {
                        all = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                        ClassesByBase[bas] = all;
                    }
                    all.TryGetValue(cls, out int a);
                    all[cls] = a + 1;

                    if (!MapsByBase.TryGetValue(bas, out var maps))
                    {
                        maps = new SortedSet<string>(); MapsByBase[bas] = maps;
                    }
                    maps.Add(map);
                }
            }

            int dropped = missing.Values.Sum(d => d.Values.Sum());
            Console.WriteLine($"=== {map} : {total} instance(s), {built} buildable, " +
                              $"{dropped} DROPPED, {unresolved.Values.Sum()} unresolved");

            foreach (var kv in missing.OrderByDescending(k => k.Value.Values.Sum()))
            {
                Console.WriteLine($"  base '{kv.Key}' has no runtime type - " +
                                  $"{kv.Value.Values.Sum()} instance(s) dropped:");
                foreach (var c in kv.Value.OrderByDescending(x => x.Value))
                {
                    Console.WriteLine($"      {c.Key,-36} x{c.Value}");
                }
            }

            if (unresolved.Count > 0)
            {
                Console.WriteLine($"  (base class unknown to the probe for {unresolved.Count} " +
                                  "class(es) - defined in a level not loaded here, not necessarily a gap)");
            }
            Console.WriteLine();
        }

        /// <summary>Totals across every map probed in this run.</summary>
        public static void Summary()
        {
            if (TotalByBase.Count == 0)
            {
                Console.WriteLine("=== no unimplemented base classes placed by any map probed.");
                return;
            }

            Console.WriteLine("=== TOTAL across every map probed");
            foreach (var kv in TotalByBase.OrderByDescending(k => k.Value))
            {
                MapsByBase.TryGetValue(kv.Key, out var maps);
                Console.WriteLine($"  {kv.Key,-24} {kv.Value,5} instance(s)   maps: " +
                                  $"{string.Join(", ", maps ?? new SortedSet<string>())}");
                foreach (var c in ClassesByBase[kv.Key].OrderByDescending(x => x.Value).Take(6))
                {
                    Console.WriteLine($"      {c.Key,-36} x{c.Value}");
                }
            }
        }
    }
}
