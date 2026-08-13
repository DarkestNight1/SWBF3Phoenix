using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using LibSWBF2.Wrappers;

namespace TexRepro
{
    /// <summary>
    /// Loads real BF2 .lvl files through LibSWBF2 and decodes every texture,
    /// outside Unity.
    ///
    /// The texture crash reproduced only inside the editor, where a hard
    /// termination leaves no stack and each attempt costs a full Unity restart.
    /// Driving the same native code from a console app makes the failure cheap
    /// to reproduce and attributable to a specific texture.
    ///
    /// Usage: TexRepro &lt;lvl-or-directory&gt; [more...]
    /// </summary>
    static class Program
    {
        static int Main(string[] args)
        {
            if (args.Length == 0)
            {
                Console.Error.WriteLine("usage: TexRepro <lvl file or directory> [...]");
                return 2;
            }

            List<string> files = new List<string>();
            foreach (string arg in args)
            {
                if (Directory.Exists(arg))
                {
                    files.AddRange(Directory.GetFiles(arg, "*.lvl", SearchOption.TopDirectoryOnly));
                }
                else if (File.Exists(arg))
                {
                    files.Add(arg);
                }
                else
                {
                    Console.Error.WriteLine($"skip (not found): {arg}");
                }
            }

            int totalTextures = 0;
            int totalDecoded = 0;
            int totalFailed = 0;

            foreach (string path in files)
            {
                Console.Out.Flush();
                Console.WriteLine($"=== {Path.GetFileName(path)} ===");
                Console.Out.Flush();

                Level level;
                try
                {
                    level = Level.FromFile(path);
                }
                catch (Exception e)
                {
                    Console.WriteLine($"  load threw: {e.GetType().Name}: {e.Message}");
                    continue;
                }

                if (level == null)
                {
                    Console.WriteLine("  load returned null");
                    continue;
                }

                Texture[] textures = level.Get<Texture>();
                if (textures == null)
                {
                    Console.WriteLine("  no textures");
                    continue;
                }

                foreach (Texture tex in textures)
                {
                    totalTextures++;

                    // Name and dimensions are printed BEFORE decoding and the
                    // stream is flushed, so if the process dies inside the
                    // decode the last line printed names the culprit.
                    string label = $"  {tex.Name} {tex.Width}x{tex.Height}";
                    Console.Write(label);
                    Console.Out.Flush();

                    try
                    {
                        byte[] rgba = tex.GetBytesRGBA();
                        long expected = (long)tex.Width * tex.Height * 4;

                        if (rgba == null)
                        {
                            Console.WriteLine("  -> null");
                            totalFailed++;
                        }
                        else if (rgba.LongLength != expected)
                        {
                            Console.WriteLine($"  -> SIZE MISMATCH got {rgba.LongLength}, expected {expected}");
                            totalFailed++;
                        }
                        else
                        {
                            Console.WriteLine("  -> ok");
                            totalDecoded++;
                        }
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine($"  -> threw {e.GetType().Name}: {e.Message}");
                        totalFailed++;
                    }
                    Console.Out.Flush();
                }
            }

            Console.WriteLine();
            Console.WriteLine($"textures seen: {totalTextures}, decoded ok: {totalDecoded}, failed: {totalFailed}");
            return totalFailed > 0 ? 1 : 0;
        }
    }
}
