using System;
using System.Collections.Generic;
using System.IO;
using LibSWBF2.Wrappers;
using LibSWBF2.Utils;

namespace SkelProbe
{
    /// <summary>
    /// Which crouch and prone clips actually exist, by brute-forcing the
    /// naming convention rather than guessing individual names.
    /// </summary>
    /// <remarks>
    /// This exists to settle one question that blocks implementing prone:
    /// PhxAnimationBanks lists no prone idle and no prone locomotion, and there
    /// are two possible reasons. Either the clips exist under names nobody has
    /// recovered - CRCs cannot be reversed, so an unrecovered name is
    /// indistinguishable from an absent clip - or prone was a one-way pose in
    /// the source game and the rest would have to be invented.
    ///
    /// A bank exposes CRCs only, but a CRC can be recomputed from a candidate
    /// and tested. So the cross-product of the naming convention already
    /// visible in PhxAnimationBanks - human_&lt;weapon&gt;_&lt;posture&gt;_&lt;action&gt; with an
    /// optional _full suffix - is generated and every combination asked for.
    /// Roughly ten thousand hashes, which is nothing, and it answers
    /// exhaustively instead of one name at a time.
    ///
    /// Set POSTURE_FILTER to narrow the report (default: crouch and prone).
    /// </remarks>
    static class PostureProbe
    {
        // The weapon banks PhxHumanAnimator already knows about, plus the ones
        // the stock odfs name in AnimationBank that it does not yet handle.
        static readonly string[] Weapons =
        {
            "rifle", "pistol", "bazooka", "tool", "sabre", "grenade",
            "melee", "dualpistol", "minigun", "sniper", "launcher",
        };

        static readonly string[] Postures =
        {
            "stand", "crouch", "prone", "squat", "crouched", "lay", "lying",
        };

        static readonly string[] Actions =
        {
            "idle", "idle_emote", "idle_emote_full", "idle_full",
            "alert", "alert_idle",
            "walkforward", "walkbackward", "walkleft", "walkright",
            "runforward", "runbackward", "runleft", "runright",
            "strafeleft", "straferight",
            "turnleft", "turnright",
            "shoot", "shoot_full", "shoot_secondary", "shoot_secondary_full",
            "reload", "reload_full",
            "hitfront", "hitback", "hitleft", "hitright",
            "death", "death_forward", "death_backward",
            "throw", "throw_grenade", "throw_full",
            "getup", "standup", "2stand", "2crouch", "2prone",
            "toss_lefthand", "toss", "idle_emote_alert", "alert_full",
        };

        // Transitions and one-offs, which do not follow posture_action shape.
        static readonly string[] Standalone =
        {
            "diveforward", "dive2prone", "prone2stand", "prone2crouch",
            "crouch2stand", "stand2crouch", "stand2prone", "crouch2prone",
            "prone_idle", "prone_walkforward", "prone_shoot",
            "roll", "rollleft", "rollright", "rollforward",
        };

        public static void Run(string path)
        {
            Level level;
            try { level = Level.FromFile(path); }
            catch (Exception e)
            {
                Console.WriteLine($"=== {Path.GetFileName(path)}: LOAD FAILED ({e.Message})");
                return;
            }
            if (level == null) return;

            AnimationBank[] banks;
            try { banks = level.Get<AnimationBank>(); } catch { return; }
            if (banks.Length == 0) return;

            string filter = Environment.GetEnvironmentVariable("POSTURE_FILTER");
            if (string.IsNullOrEmpty(filter)) filter = "crouch,prone";
            var wanted = new List<string>();
            foreach (string s in filter.Split(',')) wanted.Add(s.Trim().ToLowerInvariant());

            // Prefixes, not just "human". lookup.csv recovered
            // humanlz_rifle_prone_idle_emote - the prone clips live on the LZ
            // skeleton (14 joints against human_0's 31), which is why a probe
            // that only generated human_* found no prone at all and would have
            // concluded prone was never authored.
            string[] prefixes = { "human", "humanlz", "humanfp" };

            var candidates = new List<string>();
            foreach (string prefix in prefixes)
            foreach (string w in Weapons)
            {
                foreach (string p in Postures)
                {
                    foreach (string a in Actions)
                    {
                        candidates.Add(prefix + "_" + w + "_" + p + "_" + a);
                    }
                }
                foreach (string s in Standalone)
                {
                    candidates.Add(prefix + "_" + w + "_" + s);
                }
            }

            Console.WriteLine($"=== {Path.GetFileName(path)}: {banks.Length} bank(s), "
                              + $"{candidates.Count} candidate name(s)");

            var hits = new SortedDictionary<string, List<string>>();
            int totalAnims = 0;

            foreach (AnimationBank bank in banks)
            {
                uint[] all;
                try { all = bank.GetAnimationCRCs(); } catch { continue; }
                if (all == null) continue;
                totalAnims += all.Length;

                string bankName = "bank";

                foreach (string name in candidates)
                {
                    uint crc = HashUtils.GetCRC(name);
                    if (!bank.GetAnimationMetadata(crc, out int frames, out int bones)) continue;
                    if (frames <= 0) continue;

                    if (!hits.TryGetValue(name, out List<string> where))
                    {
                        where = new List<string>();
                        hits[name] = where;
                    }
                    where.Add($"{bankName}({frames}f)");
                }
            }

            Console.WriteLine($"    {totalAnims} animation(s) present, {hits.Count} name(s) recovered");

            foreach (var kv in hits)
            {
                bool interesting = wanted.Count == 0;
                foreach (string w in wanted)
                {
                    if (kv.Key.ToLowerInvariant().Contains(w)) { interesting = true; break; }
                }
                if (!interesting) continue;

                Console.WriteLine($"      {kv.Key,-48} {string.Join(" ", kv.Value)}");
            }

            // The count that decides the question: how much of each bank is
            // still unaccounted for. A bank whose clips are nearly all
            // recovered and still has no prone locomotion does not have any.
            Console.WriteLine($"    (unrecovered names remain if 'present' far exceeds what the "
                              + "full cross-product matched - see the total above)");
        }
    }
}
