using System;
using System.Collections.Generic;
using System.IO;
using LibSWBF2.Wrappers;
using LibSWBF2.Utils;

namespace SkelProbe
{
    /// <summary>
    /// Recover animation names from their CRCs using lookup.csv as a dictionary.
    /// </summary>
    /// <remarks>
    /// AnimationBank exposes animation CRCs and a CRC cannot be inverted, so
    /// "what is this clip called" normally has no answer. But LibSWBF2 ships
    /// lookup.csv - tens of thousands of strings harvested from the game - for
    /// exactly this sort of name recovery. Hash every line with the same CRC
    /// the banks use and the mapping falls out.
    ///
    /// This matters because PhxWalkerLocomotion picks its walk cycle by trying
    /// a hardcoded list of guesses, and a probe showed that across 188
    /// animations in imp.lvl only "idle" ever matched - so walkers never found
    /// a walk clip and never animated their legs.
    /// </remarks>
    static class CrcNames
    {
        static Dictionary<uint, string> Table;

        static void Load(string csvPath)
        {
            Table = new Dictionary<uint, string>();
            if (!File.Exists(csvPath))
            {
                Console.WriteLine($"    (no lookup.csv at {csvPath}; cannot resolve names)");
                return;
            }

            foreach (string raw in File.ReadLines(csvPath))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;

                uint crc = HashUtils.GetCRC(line);
                if (!Table.ContainsKey(crc)) Table[crc] = line;
            }
            Console.WriteLine($"    lookup.csv: {Table.Count} name(s) available for CRC recovery");
        }

        public static void Run(Level level, string csvPath, int maxBanks)
        {
            if (Table == null) Load(csvPath);

            AnimationBank[] banks = level.Get<AnimationBank>();
            int probed = 0;

            foreach (AnimationBank bank in banks)
            {
                if (probed++ >= maxBanks) break;

                uint[] anims;
                try { anims = bank.GetAnimationCRCs(); } catch { continue; }
                if (anims == null || anims.Length == 0) continue;

                var named = new List<string>();
                int unknown = 0;
                foreach (uint crc in anims)
                {
                    if (Table.TryGetValue(crc, out string name)) named.Add(name);
                    else ++unknown;
                }

                Console.WriteLine($"    bank #{probed}: {anims.Length} animation(s), " +
                                  $"{named.Count} named, {unknown} unresolved");
                named.Sort();
                foreach (string n in named) Console.WriteLine($"        {n}");
            }
        }
    }
}
