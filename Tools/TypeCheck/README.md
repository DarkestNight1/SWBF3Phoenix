# TypeCheck

A real compile of the project's scripts, without Unity.

```bash
dotnet run --project Tools/TypeCheck -c Release -- --config both
```

Exit code is non-zero if either configuration has errors.

## Why this exists

`Tools/SyntaxCheck` parses with Roslyn but references no assemblies, so it
cannot see a wrong signature, a missing member, an ambiguous type or an
unassigned local. Those cost a full editor round-trip each to find.

This references the installed Unity assemblies, the project's already-compiled
package assemblies, and LibSWBF2, so it reports what the C# compiler reports.

It checks two configurations:

| Configuration | Sources | Defines | Notes |
|---|---|---|---|
| `editor` | everything | `UNITY_EDITOR` | reproduces what the Unity editor compiles |
| `player` | excludes `*/Editor/*` | `LVLIMPORT_NO_EDITOR` | **the standalone build** |

The `player` pass is the point. A headless build needs an activated licence, so
it is the configuration nobody can check casually — and it is a genuinely
different compile: different sources, different defines, and no editor
assemblies. Package `*.Editor.*` assemblies are excluded from its reference set
so a runtime script using an editor-only type fails here rather than at build
time.

`LVLIMPORT_NO_EDITOR` is defined for the Standalone group in
`ProjectSettings.asset`, which is what gates the importer's asset-saving code
out of player builds.

## Options

```
--config editor|player|both    default: both
--unity <editor dir>           default: C:\Program Files\Unity\Hub\Editor\2020.3.22f1
--max N                        errors printed per configuration, default 40
```

## Reference assemblies

Resolved from the Unity install and the project:

- `Editor/Data/MonoBleedingEdge/lib/mono/unityjit` — the BCL
- `.../unityjit/Facades` — `netstandard.dll` as a **facade**. Required because
  `LibSWBF2.NET` targets netstandard 2.0. It must not be
  `NetStandard/ref/2.0.0/netstandard.dll`, which is a contract assembly that
  declares the primitives itself: referencing that alongside mscorlib makes
  every `System.Int32` ambiguous (CS0433) while leaving the real definitions
  unreachable (CS0518).
- `Editor/Data/Managed/UnityEngine` and `UnityEngine.dll` — engine modules
- `Editor/Data/Managed/UnityEditor.dll` — editor configuration only
- `UnityProject/Library/ScriptAssemblies` — HDRP, Burst, Collections,
  Mathematics, TextMeshPro. Requires the project to have been imported once.
- `UnityProject/Assets/Lib` — `LibSWBF2.NET.dll`. The native `LibSWBF2.dll` and
  `lua50-swbf2-x64.dll` sit in the same folder and are skipped by testing for
  managed metadata; otherwise they surface as CS0009 during compilation.

## What it does not check

- **Editor and runtime are compiled together** in the `editor` pass. Unity
  splits them into `Assembly-CSharp` and `Assembly-CSharp-Editor`, so a runtime
  file referencing an editor type is caught by the `player` pass, not this one.
- Anything that is not a compile error: serialized field wiring, missing
  `.meta` files, shader compilation, asset references, or behaviour.
- It depends on `Library/ScriptAssemblies` being present and current. After a
  package upgrade, open the project in Unity once before trusting the result.
