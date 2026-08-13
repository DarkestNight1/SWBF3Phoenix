using System;
using System.Linq;
using LibSWBF2.Wrappers;

namespace SkelProbe
{
    /// <summary>
    /// Does a mounted level actually yield playable audio?
    /// </summary>
    /// <remarks>
    /// PhxMusicManager received every audio call the era scripts make and
    /// played nothing, so it was worth proving the data path end to end -
    /// bank -> Sound -> PCM - before assuming the fault was only in the wiring.
    /// </remarks>
    static class SoundCheck
    {
        public static void Run(Level level, string label)
        {
            SoundBank[] banks = level.Get<SoundBank>();
            Sound[] loose = level.Get<Sound>();
            Console.WriteLine($"=== {label}: {banks.Length} bank(s), {loose.Length} loose sound(s)");

            int total = 0, decoded = 0, aliased = 0, empty = 0;
            long samples = 0;

            foreach (SoundBank bank in banks.Take(4))
            {
                Sound[] sounds;
                try { sounds = bank.GetSounds(); } catch { continue; }
                if (sounds == null) continue;

                foreach (Sound s in sounds.Take(40))
                {
                    ++total;
                    if (!s.HasData) { if (s.Alias != 0) ++aliased; else ++empty; continue; }

                    short[] pcm;
                    try { pcm = s.GetPCM16(); } catch { continue; }
                    if (pcm != null && pcm.Length > 0)
                    {
                        ++decoded;
                        samples += pcm.Length;
                    }
                }
            }

            Console.WriteLine($"    probed {total}: decoded {decoded}, alias {aliased}, no data {empty}, " +
                              $"{samples / 1000}k samples");
        }
    }
}
