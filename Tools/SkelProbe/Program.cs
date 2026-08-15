using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LibSWBF2.Wrappers;
using LibSWBF2.Utils;

namespace SkelProbe
{
    /// <summary>
    /// Reports what AnimationSkeleton actually contains, and how often an
    /// AnimationBank curve is missing a channel.
    ///
    /// PhxAnimationLoader reads seven curve components per bone (rot xyzw,
    /// pos xyz) and drops any component LibSWBF2 refuses to hand over. Cra
    /// then bakes the result, and an empty CraCurve is not survivable there:
    /// GetEstimatedFrameCount indexes EditKeys[-1], and Bake returns leaving
    /// BakedFrames null for the caller to dereference. So "how many channels
    /// are missing" is the difference between an animation system that works
    /// and one that throws during import.
    ///
    /// AnimationSkeleton is what fills those gaps - a joint's BaseRotation and
    /// BasePosition are the authored rest value for exactly the channels the
    /// animation chose not to key.
    ///
    /// Usage: SkelProbe &lt;lvl-or-directory&gt; [more...] [--limit N]
    /// </summary>
    static class Program
    {
        static string OdfFilter;
        static bool ResolveNames;
        static bool DestructMode;
        static bool SubLvlMode;
        static bool SoundMode;
        static bool SndChunkMode;
        static bool PairMode;
        static bool StreamMode;
        static bool WorldAnimMode;
        static bool ClassMode;
        static bool MatFlagMode;
        static bool CollisionMode;
        static bool PostureMode;
        static bool TerrainMode;
        static bool WaterMode;
        static bool CensusMode;
        static bool MissingMode;
        static bool HashMode;
        static string LookupCsv = "lookup.csv";

        static int Main(string[] args)
        {
            var files = new List<string>();
            int limit = 6;
            OdfFilter = null;
            DestructMode = false;
            SubLvlMode = false;
            SoundMode = false;
            SndChunkMode = false;
            PairMode = false;
            StreamMode = false;
            WorldAnimMode = false;
            ClassMode = false;
            MatFlagMode = false;
            CollisionMode = false;
            PostureMode = false;
            TerrainMode = false;
            WaterMode = false;
            CensusMode = false;
            MissingMode = false;
            HashMode = false;
            ResolveNames = false;

            for (int i = 0; i < args.Length; ++i)
            {
                if (args[i] == "--hash") { HashMode = true; }
                else if (args[i] == "--missing") { MissingMode = true; }
                else if (args[i] == "--census") { CensusMode = true; }
                else if (args[i] == "--water") { WaterMode = true; }
                else if (args[i] == "--terrain") { TerrainMode = true; }
                else if (args[i] == "--posture") { PostureMode = true; }
                else                 if (args[i] == "--collision") { CollisionMode = true; }
                else if (args[i] == "--matflags") { MatFlagMode = true; }
                else if (args[i] == "--classprops") { ClassMode = true; }
                else if (args[i] == "--worldanim") { WorldAnimMode = true; }
                else if (args[i] == "--streams") { StreamMode = true; }
                else if (args[i] == "--soundpair") { PairMode = true; }
                else if (args[i] == "--sndchunks") { SndChunkMode = true; }
                else if (args[i] == "--soundreach") { SoundMode = true; }
                else if (args[i] == "--sublvl") { SubLvlMode = true; }
                else if (args[i] == "--destruct") { DestructMode = true; }
                else if (args[i] == "--names") { ResolveNames = true; }
                else if (args[i] == "--csv" && i + 1 < args.Length) { LookupCsv = args[++i]; }
                else if (args[i] == "--odf" && i + 1 < args.Length) { OdfFilter = args[++i].ToLowerInvariant(); }
                else if (args[i] == "--limit" && i + 1 < args.Length)
                {
                    limit = int.Parse(args[++i]);
                }
                else if (Directory.Exists(args[i]))
                {
                    files.AddRange(Directory.GetFiles(args[i], "*.lvl", SearchOption.TopDirectoryOnly));
                }
                else if (File.Exists(args[i]))
                {
                    files.Add(args[i]);
                }
                else
                {
                    Console.Error.WriteLine($"skip (not found): {args[i]}");
                }
            }

            if (HashMode)
            {
                var names = new List<string>();
                for (int i = 0; i < args.Length; ++i) if (args[i] != "--hash") names.Add(args[i]);
                HashProbe.Run(names.ToArray());
                return 0;
            }

            if (files.Count == 0)
            {
                Console.Error.WriteLine("usage: SkelProbe <lvl file or directory> [...] [--limit N]");
                return 2;
            }

            if (PairMode) { SoundPair.Run(files.ToArray()); return 0; }

            foreach (string path in files)
            {
                if (MissingMode) { MissingProbe.Run(path); continue; }
                if (CensusMode) { CensusProbe.Run(path); continue; }
                if (WaterMode) { WaterProbe.Run(path); continue; }
                if (TerrainMode) { TerrainProbe.Run(path); continue; }
                if (PostureMode) { PostureProbe.Run(path); continue; }
                if (CollisionMode) { CollisionProbe.Run(path); continue; }
                if (MatFlagMode) { MatFlagProbe.Run(path); continue; }
                if (ClassMode) { ClassProbe.Run(path); continue; }
                if (WorldAnimMode) { WorldAnimProbe.Run(path); continue; }
                if (StreamMode) { StreamProbe.Run(path, LookupCsv); continue; }
                if (SndChunkMode) { SndChunks.Run(path); continue; }
                if (SoundMode) { SoundReach.Run(path, Path.GetFileName(path)); continue; }
                if (SubLvlMode) { SubLvlProbe.Run(path, LookupCsv); continue; }
                if (DestructMode) { DestructProbe.Run(path, Path.GetFileName(path)); continue; }
                Level level;
                try
                {
                    level = Level.FromFile(path);
                }
                catch (Exception e)
                {
                    Console.WriteLine($"=== {Path.GetFileName(path)} : LOAD FAILED ({e.Message})");
                    continue;
                }
                if (level == null)
                {
                    Console.WriteLine($"=== {Path.GetFileName(path)} : LOAD RETURNED NULL");
                    continue;
                }

                AnimationSkeleton[] skels = level.Get<AnimationSkeleton>();
                AnimationBank[] banks = level.Get<AnimationBank>();

                if (skels.Length == 0 && banks.Length == 0) { SoundCheck.Run(level, Path.GetFileName(path)); continue; }

                Console.WriteLine($"=== {Path.GetFileName(path)} : {skels.Length} skeleton(s), {banks.Length} bank(s)");

                // Every joint CRC we can resolve to a rest pose, across all
                // skeletons in this level. The animation loader needs exactly
                // this lookup, so build it the same way here.
                var jointsByCRC = new Dictionary<uint, Joint>();
                foreach (AnimationSkeleton skel in skels)
                {
                    Joint[] joints = skel.GetJoints();
                    Console.WriteLine($"  skeleton '{skel.GetName()}' : {joints.Length} joint(s)");
                    foreach (Joint j in joints) jointsByCRC[j.BoneCRC] = j;
                }
                if (skels.Length > limit)
                {
                    Console.WriteLine($"  ... ({skels.Length} skeletons total)");
                }

                SoundCheck.Run(level, Path.GetFileName(path));
                AnimNameProbe.Run(level, Path.GetFileName(path));
                if (OdfFilter != null) OdfProbe.Run(level, OdfFilter);
                if (ResolveNames) CrcNames.Run(level, LookupCsv, limit);
                ProbeBanks(banks, jointsByCRC, limit);
                CompareToModelSkeletons(level, jointsByCRC, limit);
                Console.WriteLine();
            }

            if (MissingMode) MissingProbe.Summary();
            return 0;
        }

        /// <summary>
        /// ModelLoader.AddSkeleton already gives every bone a rest transform,
        /// taken from the model's own Bone.Rotation/Location. AnimationSkeleton
        /// carries a second one. If the two agree, the animation skeleton adds
        /// nothing and unkeyed joints are already correct; if they disagree,
        /// keyed bones move in one space while unkeyed bones sit in another,
        /// which is what a deformation artefact looks like from the inside.
        /// </summary>
        static void CompareToModelSkeletons(Level level, Dictionary<uint, Joint> jointsByCRC, int limit)
        {
            if (jointsByCRC.Count == 0) return;

            Model[] models;
            try { models = level.Get<Model>(); }
            catch { return; }
            if (models.Length == 0) return;

            int compared = 0, posMismatch = 0, rotMismatch = 0, parentMismatch = 0;
            double worstPos = 0, worstRot = 0;
            string worstPosBone = null, worstRotBone = null;
            int modelsProbed = 0;

            foreach (Model model in models)
            {
                if (modelsProbed >= limit) break;
                var skel = model.Skeleton;
                if (skel == null || skel.Count == 0) continue;

                bool anyMatched = false;
                foreach (var bone in skel)
                {
                    uint crc = HashUtils.GetCRC(bone.Name);
                    if (!jointsByCRC.TryGetValue(crc, out Joint joint)) continue;
                    anyMatched = true;
                    compared++;

                    double dp = Dist(bone.Location, joint.BasePosition);
                    if (dp > worstPos) { worstPos = dp; worstPosBone = $"{model.Name}/{bone.Name}"; }
                    if (dp > 1e-3) posMismatch++;

                    double dr = QuatDist(bone.Rotation, joint.BaseRotation);
                    if (dr > worstRot) { worstRot = dr; worstRotBone = $"{model.Name}/{bone.Name}"; }
                    if (dr > 1e-3) rotMismatch++;

                    uint modelParent = string.IsNullOrEmpty(bone.ParentName) ? 0 : HashUtils.GetCRC(bone.ParentName);
                    if (modelParent != joint.ParentBoneCRC) parentMismatch++;
                }
                if (anyMatched) modelsProbed++;
            }

            if (compared == 0)
            {
                Console.WriteLine("  no model bone matched a skeleton joint CRC");
                return;
            }

            Console.WriteLine($"  model-vs-animskeleton bones compared: {compared} (across {modelsProbed} model(s))");
            Console.WriteLine($"    position differs (>1e-3): {posMismatch}   worst {worstPos:F5} @ {worstPosBone}");
            Console.WriteLine($"    rotation differs (>1e-3): {rotMismatch}   worst {worstRot:F5} @ {worstRotBone}");
            Console.WriteLine($"    parent CRC differs      : {parentMismatch}");
        }

        static double Dist(LibSWBF2.Types.Vector3 a, LibSWBF2.Types.Vector3 b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        static double QuatDist(LibSWBF2.Types.Vector4 a, LibSWBF2.Types.Vector4 b)
        {
            // Sign-insensitive: q and -q are the same rotation.
            double dot = a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;
            return 1.0 - Math.Abs(dot);
        }

        static void ProbeBanks(AnimationBank[] banks, Dictionary<uint, Joint> jointsByCRC, int limit)
        {
            long totalBones = 0;
            long bonesMissingAny = 0;
            long channelsMissing = 0;
            long channelsEmpty = 0;
            long bonesResolvable = 0;
            long bonesUnresolvable = 0;
            var missingByComponent = new long[7];
            var unresolvedExamples = new List<uint>();

            int banksProbed = 0;
            foreach (AnimationBank bank in banks)
            {
                if (banksProbed++ >= limit) break;

                uint[] animCRCs;
                try { animCRCs = bank.GetAnimationCRCs(); }
                catch (Exception e) { Console.WriteLine($"  bank: GetAnimationCRCs threw ({e.Message})"); continue; }

                foreach (uint anim in animCRCs)
                {
                    if (!bank.GetAnimationMetadata(anim, out int numFrames, out int numBones)) continue;

                    uint[] boneCRCs;
                    try { boneCRCs = bank.GetBoneCRCs(anim); }
                    catch { continue; }

                    foreach (uint bone in boneCRCs)
                    {
                        totalBones++;

                        if (jointsByCRC.ContainsKey(bone)) bonesResolvable++;
                        else
                        {
                            bonesUnresolvable++;
                            if (unresolvedExamples.Count < 8) unresolvedExamples.Add(bone);
                        }

                        bool anyMissing = false;
                        for (uint comp = 0; comp < 7; ++comp)
                        {
                            if (!bank.GetCurve(anim, bone, comp, out ushort[] indices, out float[] values))
                            {
                                channelsMissing++;
                                missingByComponent[comp]++;
                                anyMissing = true;
                            }
                            else if (indices == null || indices.Length == 0)
                            {
                                // Returned true but handed back nothing - just
                                // as fatal to Cra as an outright failure.
                                channelsEmpty++;
                                missingByComponent[comp]++;
                                anyMissing = true;
                            }
                        }
                        if (anyMissing) bonesMissingAny++;
                    }
                }
            }

            if (totalBones == 0)
            {
                Console.WriteLine("  no animated bones probed");
                return;
            }

            // The channels are all there, so the interesting gap is the other
            // direction: joints the skeleton declares that a given animation
            // never keys. Those bones hold whatever pose the model import left
            // them in, and the skeleton is the only authored statement of what
            // that pose should be.
            if (jointsByCRC.Count > 0)
            {
                int animsProbed = 0, animsPartial = 0;
                long jointsUnkeyed = 0, jointsTotal = 0;
                foreach (AnimationBank bank in banks.Take(limit))
                {
                    uint[] animCRCs;
                    try { animCRCs = bank.GetAnimationCRCs(); } catch { continue; }
                    foreach (uint anim in animCRCs)
                    {
                        if (!bank.GetAnimationMetadata(anim, out _, out _)) continue;
                        uint[] boneCRCs;
                        try { boneCRCs = bank.GetBoneCRCs(anim); } catch { continue; }

                        var keyed = new HashSet<uint>(boneCRCs);
                        int unkeyed = jointsByCRC.Keys.Count(c => !keyed.Contains(c));

                        animsProbed++;
                        jointsTotal += jointsByCRC.Count;
                        jointsUnkeyed += unkeyed;
                        if (unkeyed > 0) animsPartial++;
                    }
                }
                if (animsProbed > 0)
                {
                    Console.WriteLine($"  animations probed     : {animsProbed}, leaving >=1 joint unkeyed: {animsPartial}");
                    Console.WriteLine($"  skeleton joints unkeyed by their animation: {jointsUnkeyed} / {jointsTotal}  ({100.0 * jointsUnkeyed / Math.Max(1, jointsTotal):F1}%)");
                }
            }

            string[] names = { "rot.x", "rot.y", "rot.z", "rot.w", "pos.x", "pos.y", "pos.z" };
            Console.WriteLine($"  bones probed          : {totalBones}");
            Console.WriteLine($"  bones missing >=1 chan: {bonesMissingAny}  ({100.0 * bonesMissingAny / totalBones:F1}%)");
            Console.WriteLine($"  channels GetCurve=false: {channelsMissing}");
            Console.WriteLine($"  channels empty (true, 0 keys): {channelsEmpty}");
            Console.WriteLine($"  bone CRC in a skeleton : {bonesResolvable}  / not found: {bonesUnresolvable}");
            Console.Write("  missing by component  :");
            for (int i = 0; i < 7; ++i) Console.Write($" {names[i]}={missingByComponent[i]}");
            Console.WriteLine();
            if (unresolvedExamples.Count > 0)
            {
                Console.WriteLine($"  unresolved CRC examples: {string.Join(", ", unresolvedExamples)}");
            }
        }
    }
}
