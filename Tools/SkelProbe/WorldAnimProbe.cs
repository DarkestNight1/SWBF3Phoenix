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
    /// Which world holds a map's animation groups, and which holds the objects
    /// they animate?
    /// </summary>
    /// <remarks>
    /// Coruscant's import binds none of its world animations: every door and
    /// the gunship report the animation itself as found but the instance it
    /// drives as missing. A map lvl carries several worlds - one per game mode
    /// plus shared ones - so the likely explanation is that the group lives in
    /// a world the mode mounts while the instance lives in one it does not.
    ///
    /// This lists, per world, the animation groups and their instance names,
    /// then says which world actually contains each of those instances. If the
    /// two differ, the fix is about which worlds get searched, not about the
    /// animation system.
    /// </remarks>
    static class WorldAnimProbe
    {
        public static void Run(string path)
        {
            Container con = new Container();
            SWBF2Handle h = con.AddLevel(path);
            con.LoadLevels();

            for (int i = 0; i < 2400; ++i)
            {
                ELoadStatus s = con.GetStatus(h);
                if (s == ELoadStatus.Loaded || s == ELoadStatus.Failed) break;
                Thread.Sleep(50);
            }

            if (con.GetStatus(h) != ELoadStatus.Loaded)
            {
                Console.WriteLine($"=== {Path.GetFileName(path)}: not loaded ({con.GetStatus(h)})");
                return;
            }

            Level lvl = con.GetLevel(h, true);
            if (lvl == null) { Console.WriteLine("no level"); return; }

            World[] worlds = lvl.Get<World>();
            Console.WriteLine($"=== {Path.GetFileName(path)}: {worlds.Length} world(s)");

            // instance name -> worlds that contain it
            var instanceHome = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var animHome = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (World w in worlds)
            {
                Instance[] insts;
                try { insts = w.GetInstances(); } catch { insts = new Instance[0]; }
                foreach (Instance inst in insts)
                {
                    if (inst == null || string.IsNullOrEmpty(inst.Name)) continue;
                    if (!instanceHome.TryGetValue(inst.Name, out List<string> l))
                    {
                        l = new List<string>();
                        instanceHome[inst.Name] = l;
                    }
                    l.Add(w.Name);
                }

                foreach (WorldAnimation a in w.GetAnimations())
                {
                    if (a == null || string.IsNullOrEmpty(a.Name)) continue;
                    if (!animHome.TryGetValue(a.Name, out List<string> l))
                    {
                        l = new List<string>();
                        animHome[a.Name] = l;
                    }
                    l.Add(w.Name);
                }
            }

            foreach (World w in worlds)
            {
                WorldAnimationGroup[] groups;
                try { groups = w.GetAnimationGroups(); } catch { continue; }

                int instCount;
                try { instCount = w.GetInstances().Length; } catch { instCount = -1; }

                Console.WriteLine($"--- world '{w.Name}': {instCount} instance(s), " +
                                  $"{w.GetAnimations().Length} animation(s), {groups.Length} group(s)");

                foreach (WorldAnimationGroup g in groups)
                {
                    var pairs = g.GetAnimationInstancePairs();
                    Console.WriteLine($"    group '{g.Name}' ({pairs.Count} pair(s), playsAtStart {g.PlaysAtStart})");

                    foreach (var p in pairs)
                    {
                        string animWhere = animHome.TryGetValue(p.Item1, out List<string> al)
                            ? string.Join(",", al) : "MISSING";
                        string instWhere = instanceHome.TryGetValue(p.Item2, out List<string> il)
                            ? string.Join(",", il) : "MISSING";

                        string flag = instWhere == "MISSING" ? "  <== instance not in ANY world"
                                    : (instWhere.Contains(w.Name) ? "" : "  <== instance lives elsewhere");

                        Console.WriteLine($"        anim {p.Item1,-22} [{animWhere}]  " +
                                          $"inst {p.Item2,-22} [{instWhere}]{flag}");
                    }
                }
            }
        }
    }
}
