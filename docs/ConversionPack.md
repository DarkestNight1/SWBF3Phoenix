# Battlefront Conversion Pack support

Native support for the community
[Star Wars Battlefront Conversion Pack](https://www.moddb.com/mods/star-wars-battlefront-conversion-pack)
(Gametoast: Teancum, Maveritchell and contributors) — the mod that brings the
maps of *Star Wars: Battlefront* (2004) that never made it into Battlefront II
back into it, together with a third era (Knights of the Old Republic), extra
game modes and a large amount of additional unit content.

As with every mod, **you supply the pack yourself**. Phoenix distributes no
mod assets, and nothing here modifies the pack's files.

Runtime code: `UnityProject/Assets/Runtime/BF3Legacy/Mods/PhxConversionPackContent.cs`.

## Installing

```bash
# Windows PowerShell (or double-click InstallMod.bat and drag the folder onto it)
.\Tools\install_mod.ps1 -ModPath "$env:USERPROFILE\Downloads\ConversionPack"

# Linux / macOS / Git Bash
./Tools/install_mod.sh ~/Downloads/ConversionPack
```

The pack installs as a **single addon folder**, `GameData/addon/BF1`, and the
installer picks it up the same way it picks up any addon: by finding the
`addme.script` inside it. Uninstalling is deleting that folder.

Order matters and the installer cannot fix it for you: **install 2.0 first,
then the 2.2 patch over it.** The pack's own install check is whether
`GameData/addon/BF1/data/_LVL_PC/SIDE/patch.lvl` and `patch2.lvl` exist — every
side its maps ask for comes out of those two files, so a 2.0-only or
half-extracted install produces maps with no units. Phoenix checks the same
thing and says so, at install time:

```
  [enabled ] BF1 - Battlefront Conversion Pack
    WARNING: SIDE/patch.lvl or SIDE/patch2.lvl is missing. Install Conversion
             Pack 2.0 first, then the 2.2 patch over it - ...
```

and again at startup (`[ConversionPack]` lines in the Unity console / player
log). `./Tools/install_mod.sh --list` (`-List` on Windows) reprints it any
time.

**You do not need** the Unofficial 1.3 Patch. It exists to work around the
retail executable — in particular to make mod-defined modes selectable at all.
Phoenix reimplements the engine and reads a map's declared modes and eras
directly.

## What Phoenix does for the pack

The pack registers its content through the stock addon contract, so it needs
no loader of its own. What it needed was the three things the retail shell
would have given it and Phoenix's re-implemented shell did not:

| | Problem | Fix |
|---|---|---|
| 1 | **Its data was not found on Linux.** The pack ships the casing its tools wrote (`data/_LVL_PC/SIDE/patch.lvl`); the runtime asks in lower case. That resolves on Windows and does not on a case-sensitive file system, so every addon's data root "did not exist" and its maps fell back to stock data or failed to load. | `PhxPath.ResolveCaseInsensitive` / `OnDisk`, used by the level scheduler (`PhxEnvironment.Schedule`) and by the addon data root in `PhxGame.EnterSWBF2Map`. Exact match is still tried first, so nothing changes on Windows. |
| 2 | **Its names were not readable.** The main menu was built from stock data only, so nothing a mod authored was in scope while the mission list was on screen — every modded map was listed by its raw script id. | `PhxGame.MountAddonShellData` mounts each addon's `core.lvl` into the shell environment, which is where a SWBF2 mod puts its Localize chunks and UI textures. Stock data is mounted first and keeps precedence. |
| 3 | **Its eras and modes were not selectable.** Battlefront II's shell only expands the modes and eras it ships with, so the pack's third era and its extra modes were dropped and its maps offered nothing playable. | The mission list reads map flags directly (`PhxBF3LegacyContent.ExpandEras` / `ExpandModes`), and `PhxConversionPackContent` supplies the name, blurb and icon those flags resolve against when the pack itself supplies none. |

On top of that:

- **Detection.** `PhxModManager` recognizes the pack by folder name (`BF1`, or
  any folder named after the mod) or by its `SIDE/patch.lvl` + `patch2.lvl`
  fingerprint, so a renamed or repacked install is still recognized.
  `PhxModManager.IsConversionPackInstalled` exposes it.
- **Install diagnostics** — the missing-side-patch and missing-`mission.lvl`
  cases above, plus a warning when the pack is installed and enabled but
  registered no maps at all, which means its `addme.script` did not run to
  completion (the Lua error above that line is the reason).
- **Map classification.** Which addon folder registered a mission script is
  known (`PhxGame.GetAddonFolderForScript`), so the pack's maps are identified
  by ownership rather than by guessing at ids — ids collide between mods. The
  world is then read from the id's leading three letters, which is the SWBF
  convention (`rhn1c_con` → Rhen Var). This drives the fallback display name
  and the vertical battlefront layer, so an interior map does not get capital
  ships hung over it.

## XL mode, team sizes and tickets

The fork raises stock BF2's small squads: `bf3legacy.json` sets `TeamSize` (32
by default) and `Reinforcements` (400), which the runtime applied *over* what
the mission script asked for. That is right for a stock map written for 8–16 a
side, and wrong for a mode whose entire content is a bigger army — the pack's
**XL** mode is nothing but a raised unit count, so clamping it to `TeamSize`
deleted the mode while leaving it selectable.

Both settings are now a **floor, not a cap**: a script asking for more keeps its
own number, and the console says so
(`the mission asks for N units, more than the configured team size…`). Stock
maps are unaffected — they ask for less, so the configured value still applies.
Set `TeamSize` / `Reinforcements` to `0` to play every map exactly as its script
wrote it.

## Names, ids and the parts that are your install's business

The pack has been released and repacked more than once, and which letter it
chose for its era, or which id it gave a map, is a property of the copy you
installed rather than something worth hard-coding. So the precedence is:

1. **The pack's own strings** — read from its `core.lvl` once the shell mounts
   it. Whatever it ships, in your language, wins.
2. **Your corrections** in `conversionpack.json` (see below).
3. **Built-in fallbacks** — a small table for the era and modes the pack is
   documented to add (Knights of the Old Republic; XL, Classic Conquest, Hero
   Assault), and planet names read from map ids.
4. **A generated label**, for anything still unnamed. An unrecognized mode or
   era is always still *offered* — dropping it would make a playable
   combination unreachable — and it is logged with its flag key so you can
   name it.

### `conversionpack.json`

Written next to `bf3legacy.json` (Unity's persistent data path, or the install
root for a self-contained build) the first time the pack is detected. It ships
with **placeholder** entries — they show the shape and change nothing until you
edit them, because a file written by the runtime should not assert things about
your install. Filled in, it looks like:

```json
{
    "Comment": "...",
    "Modes": [
        { "Key": "era_k", "Name": "Knights of the Old Republic", "About": "", "Icon": "" }
    ],
    "Maps": [
        { "Id": "rhn1", "Name": "Rhen Var: Harbor", "Planet": "Rhen Var", "Layer": "Ground" }
    ]
}
```

- `Modes` — `Key` is the mission list flag exactly as the map declares it
  (`era_k`, `mode_xl`). Applies to eras and modes alike. `Icon` is a UI texture
  name from any mounted `.lvl`; leave it empty for no icon.
- `Maps` — `Id` is the map id without the era letter (`rhn1`, not `rhn1c_con`).
  `Layer` is `Ground`, `Space` or `Interior` and decides whether the vertical
  battlefront hangs capital ships over the map (`Interior` means never).

Delete an entry to fall back to the built-in value. A name you set here is not
overwritten by anything discovered later.

## Limitations

- **Galactic Conquest is not implemented in Phoenix** — for stock BF2 or for
  any mod. The pack's separate *KotOR Galactic Conquest* download therefore has
  no effect here. Instant Action is where its content lives.
- The pack's maps run on the same runtime as everything else, so the open
  items in [BF2Compatibility.md](BF2Compatibility.md) and
  [BF2DataFeatures.md](BF2DataFeatures.md) apply to them too — a mod map cannot
  use an ODF class the runtime does not implement yet.
- The built-in era/mode fallback table is exactly that: a fallback, listed
  above. If your copy names its era something the table does not cover, the era
  is still selectable and the log line tells you which key to put in
  `conversionpack.json`.
- BF3 Legacy features (vertical battlefront, weather, dismemberment, modern AI)
  apply to the pack's maps as they do to any addon map, and are toggleable in
  `bf3legacy.json`.
