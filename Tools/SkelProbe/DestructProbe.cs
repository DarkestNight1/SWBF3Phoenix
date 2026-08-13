using System;
using System.Collections.Generic;
using System.Threading;
using LibSWBF2.Wrappers;
using LibSWBF2.Enums;
using LibSWBF2;

namespace SkelProbe
{
    /// <summary>
    /// Enumerate an lvl's destructible objects, loading it the way the game does.
    /// </summary>
    /// <remarks>
    /// Level.FromFile reads only the top level of an lvl. BF2 map files keep
    /// their content in named sub-LVLs, which is why probing kas2.lvl that way
    /// reported zero entity classes for a 23MB file - the content was there and
    /// simply not expanded. A Container with no sub-LVL filter loads all of it,
    /// which is what the game effectively does once the mission script's
    /// ReadDataFile calls have named what it wants.
    ///
    /// Reports the properties that decide whether a destructible works at all:
    /// what it turns into, what it throws, and what it explodes as.
    /// </remarks>
    static class DestructProbe
    {
        static readonly string[] Interesting =
        {
            "GeometryName", "DestroyedGeometryName", "MaxHealth", "ExplosionName",
            "ChunkGeometryName", "ChunkNodeName", "ChunkPhysics", "ChunkTerrainCollisions",
            "ChunkEmitter", "ChunkSmokeEffect", "ChunkTrailEffect",
            "DamageEffect", "DamageAttachPoint", "DestructionName", "ClassLabel",
            "HealthType", "Collision", "FoleyFXClass",
            "DamageStartPercent", "DamageStopPercent",
        };

        public static void Run(string path, string label)
        {
            Container con = new Container();
            SWBF2Handle handle = con.AddLevel(path);
            con.LoadLevels();

            // Loading is asynchronous; wait for it rather than racing it.
            for (int i = 0; i < 600; ++i)
            {
                ELoadStatus status = con.GetStatus(handle);
                if (status == ELoadStatus.Loaded || status == ELoadStatus.Failed) break;
                Thread.Sleep(50);
            }

            if (con.GetStatus(handle) != ELoadStatus.Loaded)
            {
                Console.WriteLine($"=== {label}: NOT LOADED ({con.GetStatus(handle)})");
                return;
            }

            Level level = con.GetLevel(handle, true);
            if (level == null)
            {
                Console.WriteLine($"=== {label}: container returned no level");
                return;
            }

            EntityClass[] classes;
            try { classes = level.Get<EntityClass>(); }
            catch (Exception e)
            {
                Console.WriteLine($"=== {label}: {e.Message}");
                return;
            }

            var byBase = new SortedDictionary<string, int>();
            var destructibles = new List<EntityClass>();

            foreach (EntityClass ec in classes)
            {
                if (ec == null) continue;

                string b = ec.BaseClassName ?? "<none>";
                byBase.TryGetValue(b, out int n);
                byBase[b] = n + 1;

                if (b.Equals("destructablebuilding", StringComparison.OrdinalIgnoreCase) ||
                    b.Equals("animatedbuilding", StringComparison.OrdinalIgnoreCase) ||
                    b.Equals("armedbuilding", StringComparison.OrdinalIgnoreCase) ||
                    b.Equals("destruction", StringComparison.OrdinalIgnoreCase))
                {
                    destructibles.Add(ec);
                }
            }

            Console.WriteLine($"=== {label}: {classes.Length} entity class(es)");
            foreach (var kv in byBase)
            {
                if (kv.Value >= 2 || kv.Key.Contains("build") || kv.Key.Contains("destru"))
                {
                    Console.WriteLine($"      {kv.Value,4}  {kv.Key}");
                }
            }

            if (destructibles.Count == 0)
            {
                Console.WriteLine("      (no destructible classes)");
                return;
            }

            Console.WriteLine($"    --- {destructibles.Count} destructible(s) ---");
            foreach (EntityClass ec in destructibles)
            {
                Console.WriteLine($"    * {ec.Name}  (base {ec.BaseClassName})");
                foreach (string prop in Interesting)
                {
                    if (ec.GetProperty(prop, out string val) && !string.IsNullOrEmpty(val))
                    {
                        Console.WriteLine($"        {prop,-24} = {val}");
                    }
                }
            }
        }
    }
}
