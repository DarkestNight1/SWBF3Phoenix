using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using LibSWBF2.Utils;

namespace SkelProbe
{
    /// <summary>
    /// List the sub-LVLs inside an lvl file, and what each one holds.
    /// </summary>
    /// <remarks>
    /// A BF2 map lvl is a container of named sub-LVLs, and the mission script
    /// decides which of them to mount with ReadDataFile("map.lvl", "sub1",
    /// "sub2"). Anything not named is never loaded - so "missing content"
    /// usually means a sub-LVL nobody asked for rather than data that failed to
    /// parse.
    ///
    /// LibSWBF2 exposes no way to enumerate them (only Container_AddLevelFiltered,
    /// which takes names as input), so this walks the chunk tree directly. An
    /// "lvl_" chunk carries a 4-byte name hash followed by a 4-byte size, then
    /// its children.
    ///
    /// Names are recovered by hashing lookup.csv, the same trick that recovered
    /// the walker animation names.
    /// </remarks>
    static class SubLvlProbe
    {
        static Dictionary<uint, string> FnvNames;
        static Dictionary<uint, string> CrcNames;

        static void LoadNames(string csvPath)
        {
            FnvNames = new Dictionary<uint, string>();
            CrcNames = new Dictionary<uint, string>();
            if (!File.Exists(csvPath)) return;

            foreach (string raw in File.ReadLines(csvPath))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;

                uint f = HashUtils.GetFNV(line);
                if (!FnvNames.ContainsKey(f)) FnvNames[f] = line;

                uint c = HashUtils.GetCRC(line);
                if (!CrcNames.ContainsKey(c)) CrcNames[c] = line;
            }
        }

        static string Resolve(uint hash)
        {
            if (FnvNames.TryGetValue(hash, out string f)) return f;
            if (CrcNames.TryGetValue(hash, out string c)) return c + " (crc)";
            return null;
        }

        static string FourCC(byte[] d, int at)
        {
            if (at < 0 || at + 4 > d.Length) return null;
            return Encoding.ASCII.GetString(d, at, 4);
        }

        static bool Plausible(string s)
        {
            if (s == null || s.Length != 4) return false;
            foreach (char ch in s)
            {
                bool ok = (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z') ||
                          (ch >= '0' && ch <= '9') || ch == '_';
                if (!ok) return false;
            }
            return true;
        }

        public static void Run(string path, string csvPath)
        {
            if (FnvNames == null) LoadNames(csvPath);

            byte[] data;
            try { data = File.ReadAllBytes(path); }
            catch (Exception e)
            {
                Console.WriteLine($"=== {Path.GetFileName(path)}: unreadable ({e.Message})");
                return;
            }

            Console.WriteLine($"=== {Path.GetFileName(path)}  ({data.Length / (1024 * 1024)} MB)");

            // Top level is "ucfb"; its children include the lvl_ sub-levels.
            string root = FourCC(data, 0);
            if (root != "ucfb")
            {
                Console.WriteLine($"    not a ucfb container (starts '{root}')");
                return;
            }

            int end = Math.Min(data.Length, 8 + BitConverter.ToInt32(data, 4));
            int cursor = 8;
            int subs = 0;
            long subBytes = 0;

            while (cursor + 8 <= end)
            {
                string name = FourCC(data, cursor);
                if (!Plausible(name)) break;

                int size = BitConverter.ToInt32(data, cursor + 4);
                if (size < 0 || size > end - cursor - 8) break;

                if (name == "lvl_" && size >= 8)
                {
                    uint nameHash = BitConverter.ToUInt32(data, cursor + 8);
                    string resolved = Resolve(nameHash);

                    ++subs;
                    subBytes += size;

                    Console.WriteLine($"    lvl_  {size,10:N0} bytes   {(resolved ?? $"<unresolved 0x{nameHash:x8}>")}");
                }

                cursor = ((cursor + 8 + size) + 3) & ~3;
            }

            if (subs == 0)
            {
                Console.WriteLine("    no sub-LVLs - content sits at the top level");
            }
            else
            {
                Console.WriteLine($"    {subs} sub-LVL(s), {subBytes / (1024 * 1024)} MB of {data.Length / (1024 * 1024)} MB");
            }
        }
    }
}
