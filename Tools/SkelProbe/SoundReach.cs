using System;
using System.Threading;
using LibSWBF2.Wrappers;
using LibSWBF2.Enums;
using LibSWBF2;

namespace SkelProbe
{
    /// <summary>
    /// Is sound data actually reachable the way the runtime asks for it?
    /// </summary>
    /// <remarks>
    /// An earlier check loaded sound levels with Level.FromFile and found 160
    /// sounds all reporting HasData false. That is the same mistake that hid
    /// map sub-LVLs: FromFile reads only the top level of an lvl. This asks
    /// through a Container, which is what SoundLoader uses, and compares the
    /// two routes the runtime could take - Container.Get&lt;Sound&gt; by hash,
    /// which is the only one it currently uses, against walking
    /// SoundBank.GetSounds, which the coverage audit says nothing calls.
    /// </remarks>
    static class SoundReach
    {
        public static void Run(string path, string label)
        {
            LibSWBF2.Logging.Logger.SetLogLevel(LibSWBF2.Logging.ELogType.Warning);
            Container con = new Container();
            SWBF2Handle h = con.AddLevel(path);
            con.LoadLevels();

            for (int i = 0; i < 600; ++i)
            {
                ELoadStatus s = con.GetStatus(h);
                if (s == ELoadStatus.Loaded || s == ELoadStatus.Failed) break;
                Thread.Sleep(50);
            }

            if (con.GetStatus(h) != ELoadStatus.Loaded)
            {
                Console.WriteLine($"=== {label}: not loaded ({con.GetStatus(h)})");
                return;
            }

            Level lvl = con.GetLevel(h, true);
            if (lvl == null)
            {
                Console.WriteLine($"=== {label}: no level");
                return;
            }

            SoundBank[] banks;
            Sound[] loose;
            try
            {
                banks = lvl.Get<SoundBank>();
                loose = lvl.Get<Sound>();
            }
            catch (Exception e)
            {
                Console.WriteLine($"=== {label}: {e.Message}");
                return;
            }

            Console.WriteLine($"=== {label}: {banks.Length} bank(s), {loose.Length} loose sound(s)");
            foreach (SoundBank b in banks)
            {
                Console.WriteLine($"    bank 0x{b.Name:x8}  HasData {b.HasData}  format {b.Format}");
            }

            int inBank = 0, withData = 0, aliased = 0, viaContainer = 0, decoded = 0;
            int rated = 0, sampled = 0, bytesy = 0;

            foreach (SoundBank b in banks)
            {
                long bankBytes = 0;
                Sound[] bs = null;
                try { bs = b.GetSounds(); } catch { }
                if (bs != null) foreach (Sound s2 in bs) if (s2 != null) bankBytes += s2.NumBytes;
                Console.WriteLine($"    bank 0x{b.Name:x8} clips {(bs == null ? 0 : bs.Length)} sample bytes {bankBytes:N0}");
            }

            foreach (SoundBank b in banks)
            {
                Sound[] sounds;
                try { sounds = b.GetSounds(); } catch { continue; }
                if (sounds == null) continue;

                foreach (Sound s in sounds)
                {
                    if (s == null) continue;
                    ++inBank;

                    if (s.HasData) ++withData;
                    else if (s.Alias != 0) ++aliased;

                    if (s.SampleRate > 0) ++rated;
                    if (s.NumSamples > 0) ++sampled;
                    if (s.NumBytes > 0) ++bytesy;

                    // The route SoundLoader actually takes.
                    if (con.Get<Sound>(s.Name) != null) ++viaContainer;

                    if (s.HasData)
                    {
                        try
                        {
                            short[] pcm = s.GetPCM16();
                            if (pcm != null && pcm.Length > 0) ++decoded;
                        }
                        catch { }
                    }
                }
            }

            Console.WriteLine($"    in banks {inBank}: HasData {withData}, alias {aliased}, decoded {decoded}");
            Console.WriteLine($"    headers: rate>0 {rated}, samples>0 {sampled}, bytes>0 {bytesy}");
            Console.WriteLine($"    reachable via Container.Get<Sound>(hash): {viaContainer}/{inBank}");

            int shown = 0;
            while (LibSWBF2.Logging.Logger.GetNextLog(out LibSWBF2.Logging.LoggerEntry entry))
            {
                if (shown++ < 25) Console.WriteLine($"    LOG {entry.Level}: {entry.Message}");
            }
            if (shown > 25) Console.WriteLine($"    ... {shown - 25} more log line(s)");
        }
    }
}
