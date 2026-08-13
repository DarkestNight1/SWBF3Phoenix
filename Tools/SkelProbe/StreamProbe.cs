using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using LibSWBF2.Utils;
using LibSWBF2.Wrappers;
using LibSWBF2.Enums;
using LibSWBF2;

namespace SkelProbe
{
    /// <summary>
    /// Play a level's music stream end to end, the way the runtime would.
    /// </summary>
    /// <remarks>
    /// A level's sound lvl keeps its music and long voice-over in one big
    /// Stream chunk. StreamData does not load any of it - it only records where
    /// each segment starts - so playback has to hand the stream an open
    /// FileReader and a scratch buffer before selecting a segment. Neither is
    /// done anywhere in the project today, which is why SetSegment fails and
    /// every track is silent.
    ///
    /// This walks the file for stream names, then does the full sequence
    /// (SetFileReader, SetFileStreamBuffer, SetSegment, ReadSamplesUnity) so
    /// the fix is verified against real audio rather than assumed.
    /// </remarks>
    static class StreamProbe
    {
        static Dictionary<uint, string> Names;

        static void LoadNames(string csvPath)
        {
            Names = new Dictionary<uint, string>();
            if (!File.Exists(csvPath)) return;
            foreach (string raw in File.ReadLines(csvPath))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                uint f = HashUtils.GetFNV(line);
                if (!Names.ContainsKey(f)) Names[f] = line;
            }
        }

        static string Resolve(uint hash)
        {
            return Names != null && Names.TryGetValue(hash, out string n) ? n : $"0x{hash:x8}";
        }

        /// <summary>Find every Stream chunk's name hash by walking the raw file.</summary>
        /// <remarks>
        /// Mirrors Stream::PeekStreamName: the name sits 20 bytes past the
        /// chunk tag. Streams live under a StreamList at the top level.
        /// </remarks>
        static List<uint> FindStreamNames(string path)
        {
            var found = new List<uint>();
            byte[] d;
            try { d = File.ReadAllBytes(path); } catch { return found; }

            uint streamTag = HashUtils.GetFNV("Stream");
            uint listTag = HashUtils.GetFNV("StreamList");

            int end = Math.Min(d.Length, 8 + BitConverter.ToInt32(d, 4));
            int cursor = 8;

            while (cursor + 8 <= end)
            {
                uint tag = BitConverter.ToUInt32(d, cursor);
                int size = BitConverter.ToInt32(d, cursor + 4);
                if (size < 0 || size > end - cursor - 8) { ++cursor; continue; }

                if (tag == listTag)
                {
                    // Descend: streams sit directly inside the list.
                    cursor += 8;
                    continue;
                }

                if (tag == streamTag && cursor + 24 <= d.Length)
                {
                    found.Add(BitConverter.ToUInt32(d, cursor + 20));
                }

                cursor = ((cursor + 8 + size) + 3) & ~3;
            }

            return found;
        }

        public static void Run(string path, string csvPath)
        {
            if (Names == null) LoadNames(csvPath);
            LibSWBF2.Logging.Logger.SetLogLevel(LibSWBF2.Logging.ELogType.Warning);

            List<uint> streamNames = FindStreamNames(path);
            Console.WriteLine($"=== {Path.GetFileName(path)}: {streamNames.Count} stream(s) in file");
            foreach (uint sn in streamNames)
            {
                Console.WriteLine($"    stream {Resolve(sn)}");
            }
            if (streamNames.Count == 0) return;

            Container con = new Container();
            SWBF2Handle h = con.AddLevel(path);
            con.LoadLevels();
            for (int i = 0; i < 1200; ++i)
            {
                ELoadStatus s = con.GetStatus(h);
                if (s == ELoadStatus.Loaded || s == ELoadStatus.Failed) break;
                Thread.Sleep(50);
            }

            Level lvl = con.GetLevel(h, true);
            if (lvl == null) { Console.WriteLine("    level did not load"); return; }

            const int BufferBytes = 1 << 16;
            IntPtr scratch = Marshal.AllocHGlobal(BufferBytes);

            try
            {
                foreach (uint sn in streamNames)
                {
                    FileReader reader = FileReader.FromFile(path);
                    if (reader == null) { Console.WriteLine("    could not open file"); continue; }

                    SoundStream stream = lvl.FindAndIndexSoundStream(reader, sn);
                    if (stream == null)
                    {
                        Console.WriteLine($"    {Resolve(sn)}: FindAndIndexSoundStream returned null");
                        continue;
                    }

                    Sound[] segments = stream.GetSounds();
                    Console.WriteLine($"    {Resolve(sn)}: {segments.Length} segment(s), " +
                                      $"{stream.NumChannels}ch, {stream.Format}, HasData {stream.HasData}");

                    // Without these two the native SetSegment refuses.
                    stream.SetFileReader(reader);
                    stream.SetFileStreamBuffer(scratch, BufferBytes);

                    int played = 0, silent = 0;
                    long totalSamples = 0;

                    for (int i = 0; i < segments.Length && i < 8; ++i)
                    {
                        Sound seg = segments[i];
                        if (seg == null) continue;

                        if (!stream.SetSegment(seg.Name))
                        {
                            Console.WriteLine($"        {Resolve(seg.Name)}: SetSegment failed");
                            continue;
                        }

                        float[] buf = new float[16384];
                        long got = 0;
                        int read;
                        int guard = 0;
                        while ((read = stream.ReadSamplesUnity(buf)) > 0 && guard++ < 4096)
                        {
                            got += read;
                            if (read < buf.Length) break;
                        }

                        totalSamples += got;
                        if (got > 0) ++played; else ++silent;

                        Console.WriteLine($"        {Resolve(seg.Name),-32} {seg.SampleRate,6} Hz  " +
                                          $"{seg.NumSamples,9} declared  {got,9} decoded");
                    }

                    Console.WriteLine($"    -> {played} segment(s) decoded, {silent} silent, " +
                                      $"{totalSamples:N0} samples total");

                    GC.KeepAlive(reader);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(scratch);
            }

            int shown = 0;
            while (LibSWBF2.Logging.Logger.GetNextLog(out LibSWBF2.Logging.LoggerEntry entry))
            {
                if (shown++ < 12) Console.WriteLine($"    LOG {entry.Level}: {entry.Message}");
            }
        }
    }
}
