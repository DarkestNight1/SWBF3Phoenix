using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// Reads chunks straight out of a <c>.lvl</c> file that LibSWBF2 does not
/// surface.
/// </summary>
/// <remarks>
/// Six chunk types in the stock data have no route into C#. Four have no
/// handler in the native library at all (<c>CLTH</c>, <c>plnp</c>, <c>sanm</c>,
/// <c>load</c>), and two are parsed only to be skipped (<c>port</c>,
/// <c>mcfg</c>). Reaching them through the library would mean a new chunk
/// class, a C API export, a managed wrapper and a rebuilt native binary for
/// each - and the container format does not justify that. It is a FourCC, a
/// little-endian size, and the payload, padded to four bytes. Reading it here
/// costs one file walk and leaves the native library alone.
///
/// The one subtlety is <c>lvl_</c>: a sub-level carries an eight-byte
/// [name hash][inner size] header before its children, so it is the single
/// place where a container does not begin with a chunk. Missing that hides
/// everything nested inside one - which in <c>mission.lvl</c> is 1.18 MB of
/// the 1.25 MB file.
/// </remarks>
public static class BFRawChunkReader
{
    /// <summary>One chunk located in a file, with its payload bounds.</summary>
    public readonly struct BFRawChunk
    {
        public readonly string Name;
        public readonly int Start;
        public readonly int Length;
        public readonly string Parent;

        public BFRawChunk(string name, int start, int length, string parent)
        {
            Name = name;
            Start = start;
            Length = length;
            Parent = parent;
        }
    }

    /// <summary>
    /// Every chunk of the named types in a file, in the order they appear.
    /// </summary>
    /// <param name="wanted">FourCCs to collect. Null collects everything.</param>
    public static List<BFRawChunk> Find(byte[] data, params string[] wanted)
    {
        var found = new List<BFRawChunk>();
        var filter = wanted != null && wanted.Length > 0
            ? new HashSet<string>(wanted, StringComparer.Ordinal)
            : null;

        Walk(data, 0, data.Length, "<root>", 0, filter, found);
        return found;
    }

    /// <summary>Same, reading the file from disk. Empty list if unreadable.</summary>
    public static List<BFRawChunk> FindInFile(string path, params string[] wanted)
    {
        try
        {
            return Find(File.ReadAllBytes(path), wanted);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[BFSource] Could not read '{path}' for raw chunks: {e.Message}");
            return new List<BFRawChunk>();
        }
    }

    static void Walk(byte[] data, int start, int end, string parent, int depth,
                     HashSet<string> filter, List<BFRawChunk> found)
    {
        // A misidentified leaf can look like an endless nest of tiny chunks.
        if (depth > 12) return;

        // Clamp to the buffer regardless of what the caller passed. Every
        // bounds check below is expressed relative to end, so an end past
        // the array would make all of them permissive.
        if (data == null) return;
        if (start < 0) start = 0;
        if (end > data.Length) end = data.Length;

        int cursor = start;
        while (cursor + 8 <= end)
        {
            string name = FourCC(data, cursor);
            if (!PlausibleFourCC(name)) return;

            int size = BitConverter.ToInt32(data, cursor + 4);
            // Compare against the remaining space rather than adding to
            // size: a corrupt chunk header carrying a size near int.MaxValue
            // makes cursor + 8 + size overflow to a NEGATIVE value, which
            // then passes a "> end" test. The cursor advance below inherits
            // that negative value and the next FourCC reads at a negative
            // index. end - cursor - 8 cannot overflow, because cursor is
            // never negative and end never exceeds data.Length.
            if (size < 0 || size > end - cursor - 8) return;

            int payload = cursor + 8;
            if (filter == null || filter.Contains(name))
            {
                found.Add(new BFRawChunk(name, payload, size, parent));
            }

            if (name == "lvl_" && size > 8)
            {
                Walk(data, payload + 8, payload + size, name, depth + 1, filter, found);
            }
            else if (size >= 8 && Tiles(data, payload, payload + size))
            {
                Walk(data, payload, payload + size, name, depth + 1, filter, found);
            }

            cursor = ((payload + size) + 3) & ~3;
        }
    }

    /// <summary>
    /// Whether a region lays out exactly as a chain of well-formed chunks.
    /// </summary>
    /// <remarks>
    /// Requiring an exact tiling is what stops leaf payloads - vertex buffers,
    /// texture bytes - being descended into as if they were structure. Random
    /// binary sometimes opens with four printable bytes and a plausible
    /// length; it essentially never chains them to land precisely on the end.
    /// </remarks>
    static bool Tiles(byte[] data, int start, int end)
    {
        int cursor = start;
        int seen = 0;

        while (cursor + 8 <= end)
        {
            if (!PlausibleFourCC(FourCC(data, cursor))) return false;

            int size = BitConverter.ToInt32(data, cursor + 4);
            // Overflow-safe for the same reason as in Walk above.
            if (size < 0 || size > end - cursor - 8) return false;

            cursor = ((cursor + 8 + size) + 3) & ~3;
            ++seen;
        }

        return seen > 0 && cursor >= end - 3;
    }

    /// <summary>
    /// The four-character chunk tag at <paramref name="at"/>, or null when
    /// that would read outside the buffer.
    /// </summary>
    /// <remarks>
    /// The range check is a backstop, not the primary defence - the callers
    /// bound the cursor themselves. It is here because the failure mode when
    /// they get it wrong is an ArgumentOutOfRangeException naming byteIndex,
    /// which says nothing about chunks, offsets or the file being parsed.
    /// </remarks>
    static string FourCC(byte[] data, int at)
    {
        if (data == null || at < 0 || at + 4 > data.Length) return null;
        return Encoding.ASCII.GetString(data, at, 4);
    }

    static bool PlausibleFourCC(string name)
    {
        // Null means FourCC refused an out-of-range read; treat it as
        // implausible so the walk stops rather than throwing.
        if (name == null || name.Length != 4) return false;

        for (int i = 0; i < name.Length; ++i)
        {
            char c = name[i];
            bool ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
                      (c >= '0' && c <= '9') || c == '_';
            if (!ok) return false;
        }
        return true;
    }

    /// <summary>A NUL-terminated string inside a payload.</summary>
    public static string ReadString(byte[] data, int at, int max)
    {
        int end = at;
        int limit = Mathf.Min(at + max, data.Length);
        while (end < limit && data[end] != 0) ++end;
        return Encoding.ASCII.GetString(data, at, end - at);
    }
}
