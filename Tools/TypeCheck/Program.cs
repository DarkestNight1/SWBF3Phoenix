using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace TypeCheck
{
    /// <summary>
    /// A real compile of the project's scripts, outside Unity.
    /// </summary>
    /// <remarks>
    /// SyntaxCheck parses but references nothing, so it cannot see a wrong
    /// signature, a missing member, an ambiguous type or an unassigned local -
    /// exactly the errors that cost a full editor round-trip each to find. This
    /// references the installed Unity assemblies, the project's compiled
    /// package assemblies and LibSWBF2, and reports what Roslyn reports.
    ///
    /// It also checks the configuration Unity cannot check here at all: the
    /// PLAYER build. That compile excludes Editor folders and defines
    /// LVLIMPORT_NO_EDITOR, and it is the one that headless builds need a
    /// licence to run. Catching a player-only break offline is the main reason
    /// this exists.
    ///
    /// Usage:
    ///   TypeCheck [--config editor|player|both] [--unity &lt;editor dir&gt;] [--max N]
    /// </remarks>
    static class Program
    {
        const string DefaultUnity = @"C:\Program Files\Unity\Hub\Editor\2020.3.22f1";

        static int Main(string[] args)
        {
            string config = "both";
            string unityRoot = DefaultUnity;
            int max = 40;

            for (int i = 0; i < args.Length; ++i)
            {
                if (args[i] == "--config" && i + 1 < args.Length) config = args[++i].ToLower();
                else if (args[i] == "--unity" && i + 1 < args.Length) unityRoot = args[++i];
                else if (args[i] == "--max" && i + 1 < args.Length) max = int.Parse(args[++i]);
            }

            string repo = FindRepoRoot();
            if (repo == null)
            {
                Console.Error.WriteLine("Could not locate the repository root (looked for UnityProject/Assets).");
                return 2;
            }

            string data = Path.Combine(unityRoot, "Editor", "Data");
            if (!Directory.Exists(data))
            {
                Console.Error.WriteLine($"Unity install not found at '{unityRoot}'. Pass --unity.");
                return 2;
            }

            int failed = 0;
            if (config == "editor" || config == "both") failed += Run(repo, data, unityRoot, true, max);
            if (config == "player" || config == "both") failed += Run(repo, data, unityRoot, false, max);
            return failed == 0 ? 0 : 1;
        }

        /// <summary>The editor's version, parsed out of its install directory name.</summary>
        /// <remarks>
        /// The directory is what the Hub names after the version, and it is the
        /// only version marker that is present before the editor has ever run -
        /// which is the case this has to work in, since the whole point is to
        /// check code against an engine the project has not been opened with
        /// yet.
        /// </remarks>
        static (int Major, int Minor) EditorVersion(string unityRoot)
        {
            string name = new DirectoryInfo(unityRoot.TrimEnd('\\', '/')).Name;

            var m = System.Text.RegularExpressions.Regex.Match(name, @"^(\d+)\.(\d+)");
            if (!m.Success) return (0, 0);

            return (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
        }

        static bool IsUnity6(string unityRoot) => EditorVersion(unityRoot).Major >= 6000;

        /// <summary>
        /// The UNITY_* version defines the editor would set.
        /// </summary>
        /// <remarks>
        /// Unity defines the exact version, the major, and an _OR_NEWER symbol
        /// for every release up to the running one. Reproducing the whole
        /// ladder is not worth it - what matters is that code guarded on
        /// "6000 or newer" and code guarded on "2020_3 or newer" both resolve
        /// the way the real compiler resolves them.
        /// </remarks>
        static IEnumerable<string> VersionDefines(string unityRoot)
        {
            (int major, int minor) = EditorVersion(unityRoot);

            // Everything from 2017 onward keeps these true.
            yield return "UNITY_2017_1_OR_NEWER";
            yield return "UNITY_2018_1_OR_NEWER";
            yield return "UNITY_2019_1_OR_NEWER";
            yield return "UNITY_2020_1_OR_NEWER";
            yield return "UNITY_2020_3_OR_NEWER";

            if (major >= 6000)
            {
                yield return "UNITY_2021_1_OR_NEWER";
                yield return "UNITY_2022_1_OR_NEWER";
                yield return "UNITY_2023_1_OR_NEWER";
                yield return "UNITY_6000_0_OR_NEWER";
                yield return "UNITY_6000";
                yield return $"UNITY_6000_{minor}_OR_NEWER";
                yield return $"UNITY_6000_{minor}";
            }
            else
            {
                yield return "UNITY_2020_3";
                yield return "UNITY_2020";

                // Only the old stack ships the post-processing package; under
                // Unity 6 this project's effects are all HDRP volume overrides.
                yield return "UNITY_POST_PROCESSING_STACK_V2";
            }
        }

        static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "UnityProject", "Assets")))
                {
                    return dir.FullName;
                }
                dir = dir.Parent;
            }
            return null;
        }

        static int Run(string repo, string data, string unityRoot, bool editorConfig, int max)
        {
            string assets = Path.Combine(repo, "UnityProject", "Assets");
            string label = editorConfig ? "EDITOR" : "PLAYER";

            Console.WriteLine();
            Console.WriteLine($"================ {label} configuration ================");

            List<string> sources = CollectSources(assets, editorConfig);

            // Unity's own defines, plus the project's. LVLIMPORT_NO_EDITOR is
            // set for the Standalone group only (ProjectSettings.asset), which
            // is what makes the player compile a genuinely different one.
            // Derived from whichever editor this was pointed at rather than
            // hardcoded. The version defines decide which branch of a
            // #if UNITY_6000_0_OR_NEWER the compiler sees, so a checker that
            // always claimed 2020.3 would verify the wrong half of every
            // compatibility shim - the exact thing it exists to catch during
            // an engine upgrade.
            var defines = new List<string>
            {
                "UNITY_5_3_OR_NEWER",
                "UNITY_STANDALONE", "UNITY_STANDALONE_WIN", "UNITY_64",
                "ENABLE_INPUT_SYSTEM", "CSHARP_7_3_OR_NEWER",
            };
            defines.AddRange(VersionDefines(unityRoot));

            if (editorConfig) defines.Add("UNITY_EDITOR");
            else defines.Add("LVLIMPORT_NO_EDITOR");

            var parseOptions = new CSharpParseOptions(
                // Unity 6 compiles as C# 9; 2020.3 as C# 8.
                IsUnity6(unityRoot) ? LanguageVersion.CSharp9 : LanguageVersion.CSharp8,
                preprocessorSymbols: defines);

            var trees = new List<SyntaxTree>(sources.Count);
            foreach (string path in sources)
            {
                string text;
                try { text = File.ReadAllText(path); }
                catch (Exception e)
                {
                    Console.WriteLine($"  (unreadable) {path}: {e.Message}");
                    continue;
                }
                trees.Add(CSharpSyntaxTree.ParseText(text, parseOptions, path));
            }

            List<MetadataReference> refs = CollectReferences(repo, data, editorConfig);

            Console.WriteLine($"sources: {trees.Count}   references: {refs.Count}   defines: {(editorConfig ? "UNITY_EDITOR" : "LVLIMPORT_NO_EDITOR")}");

            var compilation = CSharpCompilation.Create(
                editorConfig ? "Assembly-CSharp-Editor" : "Assembly-CSharp",
                trees,
                refs,
                new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary,
                    allowUnsafe: true,
                    // Unity does not treat warnings as errors, and the package
                    // sources produce plenty. Only errors matter here.
                    generalDiagnosticOption: ReportDiagnostic.Default));

            var diagnostics = compilation.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .ToList();

            // A missing reference produces an avalanche of CS0246/CS0234 that
            // says nothing about the code. Call that out rather than printing
            // 900 lines of it.
            int missingType = diagnostics.Count(d => d.Id == "CS0246" || d.Id == "CS0234");
            if (missingType > diagnostics.Count / 2 && diagnostics.Count > 50)
            {
                Console.WriteLine();
                Console.WriteLine($"  {missingType} of {diagnostics.Count} errors are unresolved types.");
                Console.WriteLine("  That usually means a reference assembly is missing rather than that");
                Console.WriteLine("  the code is broken. Most common unresolved names:");

                foreach (var g in diagnostics
                         .Where(d => d.Id == "CS0246" || d.Id == "CS0234")
                         .GroupBy(d => d.GetMessage())
                         .OrderByDescending(g => g.Count())
                         .Take(10))
                {
                    Console.WriteLine($"    {g.Count(),5}  {g.Key}");
                }
                return 1;
            }

            if (diagnostics.Count == 0)
            {
                Console.WriteLine($"  {label}: no errors.");
                return 0;
            }

            Console.WriteLine();
            Console.WriteLine($"  {diagnostics.Count} error(s):");
            foreach (var d in diagnostics.Take(max))
            {
                FileLinePositionSpan span = d.Location.GetLineSpan();
                string file = span.Path;
                if (file != null && file.StartsWith(repo)) file = file.Substring(repo.Length).TrimStart('\\', '/');
                Console.WriteLine($"    {file}({span.StartLinePosition.Line + 1},{span.StartLinePosition.Character + 1}): {d.Id}: {d.GetMessage()}");
            }
            if (diagnostics.Count > max)
            {
                Console.WriteLine($"    ... and {diagnostics.Count - max} more (raise --max)");
            }
            return 1;
        }

        /// <summary>
        /// The files that end up in the assembly under test.
        /// </summary>
        /// <remarks>
        /// Unity puts anything under a folder named "Editor" into
        /// Assembly-CSharp-Editor and everything else into Assembly-CSharp.
        /// Samples are excluded: they are imported package content, not project
        /// code, and they are gitignored.
        /// </remarks>
        static List<string> CollectSources(string assets, bool editorConfig)
        {
            var all = Directory.GetFiles(assets, "*.cs", SearchOption.AllDirectories)
                .Where(p => !IsUnder(p, assets, "Samples"))
                .ToList();

            // The editor assembly references the runtime one, so compiling
            // everything together is the closest single-pass approximation and
            // still finds every real error.
            if (editorConfig) return all;

            return all.Where(p => !IsEditorPath(p)).ToList();
        }

        static bool IsEditorPath(string path)
        {
            string norm = path.Replace('/', '\\');
            return norm.Contains("\\Editor\\");
        }

        static bool IsUnder(string path, string root, string folder)
        {
            string rel = path.Substring(root.Length).Replace('/', '\\').TrimStart('\\');
            return rel.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase);
        }

        static List<MetadataReference> CollectReferences(string repo, string data, bool editorConfig)
        {
            var paths = new List<string>();

            void AddDir(string dir, SearchOption opt = SearchOption.TopDirectoryOnly)
            {
                if (!Directory.Exists(dir)) return;
                paths.AddRange(Directory.GetFiles(dir, "*.dll", opt));
            }

            // Core BCL as Unity ships it - a complete set (96 assemblies:
            // mscorlib, System, System.Core...).
            //
            // Deliberately NOT combined with NetStandard/ref: netstandard.dll
            // type-forwards the same primitives, so referencing both makes
            // every System.Int32 ambiguous (CS0433) while leaving the real
            // definitions unreachable (CS0518). One core only.
            // Unity 6 moved the reference BCL. 2020.3 ships the JIT profile
            // under MonoBleedingEdge; Unity 6 ships proper reference
            // assemblies under UnityReferenceAssemblies/unity-4.8-api and no
            // longer has the old path at all. Probed rather than branched on
            // the version, so an editor that ships both keeps working.
            string mscorlib = Path.Combine(data, "UnityReferenceAssemblies", "unity-4.8-api");
            if (!Directory.Exists(mscorlib))
            {
                mscorlib = Path.Combine(data, "MonoBleedingEdge", "lib", "mono", "unityjit");
            }
            AddDir(mscorlib);

            // LibSWBF2.NET targets netstandard 2.0, so consuming its types
            // needs a netstandard reference.
            //
            // It must be the FACADE that ships beside this BCL, which only
            // type-forwards into mscorlib. NetStandard/ref/2.0.0/netstandard.dll
            // is the contract assembly and declares the primitives itself, so
            // referencing that one alongside mscorlib makes every System.Int32
            // ambiguous (CS0433) while leaving the real definitions unreachable
            // (CS0518). The project's api compatibility level is .NET 4.x, so
            // the mono facade is the correct pairing.
            AddDir(Path.Combine(mscorlib, "Facades"));

            // Engine modules.
            AddDir(Path.Combine(data, "Managed", "UnityEngine"));
            paths.Add(Path.Combine(data, "Managed", "UnityEngine.dll"));
            if (editorConfig)
            {
                paths.Add(Path.Combine(data, "Managed", "UnityEditor.dll"));
            }

            // Package assemblies Unity already compiled (HDRP, Burst, Collections,
            // Mathematics, TextMeshPro...). Skip the ones we are compiling.
            string scriptAsm = Path.Combine(repo, "UnityProject", "Library", "ScriptAssemblies");
            if (Directory.Exists(scriptAsm))
            {
                paths.AddRange(Directory.GetFiles(scriptAsm, "*.dll")
                    .Where(p =>
                    {
                        string n = Path.GetFileName(p);
                        if (n.StartsWith("Assembly-CSharp", StringComparison.OrdinalIgnoreCase)) return false;

                        // A player build does not link the editor assemblies of
                        // a package. Referencing them here would let a runtime
                        // script use an editor-only type and still pass, which
                        // is precisely the break this configuration exists to
                        // catch.
                        if (!editorConfig &&
                            n.IndexOf(".Editor.", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            return false;
                        }
                        return true;
                    }));
            }

            // LibSWBF2.NET and the Lua binding.
            AddDir(Path.Combine(repo, "UnityProject", "Assets", "Lib"));

            var refs = new List<MetadataReference>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string p in paths)
            {
                if (!File.Exists(p)) continue;

                // Duplicate simple names (netstandard vs mono facades) make
                // Roslyn ambiguous rather than complete - first one wins.
                string name = Path.GetFileName(p);
                if (!seen.Add(name)) continue;

                // Assets/Lib holds native libraries next to the managed
                // wrapper. CreateFromFile accepts them lazily and the failure
                // only surfaces as CS0009 during compilation, so test for
                // managed metadata up front - GetAssemblyName throws
                // BadImageFormatException on a native PE.
                try { System.Reflection.AssemblyName.GetAssemblyName(p); }
                catch { continue; }

                try { refs.Add(MetadataReference.CreateFromFile(p)); }
                catch { /* unreadable */ }
            }

            return refs;
        }
    }
}
