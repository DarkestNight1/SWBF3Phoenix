using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

/// <summary>
/// Walks the ucfb container in SWBF2 .lvl files and tallies every chunk type
/// present, so "what does the data contain that we never read?" becomes a
/// measured list instead of an assumption.
/// </summary>
/// <remarks>
/// The container is trivially self-describing: a 4-byte FourCC, a 4-byte
/// little-endian payload size, then the payload, padded to a 4-byte boundary.
/// Some chunks are containers of the same shape and some are leaf data, and
/// there is no flag saying which - so the walker descends speculatively and
/// backs out when the children do not tile the parent exactly. That
/// heuristic is why a chunk can be reported as a leaf here while really
/// holding a structure LibSWBF2 knows how to read; it never reports a chunk
/// that is not there.
/// </remarks>
internal static class Program
{
    sealed class Tally
    {
        public long Count;
        public long Bytes;
        public readonly HashSet<string> Parents = new HashSet<string>();
        public readonly HashSet<string> Files = new HashSet<string>();
    }

    static readonly Dictionary<string, Tally> Chunks = new Dictionary<string, Tally>(StringComparer.Ordinal);

    static string Mode = "survey";
    static string Subject;
    static int DumpsRemaining = 4;

    /// <summary>API names already implemented, supplied by --known.</summary>
    static readonly HashSet<string> Known = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Identifier -> how many stock scripts reference it.</summary>
    static readonly Dictionary<string, int> LuaRefs = new Dictionary<string, int>(StringComparer.Ordinal);

    static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("usage: ChunkAudit <lvl-file-or-directory> [more...]");
            return 2;
        }

        // Survey mode is the default; the other two modes exist to answer
        // "what is actually inside this chunk?" once the survey has named it.
        string mode = "survey";
        string subject = null;
        var rest = new List<string>();
        for (int i = 0; i < args.Length; ++i)
        {
            if (args[i] == "--names" && i + 1 < args.Length) { mode = "names"; subject = args[++i]; }
            else if (args[i] == "--dump" && i + 1 < args.Length) { mode = "dump"; subject = args[++i]; }
            else if (args[i] == "--lua") { mode = "lua"; }
            else if (args[i] == "--fields" && i + 1 < args.Length) { mode = "fields"; subject = args[++i]; }
            else if (args[i] == "--known" && i + 1 < args.Length) { Known.UnionWith(File.ReadAllLines(args[++i]).Select(s => s.Trim())); }
            else rest.Add(args[i]);
        }
        Mode = mode;
        Subject = subject;
        args = rest.ToArray();

        var files = new List<string>();
        foreach (string arg in args)
        {
            if (Directory.Exists(arg))
            {
                files.AddRange(Directory.EnumerateFiles(arg, "*.lvl", SearchOption.AllDirectories));
            }
            else if (File.Exists(arg))
            {
                files.Add(arg);
            }
        }

        if (files.Count == 0)
        {
            Console.Error.WriteLine("no .lvl files found");
            return 2;
        }

        long totalBytes = 0;
        foreach (string file in files)
        {
            try
            {
                byte[] data = File.ReadAllBytes(file);
                totalBytes += data.LongLength;
                Walk(data, 0, data.Length, Path.GetFileName(file), "<root>", 0);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"  ! {Path.GetFileName(file)}: {e.Message}");
            }
        }

        if (Mode == "survey") Report(files.Count, totalBytes);
        else if (Mode == "lua") ReportLua();
        else if (Mode == "fields") ConfigFields.Report();
        return 0;
    }

    /// <summary>Descend a region, treating it as a sequence of chunks.</summary>
    static void Walk(byte[] data, int start, int end, string file, string parent, int depth, bool inSubject = false)
    {
        // Depth guard: a misidentified leaf can look like an endless nest of
        // tiny chunks, and the point of this tool is a survey, not a proof.
        if (depth > 12) return;

        int cursor = start;
        while (cursor + 8 <= end)
        {
            string name = ReadFourCC(data, cursor);
            if (!IsPlausibleFourCC(name)) return;

            int size = BitConverter.ToInt32(data, cursor + 4);
            if (size < 0 || cursor + 8 + size > end) return;

            Record(name, size, file, parent);

            int payload = cursor + 8;

            // "NAME under modl" is how the LOD naming convention gets
            // confirmed: the survey says a chunk exists, this says what it
            // holds, and only the second one can tell you whether the mesh
            // called foo has a sibling called foo_lod1.
            if (Mode == "names" && name == "NAME" && parent == Subject)
            {
                Console.WriteLine($"{file,-16} {ReadString(data, payload, size)}");
            }
            else if (Mode == "lua" && name == "BODY" && parent == "scr_")
            {
                HarvestLuaIdentifiers(data, payload, size);
            }
            else if (Mode == "fields" && name == "DATA" && inSubject)
            {
                ConfigFields.Record(data, payload, size, parent);
            }
            else if (Mode == "dump" && name == Subject && DumpsRemaining > 0)
            {
                --DumpsRemaining;
                Console.WriteLine($"--- {name} ({size} bytes, under {parent}) ---");
                Console.WriteLine(HexAndAscii(data, payload, Math.Min(size, 512)));
            }
            // A sub-level is the one container that does not begin with its
            // children: it carries an 8-byte [name hash][inner size] header
            // first, so the tiling test below always rejects it and every
            // chunk nested inside one goes uncounted. mission.lvl is 113 of
            // these holding 1.18 MB - which is to say, nearly all of it.
            if (name == "lvl_" && size > 8)
            {
                Walk(data, payload + 8, payload + size, file, name, depth + 1, inSubject || name == Subject);
            }
            else if (size >= 8 && LooksLikeContainer(data, payload, payload + size))
            {
                Walk(data, payload, payload + size, file, name, depth + 1, inSubject || name == Subject);
            }

            // Chunks are padded to a 4-byte boundary.
            cursor = payload + size;
            cursor = (cursor + 3) & ~3;
        }
    }

    /// <summary>
    /// Whether a region tiles exactly as a sequence of well-formed chunks.
    /// </summary>
    /// <remarks>
    /// Requiring an exact tiling is what keeps leaf payloads - vertex buffers,
    /// texture bytes - from being mistaken for structure. Random binary
    /// occasionally starts with four printable bytes and a plausible length,
    /// but it essentially never lays out as a chain of them that lands exactly
    /// on the end of the region.
    /// </remarks>
    static bool LooksLikeContainer(byte[] data, int start, int end)
    {
        int cursor = start;
        int seen = 0;

        while (cursor + 8 <= end)
        {
            if (!IsPlausibleFourCC(ReadFourCC(data, cursor))) return false;

            int size = BitConverter.ToInt32(data, cursor + 4);
            if (size < 0 || cursor + 8 + size > end) return false;

            cursor = cursor + 8 + size;
            cursor = (cursor + 3) & ~3;
            ++seen;
        }

        return seen > 0 && cursor >= end - 3;
    }

    /// <summary>
    /// Collect the identifier-shaped string constants out of one compiled
    /// Lua 5.0 chunk.
    /// </summary>
    /// <remarks>
    /// A global call in Lua 5.0 compiles to GETGLOBAL against an entry in the
    /// function's constant table, so every API name a script calls survives in
    /// the bytecode as a plain string. Constants are length-prefixed - a
    /// 4-byte size_t followed by the bytes and a trailing NUL - which is a
    /// strong enough shape to find by scanning, and far less work than
    /// undumping the whole prototype tree just to reach the constant tables.
    ///
    /// The catch is that it cannot distinguish a called global from a table
    /// key or a string literal that happens to look like an identifier, so
    /// this over-reports. That is the right direction to be wrong in: the
    /// output is a candidate list to diff against the implemented API, not a
    /// specification.
    /// </remarks>
    /// <summary>Globals any stock script assigns to, and so provides itself.</summary>
    static readonly HashSet<string> LuaDefined = new HashSet<string>(StringComparer.Ordinal);

    static int LuaScripts;
    static int LuaDecoded;

    static void HarvestLuaIdentifiers(byte[] data, int at, int size)
    {
        ++LuaScripts;

        var reads = new HashSet<string>(StringComparer.Ordinal);
        if (LuaBytecode.Read(data, at, size, reads, LuaDefined))
        {
            ++LuaDecoded;
            foreach (string name in reads)
            {
                LuaRefs.TryGetValue(name, out int seen);
                LuaRefs[name] = seen + 1;
            }
            return;
        }

        // Fall back to the string scan only when the chunk will not decode,
        // so a format surprise degrades to a noisier answer instead of none.
        HarvestByScanning(data, at, size);
    }

    static void HarvestByScanning(byte[] data, int at, int size)
    {
        var inThisScript = new HashSet<string>(StringComparer.Ordinal);
        int end = at + size;

        for (int i = at; i + 4 < end; ++i)
        {
            uint length = BitConverter.ToUInt32(data, i);

            // Two characters plus a NUL at the low end; nothing resembling an
            // API name is anywhere near 128 bytes at the high end.
            if (length < 3 || length > 128 || i + 4 + length > end) continue;

            // The prefix counts the terminator, so the last byte must be one.
            if (data[i + 4 + length - 1] != 0) continue;

            int textLength = (int)length - 1;
            bool identifier = true;
            for (int c = 0; c < textLength; ++c)
            {
                byte b = data[i + 4 + c];
                bool ok = (b >= 'A' && b <= 'Z') || (b >= 'a' && b <= 'z') ||
                          (b >= '0' && b <= '9') || b == '_';
                if (!ok) { identifier = false; break; }
            }

            // A leading digit means a number-like literal, not a name.
            if (!identifier || data[i + 4] >= '0' && data[i + 4] <= '9') continue;

            inThisScript.Add(Encoding.ASCII.GetString(data, i + 4, textLength));
        }

        foreach (string name in inThisScript)
        {
            LuaRefs.TryGetValue(name, out int count);
            LuaRefs[name] = count + 1;
        }
    }

    static void ReportLua()
    {
        // Ranked by how many scripts want it: a name referenced by eighty
        // missions is load-bearing, one referenced once is probably a local.
        // A global the scripts assign to is supplied by the scripts, however
        // it looks - that is the whole stock framework, every Objective class
        // and every gametype. What the engine must provide is the remainder.
        var missing = LuaRefs.Where(e => !Known.Contains(e.Key) && !LuaDefined.Contains(e.Key))
                             .OrderByDescending(e => e.Value)
                             .ToList();

        Console.WriteLine($"{LuaScripts} script(s), {LuaDecoded} decoded as Lua 5.0 bytecode");
        Console.WriteLine($"{LuaRefs.Count} distinct global(s) read; {LuaDefined.Count} defined by the scripts themselves");
        Console.WriteLine($"{LuaRefs.Count - missing.Count} accounted for, {missing.Count} unresolved\n");
        Console.WriteLine($"{"SCRIPTS",8}  IDENTIFIER");
        Console.WriteLine(new string('-', 48));
        foreach (KeyValuePair<string, int> entry in missing)
        {
            Console.WriteLine($"{entry.Value,8}  {entry.Key}");
        }
    }

    static string ReadString(byte[] data, int at, int size)
    {
        int end = at;
        while (end < at + size && data[end] != 0) ++end;
        return Encoding.ASCII.GetString(data, at, end - at);
    }

    /// <summary>Hex plus printable ASCII, so structure and embedded names are both legible.</summary>
    static string HexAndAscii(byte[] data, int at, int length)
    {
        var text = new StringBuilder();
        for (int row = 0; row < length; row += 16)
        {
            int width = Math.Min(16, length - row);
            var hex = new StringBuilder();
            var ascii = new StringBuilder();
            for (int i = 0; i < width; ++i)
            {
                byte b = data[at + row + i];
                hex.Append(b.ToString("x2")).Append(' ');
                ascii.Append(b >= 32 && b < 127 ? (char)b : '.');
            }
            text.Append($"{row:x4}  {hex,-48} {ascii}").AppendLine();
        }
        return text.ToString();
    }

    static string ReadFourCC(byte[] data, int at) =>
        Encoding.ASCII.GetString(data, at, 4);

    static bool IsPlausibleFourCC(string name)
    {
        foreach (char c in name)
        {
            bool ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
                      (c >= '0' && c <= '9') || c == '_';
            if (!ok) return false;
        }
        return true;
    }

    static void Record(string name, int size, string file, string parent)
    {
        if (!Chunks.TryGetValue(name, out Tally tally))
        {
            tally = new Tally();
            Chunks.Add(name, tally);
        }
        ++tally.Count;
        tally.Bytes += size;
        tally.Parents.Add(parent);
        tally.Files.Add(file);
    }

    static void Report(int fileCount, long totalBytes)
    {
        Console.WriteLine($"Scanned {fileCount} file(s), {totalBytes / (1024 * 1024)} MB");
        Console.WriteLine($"{Chunks.Count} distinct chunk types\n");
        Console.WriteLine($"{"CHUNK",-8} {"COUNT",10} {"BYTES",14}  PARENTS");
        Console.WriteLine(new string('-', 78));

        foreach (KeyValuePair<string, Tally> entry in Chunks.OrderByDescending(e => e.Value.Bytes))
        {
            string parents = string.Join(",", entry.Value.Parents.OrderBy(p => p).Take(4));
            Console.WriteLine($"{entry.Key,-8} {entry.Value.Count,10} {entry.Value.Bytes,14}  {parents}");
        }
    }
}
