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
    /// Dump every property of every entity class with a given base class.
    /// </summary>
    /// <remarks>
    /// The import validation shows three base classes with no runtime type -
    /// soundambiencestatic, rumbleeffect and dusteffect - across most stock
    /// maps. Implementing them means knowing what the odfs actually declare
    /// rather than what the class names suggest, so this lists the real
    /// properties and how often each one appears.
    ///
    /// Set CLASS_FILTER to a comma-separated list of base class names.
    /// </remarks>
    static class ClassProbe
    {
        // Property names are not enumerable through the wrapper, so ask for the
        // ones these classes plausibly carry and report which actually exist.
        static readonly string[] Candidates =
        {
            "GeometryName", "ClassLabel", "SoundName", "SoundAmbient", "Sound",
            "MinDistance", "MaxDistance", "Volume", "Pitch", "Loop", "Interval",
            "MinInterval", "MaxInterval", "InnerRadius", "OuterRadius", "Radius",
            "ScaleRange", "Rate", "Duration", "Magnitude", "Amplitude",
            "Frequency", "Falloff", "Range", "EffectName", "Effect", "Emitter",
            "ParticleEffect", "Texture", "Color", "Alpha", "Size", "Speed",
            "Lifetime", "SpawnDelay", "Attached", "AttachedTo", "OffsetPosition",
            "MaxParticles", "StartDelay", "FadeInTime", "FadeOutTime",
            "SoundStream", "StreamName", "SegmentName", "Priority", "Is3D",
            "ShakeRadius", "ShakeDuration", "ShakeMagnitude", "RumbleType",
        };

        public static void Run(string path)
        {
            string filter = Environment.GetEnvironmentVariable("CLASS_FILTER");
            if (string.IsNullOrEmpty(filter))
            {
                Console.WriteLine("set CLASS_FILTER=base1,base2");
                return;
            }

            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string s in filter.Split(',')) wanted.Add(s.Trim());

            Container con = new Container();
            SWBF2Handle h = con.AddLevel(path);

            // An instance only resolves its EntityClass if the lvl defining
            // that class is mounted too. Shared classes live in ingame.lvl, so
            // probing a map alone reports every one of them as unresolved -
            // the same mistake as reading a map with Level.FromFile.
            string extra = Environment.GetEnvironmentVariable("EXTRA_LVL");
            if (!string.IsNullOrEmpty(extra))
            {
                foreach (string e in extra.Split(';'))
                {
                    if (e.Length > 0 && File.Exists(e)) con.AddLevel(e);
                }
            }

            con.LoadLevels();

            for (int i = 0; i < 2400; ++i)
            {
                ELoadStatus s = con.GetStatus(h);
                if (s == ELoadStatus.Loaded || s == ELoadStatus.Failed) break;
                Thread.Sleep(50);
            }

            Level lvl = con.GetLevel(h, true);
            if (lvl == null)
            {
                Console.WriteLine($"=== {Path.GetFileName(path)}: not loaded");
                return;
            }

            EntityClass[] classes;
            try { classes = lvl.Get<EntityClass>(); }
            catch (Exception e) { Console.WriteLine($"=== {Path.GetFileName(path)}: {e.Message}"); return; }

            var matched = new List<EntityClass>();
            int hits = 0;
            foreach (EntityClass ec in classes)
            {
                if (ec == null || ec.BaseClassName == null) continue;
                if (!wanted.Contains(ec.BaseClassName)) continue;

                ++hits;
                Console.WriteLine($"    * {ec.Name}  (base {ec.BaseClassName})");
                matched.Add(ec);
            }

            Console.WriteLine($"=== {Path.GetFileName(path)}: {hits} matching class(es) of {classes.Length}");

            // The class odf is only half of it - what a placed instance
            // overrides is where the actual sound name, radius and so on live.
            // GetOverriddenProperties enumerates them by hash, so this reports
            // what is really there instead of guessing property names.
            var names = new Dictionary<uint, string>();

            string csv = "lookup.csv";
            if (File.Exists(csv))
            {
                foreach (string raw in File.ReadLines(csv))
                {
                    string line = raw.Trim();
                    if (line.Length == 0) continue;
                    uint fnv = LibSWBF2.Utils.HashUtils.GetFNV(line);
                    if (!names.ContainsKey(fnv)) names[fnv] = line;
                }
            }

            // Enumerate what the class really declares instead of probing for
            // names it might have - GetOverriddenProperties returns them all.
            foreach (EntityClass ec in matched)
            {
                ec.GetOverriddenProperties(out uint[] cp, out string[] cv);
                Console.WriteLine("    --- " + ec.Name + " full property set (" + cp.Length + ")");
                for (int i = 0; i < cp.Length && i < cv.Length; ++i)
                {
                    string pn = names.TryGetValue(cp[i], out string n2) ? n2 : "0x" + cp[i].ToString("x8");
                    Console.WriteLine("        " + pn.PadRight(24) + " = " + cv[i]);
                }
            }

            foreach (World w in lvl.Get<World>())
            {
                Instance[] insts;
                try { insts = w.GetInstances(); } catch { continue; }

                foreach (Instance inst in insts)
                {
                    if (inst == null) continue;

                    string cls = inst.EntityClass != null ? inst.EntityClass.BaseClassName : null;
                    if (cls == null || !wanted.Contains(cls)) continue;

                    Console.WriteLine($"    instance '{inst.Name}' of {inst.EntityClassName} in world '{w.Name}'");

                    inst.GetOverriddenProperties(out uint[] props, out string[] vals);
                    for (int i = 0; i < props.Length && i < vals.Length; ++i)
                    {
                        string pn = names.TryGetValue(props[i], out string n) ? n : $"0x{props[i]:x8}";
                        Console.WriteLine($"        {pn,-24} = {vals[i]}");
                    }
                }
            }
        }
    }
}
