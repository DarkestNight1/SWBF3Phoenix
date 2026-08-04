# SyntaxCheck

A Unity-free sanity check for the runtime scripts. Useful on machines (and CI)
that have the .NET SDK but no Unity install, where a normal compile isn't
possible.

```bash
dotnet run --project Tools/SyntaxCheck -- UnityProject/Assets/Runtime
```

Exit code is non-zero if anything is reported.

## What it checks

1. **Syntax** — parses every `.cs` file with Roslyn (C# 8, matching Unity
   2020.3's language level) and reports parse errors with file and line.

2. **`PhxProp` inheritance collisions** — flags a `PhxProp<>`,
   `PhxMultiProp` or `PhxPropertySection` field that a derived class
   redeclares when a base class already declares the same name.

   This matters because `PhxClass.InitClass` and `PhxInstance.InitInstance`
   enumerate fields with `Type.GetMembers()` and then call
   `Type.GetField(name)`. A name declared in both a base and a derived class
   appears twice in `GetMembers()`, which makes the following
   `GetField(name)` ambiguous — so that ODF property gets registered twice and
   its assignment becomes order-dependent. It's silent at runtime and shows up
   only as a property mysteriously not taking effect.

   Types are keyed by qualified name (`Outer.Inner`) because the codebase has
   many distinct nested types all named `ClassProperties`.

## What it does NOT check

Type resolution. UnityEngine/HDRP assemblies aren't referenced, so unresolved
symbols, wrong signatures and missing members are **not** caught — only a real
Unity compile finds those. Passing this check does not mean the project builds.
