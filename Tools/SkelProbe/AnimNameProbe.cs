using System;
using System.Collections.Generic;
using LibSWBF2.Wrappers;
using LibSWBF2.Utils;

namespace SkelProbe
{
    /// <summary>
    /// Which animation names actually exist in a level's banks.
    /// </summary>
    /// <remarks>
    /// AnimationBank exposes CRCs, not names, and a CRC cannot be reversed. But
    /// it can be recomputed: hash a candidate and ask the bank whether it has
    /// that animation. That turns "what are the walker's clips called" from a
    /// guess into a lookup, which matters because PhxWalkerLocomotion picks its
    /// walk cycle by trying a hardcoded list of plausible names.
    /// </remarks>
    static class AnimNameProbe
    {
        static readonly string[] Candidates =
        {
            // what PhxWalkerLocomotion currently tries
            "walk", "walkforward", "forward", "move", "run",
            "idle", "stand", "rest",

            // BF2's actual vehicle animation vocabulary
            "walkforward1", "walkforward2", "walk_forward", "walkfwd",
            "atat_walk", "atst_walk", "walkloop", "loop",
            "turnleft", "turnright", "walkbackward",
            "deathleft", "deathright", "death",
            "enter", "exit", "openhatch", "closehatch",
            "fire", "reload", "aim",

            // droideka: the transform between rolling and deployed is the
            // whole unit, and it is animation-driven
            "roll", "rollforward", "unroll", "deploy", "undeploy",
            "transform", "stand_idle", "roll_idle", "shield",
        };

        public static void Run(Level level, string label)
        {
            AnimationBank[] banks = level.Get<AnimationBank>();
            if (banks.Length == 0) return;

            Console.WriteLine($"--- {label}: probing {Candidates.Length} candidate names against {banks.Length} bank(s)");

            var hits = new SortedDictionary<string, int>();
            int totalAnims = 0;

            foreach (AnimationBank bank in banks)
            {
                uint[] all;
                try { all = bank.GetAnimationCRCs(); } catch { continue; }
                if (all == null) continue;
                totalAnims += all.Length;

                foreach (string name in Candidates)
                {
                    uint crc = HashUtils.GetCRC(name);
                    if (bank.GetAnimationMetadata(crc, out int frames, out int bones) && frames > 0)
                    {
                        hits.TryGetValue(name, out int n);
                        hits[name] = n + 1;
                    }
                }
            }

            Console.WriteLine($"    {totalAnims} animation(s) present; {hits.Count} candidate name(s) matched");
            foreach (var kv in hits)
            {
                Console.WriteLine($"      HIT  {kv.Key}  (in {kv.Value} bank(s))");
            }
            if (hits.Count == 0)
            {
                Console.WriteLine("      no candidate matched - the names this project guesses are wrong");
            }
        }
    }
}
