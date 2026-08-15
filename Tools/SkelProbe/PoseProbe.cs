using System;
using System.Collections.Generic;
using System.IO;
using LibSWBF2.Wrappers;
using LibSWBF2.Utils;

namespace SkelProbe
{
    /// <summary>
    /// Which animation bank actually contains a given clip.
    /// </summary>
    /// <remarks>
    /// PhxSoldier.CreatePilotPoser searches bank x clip combinations for a
    /// rider pose and finds nothing, so every soldier on a turret or speeder
    /// sits unposed. The clip names it builds are right - human_minigun_9pose
    /// is in the game's own CRC table - so the banks it searches must be wrong.
    ///
    /// Clip names are stored as CRCs, so this goes the other way: hash the
    /// candidate names, then report every bank that contains the hash. What
    /// comes back is the bank list CreatePilotPoser should have been using.
    ///
    /// Usage: POSE_NAMES=a,b,c SkelProbe --poses &lt;lvl ...&gt;
    /// </remarks>
    static class PoseProbe
    {
        static readonly string[] BankNames = {
            "human_4", "human_0", "human_1", "human_2", "human_3", "human",
            "humanlz", "com", "rep", "imp", "all", "cis", "gam", "wok",
            "droideka", "human_sabre", "human_tool", "human_pilot", "pilot",
            "vehicles", "vehicle", "misc", "ingame", "common", "turret",
        };

        public static void Run(string path)
        {
            string filter = Environment.GetEnvironmentVariable("POSE_NAMES");
            if (string.IsNullOrEmpty(filter))
            {
                Console.WriteLine("set POSE_NAMES=clip1,clip2");
                return;
            }

            var wanted = new List<string>();
            foreach (string s in filter.Split(',')) wanted.Add(s.Trim());

            Level level;
            try { level = Level.FromFile(path); } catch { return; }
            if (level == null) return;

            AnimationBank[] banks;
            try { banks = level.Get<AnimationBank>(); } catch { return; }
            if (banks.Length == 0) return;

            string file = Path.GetFileName(path);

            foreach (string bn in BankNames)
            {
                AnimationBank named = null;
                try { named = level.Get<AnimationBank>(bn); } catch { }
                if (named == null) continue;
                foreach (string clip in wanted)
                {
                    uint c2 = HashUtils.GetCRC(clip);
                    if (named.GetAnimationMetadata(c2, out int f2, out int b2) && f2 > 0)
                    {
                        Console.WriteLine($"  NAMED-HIT  {Path.GetFileName(path)}  bank \"{bn}\"  clip \"{clip}\"  {f2}f/{b2}b");
                    }
                }
            }

            foreach (AnimationBank bank in banks)
            {
                string bankName = "#" + Array.IndexOf(banks, bank);

                foreach (string clip in wanted)
                {
                    uint crc = HashUtils.GetCRC(clip);
                    if (!bank.GetAnimationMetadata(crc, out int frames, out int bones)) continue;
                    if (frames <= 0) continue;

                    Console.WriteLine($"  HIT  {file}  bank '{bankName}'  clip '{clip}'  "
                                      + $"{frames} frame(s), {bones} bone(s)");
                }
            }
        }
    }
}
