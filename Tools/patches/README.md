# Submodule patches

Both native/importer submodules carry local fixes. Because they live in
submodules with their own upstreams, those fixes are **not** carried by a
commit to this repository — these patches are the durable copy.

| Patch | Submodule | Pinned commit |
|---|---|---|
| `lvlimport-phoenix-fixes.patch` | `UnityProject/Assets/LVLImport` | `8dd7de0` |
| `libswbf2-build-fixes.patch` | `LibSWBF2` | `3ee5fc9` |
| `lua50-static-crt.patch` | `lua5.0-swbf2-x64` | `00b414c` |

Each has been verified to apply cleanly against its pinned commit.

---

# The shipped binaries were Debug builds

This is the single highest-impact finding, and it explains the
`DllNotFoundException` that the importer's Windows users report.

`Assets/Lib/LibSWBF2.dll` as shipped imported:

```
MSVCP140D.dll  VCRUNTIME140D.dll  VCRUNTIME140_1D.dll  ucrtbased.dll
```

The trailing **`D` means the debug CRT**, and the debug CRT is *not
redistributable* — Microsoft ships it only with Visual Studio. So the library
loads on a developer's machine and fails on everyone else's, and .NET reports
that as a `DllNotFoundException` naming `LibSWBF2` while saying nothing about
the runtime that is actually missing.

That accounts for every property of the reported bug: Windows-only, immediate,
affecting all `.lvl` files equally, and "fixed" by installing Visual Studio or
the Windows SDK — which merely drops the missing CRTs onto the machine.

`lua50-swbf2-x64.dll` had the same problem (`VCRUNTIME140D.dll`,
`ucrtbased.dll`).

It is also a plausible contributor to the second reported issue, the array
index out of bounds: the debug CRT changes `_ITERATOR_DEBUG_LEVEL` and with it
the layout of standard library containers, so a debug-built native library
paired with a release-built managed wrapper is exactly the sort of mismatch
that corrupts marshalled reads.

## Current state

All three natives were rebuilt in Release with a static CRT and installed. Each
now imports **only `KERNEL32.dll`**, so they load on a clean Windows box with
no Visual Studio, no Windows SDK and no redistributable:

| Binary | Before | After |
|---|---|---|
| `LibSWBF2.dll` | 7.8 MB, debug CRT | 2.2 MB, `KERNEL32.dll` only |
| `lua50-swbf2-x64.dll` | debug CRT | `KERNEL32.dll` only |
| `LibSWBF2.NET.dll` | Debug | Release |

The Debug originals are kept in `Tools/lib-backup-debug/` in case a behavioural
difference shows up. To revert, copy them back over `UnityProject/Assets/Lib/`.

The stale `LibSWBF2.NET.pdb` was removed — `BuildAndCopyLibsWin.bat` only
copies it in Debug mode, so its presence was what revealed the shipped
binaries were Debug builds in the first place.

## Avoiding the regression

`BuildAndCopyLibsWin.bat` defaults to Release and is not at fault; the shipped
artifacts had simply been produced by choosing option 2. When rebuilding,
take the Release default, and sanity check the result:

```bash
dumpbin /DEPENDENTS UnityProject/Assets/Lib/LibSWBF2.dll
```

Anything beyond `KERNEL32.dll` — particularly a name ending in `D.dll` — means
the build will not load on machines without Visual Studio.

---

# LibSWBF2 build fixes

`git am ../../LibSWBF2/... ` — from the submodule root:

```bash
cd LibSWBF2
git am ../Tools/patches/libswbf2-build-fixes.patch
```

Contents:

- **`Hashing.h` / `InternalHelpers.cpp`** — add `<string>` and `<algorithm>`.
  Both were relying on transitive includes that do not hold on every standard
  library, so the build fails outright on some toolchains.
- **`CMakeLists.txt`** — link the MSVC runtime statically (`/MT`).

## Why static CRT matters

CMake defaults to `/MD` on MSVC, which leaves `LibSWBF2.dll` load-time
dependent on `MSVCP140.dll` and `VCRUNTIME140.dll`. Those ship with the Visual
C++ redistributable, **not** with Windows. On a machine that has never had
Visual Studio installed the DLL cannot be loaded, and .NET reports this as a
bare `DllNotFoundException` naming `LibSWBF2` — never naming the runtime DLL
that is actually missing.

This is consistent with the `DllNotFoundException` reported by the importer's
Windows users: immediate, Windows-only, and "fixed" by installing the Windows
SDK or Visual Studio — both of which simply drop the redistributable onto the
machine as a side effect. `/MT` removes the dependency entirely.

`CMAKE_MSVC_RUNTIME_LIBRARY` is set before `add_subdirectory` so the bundled
`fmt` and `glm` targets use the same runtime; mixing `/MT` and `/MD` in one
link is a hard error (LNK2038).

Verified by configuring and inspecting the generated projects: both
`LibSWBF2.vcxproj` and `fmt.vcxproj` resolve to `MultiThreaded` /
`MultiThreadedDebug`, with no target left on the DLL runtime. **Not yet
verified by a full compile and load test on a clean Windows machine** — that is
the test that would actually close out the bug.

### Unrelated snag when configuring with CMake 4.x

The bundled `ThirdParty/glm` declares `cmake_minimum_required` below 3.5, which
CMake 4 rejects. This predates our changes. Work around it with:

```bash
cmake -S LibSWBF2 -B build -DCMAKE_POLICY_VERSION_MINIMUM=3.5
```

---

# LVLImport submodule patches

`UnityProject/Assets/LVLImport` is a git submodule pointing at
[WHSnyder/SWBF2-.lvl-Extraction-Tools-for-Unity3D](https://github.com/WHSnyder/SWBF2-.lvl-Extraction-Tools-for-Unity3D).
Fixes made to the loaders while debugging Phoenix live inside that submodule,
which means they are **not** carried by a commit to this repository. This
directory keeps them as patches so they can be restored in any clone.

## Why the submodule is fragile here

The pinned commit is `8dd7de0`, and it is not reachable from any published
branch of the upstream repository — it has diverged 212 commits in each
direction from `origin/runtime`, the branch named in `.gitmodules`. A commit
that no branch contains can be garbage-collected upstream at any time, at which
point `git submodule update --init` fails outright and the project will not
build from a fresh clone.

Committing our fixes inside the submodule does not solve this: the resulting
commit exists only on this machine, so pointing the parent repo at it would
break a fresh clone in a different way.

**The durable fix is to fork the upstream repository and re-point
`.gitmodules` at the fork.** That is a decision about where the project's
dependencies live, so it is left to the maintainer rather than made silently.

## Restoring the loader fixes

The submodule carries these on a local branch `phoenix-fixes` (commit
`e521dda`). If that branch is lost — a fresh clone, or `git submodule update`
resetting to the pinned commit — reapply:

```bash
cd UnityProject/Assets/LVLImport
git am ../../../Tools/patches/lvlimport-phoenix-fixes.patch
```

Or, to apply without creating a commit:

```bash
cd UnityProject/Assets/LVLImport
git apply ../../../Tools/patches/lvlimport-phoenix-fixes.patch
```

## What the patch contains

| File | Change |
|---|---|
| `EffectsLoader.cs` | Match `MinMaxCurve` modes across x/y/z of the velocity module. Unity rejects a vector module whose axes mix constant and curve modes and silently drops the velocity, producing the `Particle Velocity curves must all be in the same mode` spam. |
| `MaterialLoader.cs` | Explicit error plus a lit-material fallback when the terrain material has no shader, instead of rendering solid magenta with nothing in the log. |
| `ModelLoader.cs` | Report zero-filled collision primitives once per model at Log level rather than warning per primitive. |
| `TextureLoader.cs` | Do not warn for the `noIcon` sentinel, which is BF2's marker for "deliberately has no icon". |
