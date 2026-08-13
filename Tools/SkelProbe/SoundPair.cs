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
    /// Do a level's sample headers resolve once the shared bank is mounted?
    /// </summary>
    /// <remarks>
    /// A level's sound lvl carries SampleBank Info chunks with no Data chunk -
    /// 1265 clip headers in kas.lvl, none of which carry samples. common.bnk is
    /// the opposite: one bank, 77MB of Data, every clip decodable. The open
    /// question is whether the level headers are a manifest naming clips that
    /// live in the shared bank, or whether they name audio that this
    /// installation simply does not have.
    ///
    /// Loading both into one Container and asking for each level clip by hash
    /// answers it, and does so through Container.Get&lt;Sound&gt; - the exact
    /// call SoundLoader makes at runtime.
    /// </remarks>
    static class SoundPair
    {
        static Level Load(Container con, string path)
        {
            SWBF2Handle h = con.AddLevel(path);
            return null;   // levels are fetched after LoadLevels
        }

        public static void Run(string[] paths)
        {
            if (paths.Length < 2)
            {
                Console.WriteLine("--soundpair needs <shared.bnk> <level.lvl> [more.lvl ...]");
                return;
            }

            Container con = new Container();
            var handles = new List<SWBF2Handle>();
            foreach (string p in paths) handles.Add(con.AddLevel(p));
            con.LoadLevels();

            for (int i = 0; i < 1200; ++i)
            {
                bool allDone = true;
                foreach (SWBF2Handle h in handles)
                {
                    ELoadStatus s = con.GetStatus(h);
                    if (s != ELoadStatus.Loaded && s != ELoadStatus.Failed) allDone = false;
                }
                if (allDone) break;
                Thread.Sleep(50);
            }

            // Everything the shared bank can supply, by hash.
            var shared = new HashSet<uint>();
            Level sharedLvl = con.GetLevel(handles[0], true);
            if (sharedLvl != null)
            {
                foreach (SoundBank b in sharedLvl.Get<SoundBank>())
                {
                    Sound[] ss;
                    try { ss = b.GetSounds(); } catch { continue; }
                    if (ss == null) continue;
                    foreach (Sound s in ss) if (s != null && s.HasData) shared.Add(s.Name);
                }
            }
            Console.WriteLine($"=== shared bank {Path.GetFileName(paths[0])}: {shared.Count} clip(s) with data");

            for (int i = 1; i < paths.Length; ++i)
            {
                Level lvl = con.GetLevel(handles[i], true);
                if (lvl == null)
                {
                    Console.WriteLine($"--- {Path.GetFileName(paths[i])}: not loaded");
                    continue;
                }

                int total = 0, inShared = 0, viaContainer = 0;
                foreach (SoundBank b in lvl.Get<SoundBank>())
                {
                    Sound[] ss;
                    try { ss = b.GetSounds(); } catch { continue; }
                    if (ss == null) continue;

                    foreach (Sound s in ss)
                    {
                        if (s == null) continue;
                        ++total;
                        if (shared.Contains(s.Name)) ++inShared;

                        Sound found = con.Get<Sound>(s.Name);
                        if (found != null && found.HasData) ++viaContainer;
                    }
                }

                double pct = total == 0 ? 0 : 100.0 * inShared / total;
                Console.WriteLine($"--- {Path.GetFileName(paths[i])}: {total} header(s), " +
                                  $"{inShared} in shared bank ({pct:F1}%), " +
                                  $"{viaContainer} resolvable via Container.Get<Sound>");
            }
        }
    }
}
