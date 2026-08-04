# Installing SWBF3 Phoenix — quickstart

You need: **your own copy of Star Wars Battlefront II (2005)** (Steam, GOG or
retail) and this project built once (see README for the full build steps).

## 1. Point Phoenix at the game (usually automatic)

On startup Phoenix now **auto-detects** BF2 installs in the standard
Steam/GOG/retail locations (including extra Steam library folders). If your
install is somewhere unusual, set `Game Path String` on the *Game* object in
`PhxMainScene` (or in the built player's settings) to the folder that
contains `GameData/`.

## 2. Install mods (optional) — e.g. Battlefront 3 Legacy 3.1

Mods are plain addon folders. Either drop the extracted mod into
`<BF2>/GameData/addon/` yourself, or use the helper:

```bash
# Linux / macOS / Git Bash
./Tools/install_mod.sh ~/Downloads/BF3LegacyMod31

# Windows PowerShell
.\Tools\install_mod.ps1 -ModDir "$env:USERPROFILE\Downloads\BF3LegacyMod31"
```

The script finds your BF2 install, copies every folder that contains an
`addme.script`, and tells you where things landed. Load order and disabling
are controlled by `GameData/addon/modorder.txt` (one folder per line,
`!folder` disables, `#` comments).

## 3. Configure BF3 Legacy features (optional)

First launch writes `bf3legacy.json` (in Unity's persistent data path, next
to `Player.log`). Toggle capital ships, dismemberment, AI difficulty
(0 Classic – 3 Legendary), weather, 4K mode and more there.

## 4. Troubleshooting

See "Debugging mods and maps" in [docs/BF3Legacy.md](docs/BF3Legacy.md).
The short version: run in the Unity editor and read the Console — every
addon registration, load failure and `[BF3Legacy]` system logs there.
