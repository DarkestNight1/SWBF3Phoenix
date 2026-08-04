#!/usr/bin/env bash
# Installs an extracted SWBF2 mod (e.g. Battlefront 3 Legacy 3.1) into the
# game's addon folder. Usage:
#   ./install_mod.sh <extracted-mod-dir> [bf2-game-dir]
#
# If the game dir is omitted, common Steam/GOG locations are probed.
set -euo pipefail

MOD_DIR="${1:?Usage: install_mod.sh <extracted-mod-dir> [bf2-game-dir]}"
GAME_DIR="${2:-}"

probe_paths=(
  "$HOME/.steam/steam/steamapps/common/Star Wars Battlefront II"
  "$HOME/.local/share/Steam/steamapps/common/Star Wars Battlefront II"
  "/c/Program Files (x86)/Steam/steamapps/common/Star Wars Battlefront II"
  "/c/GOG Games/Star Wars - Battlefront II"
)

if [[ -z "$GAME_DIR" ]]; then
  for p in "${probe_paths[@]}"; do
    if [[ -f "$p/GameData/data/_lvl_pc/common.lvl" ]]; then
      GAME_DIR="$p"
      break
    fi
  done
fi

if [[ -z "$GAME_DIR" || ! -f "$GAME_DIR/GameData/data/_lvl_pc/common.lvl" ]]; then
  echo "ERROR: could not find a valid BF2 install. Pass it explicitly:" >&2
  echo "  ./install_mod.sh <mod-dir> \"/path/to/Star Wars Battlefront II\"" >&2
  exit 1
fi

ADDON="$GAME_DIR/GameData/addon"
mkdir -p "$ADDON"

# A mod release may be a single addon folder (has addme.script) or a pack of
# several. Copy whichever layout we're given.
installed=0
if [[ -f "$MOD_DIR/addme.script" ]]; then
  cp -r "$MOD_DIR" "$ADDON/"
  echo "Installed: $(basename "$MOD_DIR")"
  installed=1
else
  for sub in "$MOD_DIR"/*/; do
    if [[ -f "$sub/addme.script" || -f "$sub/addme.script.off" ]]; then
      cp -r "$sub" "$ADDON/"
      echo "Installed: $(basename "$sub")"
      installed=1
    fi
  done
fi

if [[ "$installed" == 0 ]]; then
  echo "ERROR: no addme.script found in '$MOD_DIR' (or its subfolders)." >&2
  echo "Extract the mod download first, then point this script at it." >&2
  exit 1
fi

echo "Done. Addon folder: $ADDON"
echo "Tip: create '$ADDON/modorder.txt' to control load order ('!name' disables)."
