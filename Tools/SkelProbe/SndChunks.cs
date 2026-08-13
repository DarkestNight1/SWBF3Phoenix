using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SkelProbe
{
    /// <summary>
    /// Walk the raw chunk tree of a sound lvl.
    /// </summary>
    /// <remarks>
    /// SoundBank.HasData comes back false for every bank while every clip header
    /// carries a nonzero data length, and SampleBankData logs nothing - so the
    /// reader never reaches a Data chunk. This dumps what is actually in the
    /// file so the answer comes from the bytes rather than from reading the
    /// parser and guessing.
    ///
    /// Chunk tags are 4 bytes followed by a 4-byte size. Most are printable
    /// ASCII ("ucfb", "Info", "Data"); the rest are FNV hashes of longer names,
    /// which are resolved against a small table of the names this format uses.
    /// </remarks>
    static class SndChunks
    {
        static readonly string[] KnownTags =
        {
            "SampleBank", "SampleBankInfo", "SampleBankData", "StreamBank",
            "Info", "Data", "ucfb", "lvl_", "SoundBankList", "sanm", "Stream",
            "SoundBank", "StreamBankList", "SampleBankList", "Sample", "Bank",
            "SoundStream", "StreamData", "StreamInfo", "SoundData", "emo_",
            "StreamList", "_pad", "SampleBankList", "Segment", "SegmentInfo",
        };

        static Dictionary<uint, string> HashToName;

        static uint Fnv(string s)
        {
            uint h = 0x811c9dc5u;
            foreach (char c in s)
            {
                h ^= (uint)(c | 0x20);
                h *= 0x01000193u;
            }
            return h;
        }

        static void BuildTable()
        {
            HashToName = new Dictionary<uint, string>();
            foreach (string t in KnownTags)
            {
                uint h = Fnv(t);
                if (!HashToName.ContainsKey(h)) HashToName[h] = t;
            }
        }

        static bool PrintableTag(byte[] d, int at, out string tag)
        {
            tag = null;
            if (at + 4 > d.Length) return false;
            for (int i = 0; i < 4; ++i)
            {
                byte b = d[at + i];
                bool ok = (b >= 'A' && b <= 'Z') || (b >= 'a' && b <= 'z') ||
                          (b >= '0' && b <= '9') || b == '_';
                if (!ok) return false;
            }
            tag = Encoding.ASCII.GetString(d, at, 4);
            return true;
        }

        static string TagName(byte[] d, int at)
        {
            if (PrintableTag(d, at, out string ascii)) return ascii;
            uint h = BitConverter.ToUInt32(d, at);
            if (HashToName.TryGetValue(h, out string known)) return known + " (fnv)";
            return null;
        }

        static void Walk(byte[] d, int start, int end, int depth, Dictionary<string, long> tally)
        {
            int cursor = start;
            while (cursor + 8 <= end)
            {
                string tag = TagName(d, cursor);
                if (tag == null) { ++cursor; continue; }

                int size = BitConverter.ToInt32(d, cursor + 4);
                if (size < 0 || size > end - cursor - 8) { ++cursor; continue; }

                tally.TryGetValue(tag, out long bytes);
                tally[tag] = bytes + size;

                if (depth < 3)
                {
                    Console.WriteLine($"    {new string(' ', depth * 2)}{tag,-18} {size,12:N0}");
                }

                // Containers worth descending into.
                bool container = tag == "ucfb" || tag == "lvl_" ||
                                 tag.StartsWith("SampleBank") || tag.StartsWith("StreamBank") ||
                                 tag.StartsWith("SoundBankList");

                if (container && depth < 4)
                {
                    int inner = cursor + 8;
                    if (tag == "lvl_") inner += 8;   // name hash + size
                    Walk(d, inner, cursor + 8 + size, depth + 1, tally);
                }

                cursor = ((cursor + 8 + size) + 3) & ~3;
            }
        }

        public static void Run(string path)
        {
            if (HashToName == null) BuildTable();

            if (Environment.GetEnvironmentVariable("SND_TAGS") != null)
            {
                foreach (string t in KnownTags) Console.WriteLine($"    fnv {t,-16} = 0x{Fnv(t):x8}");
            }

            byte[] d;
            try { d = File.ReadAllBytes(path); }
            catch (Exception e)
            {
                Console.WriteLine($"=== {Path.GetFileName(path)}: {e.Message}");
                return;
            }

            Console.WriteLine($"=== {Path.GetFileName(path)}  ({d.Length / (1024 * 1024)} MB)");

            var tally = new Dictionary<string, long>();
            int end = Math.Min(d.Length, 8 + BitConverter.ToInt32(d, 4));
            Walk(d, 8, end, 0, tally);

            Console.WriteLine("    --- totals by tag ---");
            var keys = new List<string>(tally.Keys);
            keys.Sort((a, b) => tally[b].CompareTo(tally[a]));
            foreach (string k in keys)
            {
                Console.WriteLine($"    {k,-20} {tally[k],14:N0} bytes");
            }
        }
    }
}
