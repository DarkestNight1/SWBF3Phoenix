using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using LibSWBF2.Wrappers;

namespace ScriptDump
{
    /// <summary>
    /// Dumps the string constants out of BF2's compiled Lua scripts, and diffs
    /// them against the functions Phoenix implements.
    ///
    /// Every global a Lua chunk calls appears in its constant table, so this
    /// gives the authoritative list of engine API a given mission or objective
    /// script depends on - as opposed to grepping the whole .lvl, which drags
    /// in class names, ODF keys and binary noise.
    ///
    /// Usage:
    ///   ScriptDump &lt;lvl&gt;[,&lt;lvl&gt;...] --script objective --api &lt;PhxLuaAPI.cs&gt;
    /// </summary>
    static class Program
    {
        static int Main(string[] args)
        {
            List<string> lvls = new List<string>();
            string scriptFilter = null;
            string apiFile = null;

            for (int i = 0; i < args.Length; ++i)
            {
                if (args[i] == "--script" && i + 1 < args.Length) { scriptFilter = args[++i].ToLower(); }
                else if (args[i] == "--api" && i + 1 < args.Length) { apiFile = args[++i]; }
                else { lvls.Add(args[i]); }
            }

            if (lvls.Count == 0)
            {
                Console.Error.WriteLine("usage: ScriptDump <lvl...> [--script <substr>] [--api <PhxLuaAPI.cs>]");
                return 2;
            }

            HashSet<string> implemented = apiFile != null ? ReadImplemented(apiFile) : new HashSet<string>();
            if (apiFile != null)
            {
                Console.WriteLine($"Phoenix implements {implemented.Count} Lua functions\n");
            }

            // name -> scripts that reference it
            SortedDictionary<string, SortedSet<string>> refs = new SortedDictionary<string, SortedSet<string>>();

            foreach (string lvlPath in lvls)
            {
                if (!File.Exists(lvlPath)) { Console.Error.WriteLine($"skip: {lvlPath}"); continue; }

                Level level = Level.FromFile(lvlPath);
                if (level == null) { Console.Error.WriteLine($"load failed: {lvlPath}"); continue; }

                Script[] scripts = level.Get<Script>();
                if (scripts == null) continue;

                foreach (Script s in scripts)
                {
                    string name = s.Name ?? "<unnamed>";
                    if (scriptFilter != null && !name.ToLower().Contains(scriptFilter)) continue;
                    if (!s.GetData(out IntPtr data, out uint size) || data == IntPtr.Zero || size == 0) continue;

                    byte[] bytes = new byte[size];
                    Marshal.Copy(data, bytes, 0, (int)size);

                    foreach (string token in ExtractIdentifiers(bytes))
                    {
                        if (!refs.TryGetValue(token, out SortedSet<string> from))
                        {
                            from = new SortedSet<string>();
                            refs[token] = from;
                        }
                        from.Add(name);
                    }
                }
            }

            // Identifier-shaped tokens that Phoenix does not implement are the
            // candidates worth looking at. Ranked by how many scripts want them,
            // because a function several modes call blocks several modes.
            var missing = refs
                .Where(kv => !implemented.Contains(kv.Key))
                .OrderByDescending(kv => kv.Value.Count)
                .ThenBy(kv => kv.Key)
                .ToList();

            Console.WriteLine($"referenced identifiers: {refs.Count}, not implemented: {missing.Count}\n");
            Console.WriteLine($"{"count",-6} {"function",-38} scripts");
            foreach (var kv in missing)
            {
                string scripts = string.Join(",", kv.Value.Take(4));
                if (kv.Value.Count > 4) scripts += ",…";
                Console.WriteLine($"{kv.Value.Count,-6} {kv.Key,-38} {scripts}");
            }

            return 0;
        }

        /// <summary>
        /// Printable runs from the bytecode that look like Lua identifiers.
        /// Filtered to names that plausibly denote engine calls rather than
        /// literals: must start with a letter and be long enough to not be
        /// noise, and must not look like a file path or sentence.
        /// </summary>
        static IEnumerable<string> ExtractIdentifiers(byte[] bytes)
        {
            StringBuilder sb = new StringBuilder();
            HashSet<string> seen = new HashSet<string>();

            for (int i = 0; i <= bytes.Length; ++i)
            {
                byte b = i < bytes.Length ? bytes[i] : (byte)0;
                bool isIdent = (b >= 'A' && b <= 'Z') || (b >= 'a' && b <= 'z')
                            || (b >= '0' && b <= '9') || b == '_';

                if (isIdent) { sb.Append((char)b); continue; }

                if (sb.Length >= 4)
                {
                    string token = sb.ToString();
                    char c0 = token[0];
                    if (((c0 >= 'A' && c0 <= 'Z') || (c0 >= 'a' && c0 <= 'z')) && seen.Add(token))
                    {
                        yield return token;
                    }
                }
                sb.Clear();
            }
        }

        /// <summary>Public static method names from PhxLuaAPI.cs.</summary>
        static HashSet<string> ReadImplemented(string apiFile)
        {
            HashSet<string> names = new HashSet<string>();
            if (!File.Exists(apiFile)) return names;

            foreach (string line in File.ReadAllLines(apiFile))
            {
                string t = line.Trim();
                if (!t.StartsWith("public static")) continue;

                int paren = t.IndexOf('(');
                if (paren <= 0) continue;

                string head = t.Substring(0, paren);
                int lastSpace = head.LastIndexOf(' ');
                if (lastSpace <= 0) continue;

                string name = head.Substring(lastSpace + 1).Trim();
                if (name.Length > 0) names.Add(name);
            }
            return names;
        }
    }
}
