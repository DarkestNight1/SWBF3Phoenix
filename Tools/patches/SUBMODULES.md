# Submodule publishing status

**A fresh `git clone --recurse-submodules` of this repository does not work.**
This file records exactly why, so the fix can be made deliberately.

## The problem

`.gitmodules` points at the upstream projects, but the commits this repository
pins do not exist there:

```
$ git fetch --depth 1 origin a1bc1c46b91b8e2a7a74bffe3ce5de2ba8a748d2
fatal: remote error: upload-pack: not our ref a1bc1c46b91b8e2a7a74bffe3ce5de2ba8a748d2
```

| Submodule | `.gitmodules` points at | Local-only commits | Shares history with upstream? |
|---|---|---|---|
| `LibSWBF2` | `Ben1138/LibSWBF2` | yes | yes |
| `UnityProject/Assets/LVLImport` | `WHSnyder/SWBF2-.lvl-Extraction-Tools-for-Unity3D` | 222 | **no — no common ancestor** |
| `UnityProject/Assets/CraUnity` | `Ben1138/CraUnity` | yes | yes |
| `lua5.0-swbf2-x64` | `Ben1138/lua5.0-swbf2-x64` | yes | yes |

LVLImport is the awkward one. `git merge-base` finds no common ancestor with
`origin/runtime`, `origin/main` or `origin/dev`, so it is not a fork that
drifted — it is a separate history that happens to contain similar files. A
patch against upstream cannot be produced, and an upstream pull request is not
possible.

## Options

1. **Publish each submodule to a repository you own** and re-point
   `.gitmodules` at those. Keeps the submodule structure; needs four new repos.
2. **Vendor them** — delete the submodules and commit their files directly into
   this repository. Simplest thing that works, loses upstream merge tracking.
   For LVLImport that loses nothing, because there is no shared history to
   merge with in the first place.

Until one of those happens, the patches beside this file are the only portable
copy of the native work, and they cover `LibSWBF2`, `CraUnity` and
`lua5.0-swbf2-x64` only.

## Patches

Regenerated against the newest upstream-reachable ancestor of each submodule:

| Patch | Submodule |
|---|---|
| `libswbf2-phoenix.patch` | `LibSWBF2` |
| `craunity-phoenix.patch` | `UnityProject/Assets/CraUnity` |
| `lua50-phoenix.patch` | `lua5.0-swbf2-x64` |

Apply from inside the submodule with `git am ../../Tools/patches/<file>`.

The older `libswbf2-build-fixes.patch`, `lua50-static-crt.patch` and
`lvlimport-phoenix-fixes.patch` are superseded and no longer apply cleanly;
they are kept only as a record of what was fixed when.
