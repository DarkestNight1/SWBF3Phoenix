# Installing SWBF3 Phoenix — quickstart

You need **your own copy of Star Wars Battlefront II (2005)** (Steam, GOG or
retail). No game files are included or distributed by this project.

## TL;DR — double-click `InstallMod.bat`

That's the whole install. It finds your Battlefront II install, finds the mod
download (Downloads, Desktop, Documents, or next to the repo), installs every
addon folder in it, **and points the Unity project at the game**. Then open
`UnityProject`, open `Runtime/Scenes/PhxMainScene`, press Play. No scene fields
to fill in, no config to hand-write.

- **Mod somewhere unusual?** Drag the extracted mod folder onto `InstallMod.bat`.
- **Re-running is safe** — components already installed are skipped, so it
  won't re-copy gigabytes. Type `FORCE` as an argument to reinstall anyway.
- **No mod, just editor setup?** Double-click it with no mod present; it still
  wires Unity to your game.

Prefer a terminal? `Tools\install_mod.ps1` is what the .bat calls, with more
switches (`-Link`, `-Verify`, `-List`, `-GameDir`); `Tools/install_mod.sh` is
the Linux/macOS equivalent.

Check the result any time:

```bash
.\Tools\install_mod.ps1 -Verify
```

```
Game:   C:\Users\you\Desktop\GOG Games\Star Wars - Battlefront 2
  All 6 required lvl files present.
Addon:  C:\Users\you\Desktop\GOG Games\Star Wars - Battlefront 2\GameData\addon
  [enabled ] BF3 - BF3 Legacy: Pre-Demo 3.0 (main mod)
  [enabled ] BF3Era - BF3 Legacy: Era Mod 1.4
  ...
  BF3 Legacy: 7/7 components enabled.
Unity:  configured - GamePathOverride = C:/Users/you/Desktop/GOG Games/Star Wars - Battlefront 2
```

## 1. Point Phoenix at the game — usually nothing to do

On startup Phoenix auto-detects BF2 across Steam (including extra library
folders from `libraryfolders.vdf`), GOG, retail and manual copies. It tries
every folder name the various installers use — Steam's
`Star Wars Battlefront II`, GOG's `Star Wars - Battlefront 2` and others — under
your user folders (including the Desktop), Program Files and every fixed drive,
and looks one level inside `GOG Games` / `Games` libraries so a renamed install
is still found.

The installer scripts use the same search, and write the result to
`bf3legacy.json` as `GamePathOverride`. That is exactly where the runtime looks
when the scene's `Game Path String` is empty — which is why installing a mod
also finishes your editor setup.

If nothing is found, **you get a setup panel in-game** listing every location
it checked, with a box to paste your install folder. It validates the path,
remembers it, and starts the game.

To wire up Unity without installing any mod:

```bash
.\Tools\install_mod.ps1                # or:  ./Tools/install_mod.sh --setup
```

## 2. Install mods — e.g. Battlefront 3 Legacy 3.1

Point the helper at the download; it handles `.zip` archives directly and
finds the addon folders inside whatever layout the release uses. The BF3
Legacy 3.1 pack is seven separate addon folders — all of them get installed.

```bash
# Windows PowerShell — .zip or an extracted folder
.\Tools\install_mod.ps1 -ModPath "$env:USERPROFILE\Downloads\Battlefront3Legacy3.1Demo"

# Linux / macOS / Git Bash — .zip, .7z, or an extracted folder
./Tools/install_mod.sh ~/Downloads/Battlefront3Legacy3.1Demo
```

**Link instead of copy.** The 3.1 pack is ~19 GB. If the download is staying
put, link it instead — instant, no extra disk, and you can edit mod files in
place while debugging:

```bash
.\Tools\install_mod.ps1 -ModPath "...\Battlefront3Legacy3.1Demo" -Link
./Tools/install_mod.sh --link ~/Downloads/Battlefront3Legacy3.1Demo
```

Uninstall by deleting the link; the download is untouched. Two caveats: don't
delete or move the download afterwards, and toggling a mod off renames
`addme.script` *inside the original download*.

Check what's installed at any time:

```bash
.\Tools\install_mod.ps1 -List          # or:  ./Tools/install_mod.sh --list
```

Doing it by hand works too — a mod is just a folder in
`<BF2>/GameData/addon/` containing an `addme.script`.

**You do not need** the Unofficial 1.3 Patch or [GT]Anakin's UI Remaster that
the pack's readme asks for. Both work around the retail executable; Phoenix
reimplements the engine and supplies the shell helpers the mod needs. See
[docs/BF3Legacy.md](docs/BF3Legacy.md#running-the-bf3-legacy-31-pack).

**Battlefront Conversion Pack.** The same command installs it (it is one addon
folder, `BF1`). Install **2.0 first, then the 2.2 patch over it** — the
installer and the runtime both check for the pack's `SIDE/patch.lvl` and
`patch2.lvl` and warn if they are missing, which is what a half-finished
install looks like. Its Knights of the Old Republic era and its extra game
modes are selectable without the 1.3 patch; Galactic Conquest is not
implemented in Phoenix, so the pack's separate KotOR GC download does nothing
here. See [docs/ConversionPack.md](docs/ConversionPack.md).

**Load order / disabling:** create `<BF2>/GameData/addon/modorder.txt` — one
folder name per line, `!foldername` to disable one, `#` for comments. This is
also the fastest way to bisect a mod that breaks a map.

## 3. Configure BF3 Legacy features (optional)

First launch writes `bf3legacy.json` into Unity's persistent data path (next
to `Player.log`). Everything is toggleable there: capital ships,
dismemberment, AI difficulty (0 Classic – 3 Legendary), weather, texture
upscaling, 4K mode, graphics enhancements.

Useful knobs if performance is tight:

| Setting | Effect |
|---|---|
| `UpscaleTextures` / `UpscaleFactor` | Texture upscaling; drop to `false` or factor `2` first |
| `UpscaleBudgetMB` | Cap on extra VRAM used by upscaled textures |
| `GraphicsEnhancements` | TAA, motion blur, DoF, PBR sky, reflection probe |
| `UseDynamicResolution` | Let HDRP drop internal resolution to hold framerate |
| `AIDifficulty` | 0 Classic, 1 Veteran, 2 Elite, 3 Legendary |

## 4. Self-contained install inside the game folder

Double-click **`BuildPhoenix.bat`**. It builds a standalone player directly into
your Battlefront II install:

```
Star Wars - Battlefront 2/
    BattlefrontII.exe                <- the original game, untouched
    SWB2Launcher.exe                 <- original launcher, untouched
    GameData/                        <- shared data, and addon/ mods
    Phoenix/
        Phoenix.exe                  <- Phoenix + BF3 Legacy
        Phoenix_Data/ ...
        bf3legacy.json               <- written on first run
    Play Phoenix (BF3 Legacy).lnk    <- shortcut to Phoenix.exe
```

Two ways to play, side by side: `BattlefrontII.exe` for the stock 2005 game,
the shortcut for Phoenix. Neither knows about the other, and both read the same
`GameData` — so mods installed with `InstallMod.bat` show up in both.

**It really is self-contained.** The player locates `GameData` by walking up
from its own folder (`PhxGamePathDetector.TryDetectPortable`), so it needs no
configuration, no registry entries and no AppData. Its config is written next
to the executable rather than in `LocalLow`, and it falls back to `LocalLow`
only if the folder isn't writable (e.g. an install under `Program Files`).
Deleting `Phoenix/` and the shortcut removes every trace.

Requires the native libraries first (`BuildAndCopyLibsWin.bat`, see below) and
Unity installed. The build is Mono, not IL2CPP — plain "Windows Build Support"
is enough.

From the editor instead: **Phoenix > Build Self-Contained Player...**

## 5. Building the native libs on a modern toolchain

`BuildAndCopyLibsWin.bat` / `BuildAndCopyLibsUnix.sh` need the submodules, so
run this first if you cloned without `--recurse-submodules`:

```bash
git submodule update --init --recursive
```

Two things bite on current toolchains, both handled or noted here:

- **CMake 4.x** removed compatibility with `cmake_minimum_required(VERSION < 3.5)`,
  and the bundled `glm` (3.2) and `fmt` (3.1) still declare it — configure fails
  outright. Both build scripts now detect CMake ≥ 4 and pass
  `-DCMAKE_POLICY_VERSION_MINIMUM=3.5`, which is the supported escape hatch.
  Nothing changes on CMake 3.x.
- **MSVC 14.4x** (VS2022 17.14+) no longer pulls `<string>` and `<algorithm>`
  in transitively, so LibSWBF2 fails to compile with
  `'string' is not a valid template type argument` (`Hashing.h`) and
  `'transform': is not a member of 'std'` (`InternalHelpers.cpp`). Add the
  missing `#include <string>` and `#include <algorithm>` to those two files.
  This is an upstream LibSWBF2 issue — the fix lives in the submodule, so
  `git submodule update` reverts it.

## 6. "Unity has not been activated" — even though Hub shows a licence

Symptom: `BuildPhoenix.bat` fails, and the Unity log contains

```
[LicensingClient] Error: Code 10 while verifying Licensing Client signature
[Licensing::Module] Error: LicensingClient has failed validation
BatchMode: Unity has not been activated with a valid License.
```

This is **not** a missing licence. Unity Hub 3.x ships a licensing client that
editors from the 2020.3 era don't trust, so the editor refuses a licence that is
sitting right there in
`%LOCALAPPDATA%\Unity\licenses\UnityEntitlementLicense.xml`.

Two ways around it:

**Build from the open editor** — *Phoenix > Build Self-Contained Player…*. The
GUI path doesn't depend on headless licensing.

**Or activate manually**, which bypasses the licensing client by writing a
legacy `C:\ProgramData\Unity\Unity_lic.ulf`:

```bash
.\Tools\unity_manual_activation.ps1 -Create
```

Upload the printed `.alf` at <https://license.unity3d.com/manual>, download the
`.ulf` it returns, then:

```bash
.\Tools\unity_manual_activation.ps1 -Apply "<path to .ulf>"
```

Headless builds work after that. (A newer 2020.3.x patch release also handles
the modern licensing client, but changes the project's editor version.)

## 7. Troubleshooting

- **Black screen / "Invalid game path"** — the setup panel should appear; if
  it doesn't, check `Player.log` for `[BF3Legacy]` lines.
- **Mod maps missing** — run `install_mod.sh --list` to confirm the folder
  landed with its `addme.script`, then check the console for addon
  registration warnings on startup.
- **Full debugging workflow** (editor console, log locations, booting straight
  into a map) — see "Debugging mods and maps" in
  [docs/BF3Legacy.md](docs/BF3Legacy.md).
