# Installing SWBF3 Phoenix — quickstart

You need **your own copy of Star Wars Battlefront II (2005)** (Steam, GOG or
retail). No game files are included or distributed by this project.

## 1. Point Phoenix at the game — usually nothing to do

On startup Phoenix auto-detects BF2 in the standard Steam/GOG/retail
locations, including extra Steam library folders (read from
`libraryfolders.vdf`).

If it can't find your install, **you get a setup panel in-game** listing every
location it checked, with a box to paste your install folder. It validates the
path, remembers it in `bf3legacy.json`, and starts the game — no scene editing,
no config files to hand-write.

## 2. Install mods (optional) — e.g. Battlefront 3 Legacy 3.1

Point the helper at the download; it handles `.zip` archives directly and
finds the addon folders inside whatever layout the release uses:

```bash
# Linux / macOS / Git Bash — .zip, .7z, or an extracted folder
./Tools/install_mod.sh ~/Downloads/BF3LegacyMod31.zip

# Windows PowerShell — .zip or an extracted folder
.\Tools\install_mod.ps1 -ModPath "$env:USERPROFILE\Downloads\BF3LegacyMod31.zip"
```

Check what's installed at any time:

```bash
./Tools/install_mod.sh --list          # or:  .\Tools\install_mod.ps1 -List
```

```
Addon folder: /.../GameData/addon
  [enabled ] BF3Legacy
  [disabled] SomeOtherMod
```

Doing it by hand works too — a mod is just a folder in
`<BF2>/GameData/addon/` containing an `addme.script`.

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

## 4. Troubleshooting

- **Black screen / "Invalid game path"** — the setup panel should appear; if
  it doesn't, check `Player.log` for `[BF3Legacy]` lines.
- **Mod maps missing** — run `install_mod.sh --list` to confirm the folder
  landed with its `addme.script`, then check the console for addon
  registration warnings on startup.
- **Full debugging workflow** (editor console, log locations, booting straight
  into a map) — see "Debugging mods and maps" in
  [docs/BF3Legacy.md](docs/BF3Legacy.md).
