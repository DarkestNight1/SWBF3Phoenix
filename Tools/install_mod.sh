#!/usr/bin/env bash
# Install a SWBF2 mod (e.g. Battlefront 3 Legacy 3.1) into the game's addon
# folder. Accepts either an extracted folder OR a .zip/.7z archive.
#
#   ./install_mod.sh <mod.zip | extracted-mod-dir> [bf2-game-dir]
#   ./install_mod.sh --list [bf2-game-dir]
#
# With no game dir given, common Steam/GOG locations are probed.
set -euo pipefail

probe_paths=(
  "$HOME/.steam/steam/steamapps/common/Star Wars Battlefront II"
  "$HOME/.local/share/Steam/steamapps/common/Star Wars Battlefront II"
  "$HOME/Library/Application Support/Steam/steamapps/common/Star Wars Battlefront II"
  "/c/Program Files (x86)/Steam/steamapps/common/Star Wars Battlefront II"
  "/c/GOG Games/Star Wars - Battlefront II"
)

is_game_dir() { [[ -f "$1/GameData/data/_lvl_pc/common.lvl" ]]; }

find_game_dir() {
  local given="${1:-}"
  if [[ -n "$given" ]] && is_game_dir "$given"; then echo "$given"; return 0; fi
  # also read extra Steam libraries from libraryfolders.vdf
  for steam in "$HOME/.steam/steam" "$HOME/.local/share/Steam" "/c/Program Files (x86)/Steam"; do
    local vdf="$steam/steamapps/libraryfolders.vdf"
    [[ -f "$vdf" ]] || continue
    while IFS= read -r lib; do
      probe_paths+=("$lib/steamapps/common/Star Wars Battlefront II")
    done < <(grep -oE '"path"[[:space:]]+"[^"]+"' "$vdf" 2>/dev/null | sed -E 's/.*"path"[[:space:]]+"([^"]+)".*/\1/' | sed 's/\\\\/\//g')
  done
  for p in "${probe_paths[@]}"; do
    if is_game_dir "$p"; then echo "$p"; return 0; fi
  done
  return 1
}

# ---- --list mode ----
if [[ "${1:-}" == "--list" ]]; then
  GAME_DIR="$(find_game_dir "${2:-}")" || {
    echo "ERROR: no BF2 install found. Pass it: ./install_mod.sh --list \"/path/to/game\"" >&2; exit 1; }
  ADDON="$GAME_DIR/GameData/addon"
  echo "Addon folder: $ADDON"
  [[ -d "$ADDON" ]] || { echo "(none installed yet)"; exit 0; }
  found=0
  for d in "$ADDON"/*/; do
    [[ -d "$d" ]] || continue
    name="$(basename "$d")"
    if   [[ -f "$d/addme.script"     ]]; then echo "  [enabled ] $name"; found=1
    elif [[ -f "$d/addme.script.off" ]]; then echo "  [disabled] $name"; found=1
    else                                      echo "  [no addme] $name"; found=1
    fi
  done
  [[ "$found" == 1 ]] || echo "(none installed yet)"
  exit 0
fi

SRC="${1:?Usage: install_mod.sh <mod.zip | extracted-mod-dir> [bf2-game-dir]}"
GAME_DIR="$(find_game_dir "${2:-}")" || {
  echo "ERROR: could not find a valid BF2 install. Pass it explicitly:" >&2
  echo "  ./install_mod.sh \"$SRC\" \"/path/to/Star Wars Battlefront II\"" >&2
  exit 1; }

# ---- extract archives to a temp dir ----
CLEANUP=""
trap '[[ -n "$CLEANUP" ]] && rm -rf "$CLEANUP"' EXIT

if [[ -f "$SRC" ]]; then
  TMP="$(mktemp -d)"; CLEANUP="$TMP"
  case "$SRC" in
    *.zip)
      command -v unzip >/dev/null || { echo "ERROR: 'unzip' not installed." >&2; exit 1; }
      echo "Extracting $(basename "$SRC")..."
      unzip -q "$SRC" -d "$TMP" ;;
    *.7z)
      command -v 7z >/dev/null || { echo "ERROR: '7z' (p7zip) not installed." >&2; exit 1; }
      echo "Extracting $(basename "$SRC")..."
      7z x -y -o"$TMP" "$SRC" >/dev/null ;;
    *)
      echo "ERROR: unsupported archive '$SRC' (use .zip, .7z, or an extracted folder)." >&2
      exit 1 ;;
  esac
  MOD_DIR="$TMP"
else
  MOD_DIR="$SRC"
fi

[[ -d "$MOD_DIR" ]] || { echo "ERROR: '$MOD_DIR' is not a folder." >&2; exit 1; }

ADDON="$GAME_DIR/GameData/addon"
mkdir -p "$ADDON"

# Find every addon folder anywhere in the tree (releases nest differently),
# so the user doesn't have to know the archive's internal layout.
installed=0
while IFS= read -r script; do
  d="$(dirname "$script")"
  name="$(basename "$d")"
  rm -rf "${ADDON:?}/$name"
  cp -r "$d" "$ADDON/"
  echo "Installed: $name"
  installed=$((installed + 1))
done < <(find "$MOD_DIR" -maxdepth 4 -iname 'addme.script' 2>/dev/null)

if [[ "$installed" == 0 ]]; then
  echo "ERROR: no addme.script found under '$SRC'." >&2
  echo "That file marks a SWBF2 addon - make sure this is a mod download." >&2
  exit 1
fi

echo
echo "Done - $installed mod folder(s) into: $ADDON"
echo "Load order (optional): create '$ADDON/modorder.txt', one folder per line, '!name' to disable."
echo "Verify with: ./install_mod.sh --list"
