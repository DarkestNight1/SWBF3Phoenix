using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// 1) Parses every .cs file with Roslyn and reports SYNTAX diagnostics.
//    (Full semantic checking needs UnityEngine assemblies, unavailable here.)
//
// 2) Flags PhxProp<> / PhxMultiProp / PhxPropertySection field names that a
//    derived class redeclares when a base class already declares them.
//
//    Why: PhxClass.InitClass and PhxInstance.InitInstance enumerate fields via
//    Type.GetMembers() then call Type.GetField(name). A name present in both a
//    base and a derived class appears twice in GetMembers(), making the
//    subsequent GetField(name) ambiguous - so that ODF property is registered
//    twice and its assignment becomes order-dependent.
//
//    Types are keyed by QUALIFIED name (Outer.Inner) because this codebase has
//    many distinct nested types all called "ClassProperties".
class Program
{
    class TypeInfo
    {
        public string Qualified;
        public string Simple;
        public string Outer;            // null for top-level
        public string BaseRaw;          // as written, generics/qualifiers stripped
        public string File;
        public readonly List<(string name, int line)> PropFields = new List<(string, int)>();
    }

    static int Main(string[] args)
    {
        var opts = new CSharpParseOptions(LanguageVersion.CSharp8);
        var types = new Dictionary<string, TypeInfo>();
        int files = 0, syntaxErrorFiles = 0;

        foreach (string root in args)
        {
            foreach (string path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                files++;
                SyntaxTree tree = CSharpSyntaxTree.ParseText(File.ReadAllText(path), opts, path);

                var diags = tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
                if (diags.Count > 0)
                {
                    syntaxErrorFiles++;
                    Console.WriteLine($"=== SYNTAX {path}");
                    foreach (var d in diags.Take(10))
                        Console.WriteLine($"    line {d.Location.GetLineSpan().StartLinePosition.Line + 1}: {d.Id} {d.GetMessage()}");
                    continue;
                }

                foreach (var cls in tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())
                {
                    string simple = cls.Identifier.Text;
                    string outer = (cls.Parent as ClassDeclarationSyntax)?.Identifier.Text;
                    string qualified = outer != null ? outer + "." + simple : simple;

                    if (!types.TryGetValue(qualified, out var ti))
                    {
                        ti = new TypeInfo { Qualified = qualified, Simple = simple, Outer = outer, File = path };
                        types[qualified] = ti;
                    }

                    if (cls.BaseList != null && cls.BaseList.Types.Count > 0)
                    {
                        string b = cls.BaseList.Types[0].Type.ToString();
                        int lt = b.IndexOf('<');
                        if (lt > 0) b = b.Substring(0, lt);      // strip generic args
                        ti.BaseRaw = b;
                    }

                    foreach (var field in cls.Members.OfType<FieldDeclarationSyntax>())
                    {
                        string t = field.Declaration.Type.ToString();
                        if (!t.StartsWith("PhxProp<") && t != "PhxMultiProp" && t != "PhxPropertySection")
                            continue;
                        foreach (var v in field.Declaration.Variables)
                            ti.PropFields.Add((v.Identifier.Text,
                                v.Identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1));
                    }
                }
            }
        }

        // Resolve a base-type reference from the perspective of a given type.
        TypeInfo Resolve(TypeInfo from, string baseRaw)
        {
            if (string.IsNullOrEmpty(baseRaw)) return null;

            // already qualified (e.g. "PhxProp.ClassProperties")
            if (baseRaw.Contains(".") && types.TryGetValue(baseRaw, out var q)) return q;

            // sibling nested type in the same outer class
            if (from.Outer != null && types.TryGetValue(from.Outer + "." + baseRaw, out var sib)) return sib;

            // top-level type
            if (types.TryGetValue(baseRaw, out var top)) return top;
            return null;
        }

        int collisions = 0;
        foreach (var ti in types.Values.OrderBy(t => t.Qualified))
        {
            var inherited = new Dictionary<string, string>();
            var visited = new HashSet<string> { ti.Qualified };

            TypeInfo cur = Resolve(ti, ti.BaseRaw);
            while (cur != null && visited.Add(cur.Qualified))
            {
                foreach (var (n, _) in cur.PropFields)
                    if (!inherited.ContainsKey(n)) inherited[n] = cur.Qualified;
                cur = Resolve(cur, cur.BaseRaw);
            }

            foreach (var (n, line) in ti.PropFields)
            {
                if (inherited.TryGetValue(n, out string declaredIn))
                {
                    collisions++;
                    Console.WriteLine($"PROP COLLISION: {ti.Qualified}.{n} (line {line}) shadows base {declaredIn}.{n}");
                    Console.WriteLine($"                {ti.File}");
                }
            }
        }

        Console.WriteLine($"\nParsed {files} file(s). Syntax-error files: {syntaxErrorFiles}. " +
                          $"Types mapped: {types.Count}. PhxProp inheritance collisions: {collisions}.");
        return (syntaxErrorFiles == 0 && collisions == 0) ? 0 : 1;
    }
}
