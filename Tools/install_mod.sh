#!/usr/bin/env bash
# Install a SWBF2 mod (e.g. Battlefront 3 Legacy 3.1) into the game's addon
# folder AND point the Unity project at the game, so opening the editor and
# pressing Play needs no further setup.
#
#   ./install_mod.sh <mod.zip | mod.7z | extracted-mod-dir> [bf2-game-dir]
#   ./install_mod.sh --link <extracted-mod-dir> [bf2-game-dir]
#   ./install_mod.sh --list [bf2-game-dir]
#   ./install_mod.sh --verify [bf2-game-dir]
#   ./install_mod.sh --setup [bf2-game-dir]     # Unity wiring only, no mod
#
# --link symlinks instead of copying. The BF3 Legacy 3.1 pack is ~19 GB; a
# symlink is instant, uses no extra disk, and lets you edit the mod in place
# while debugging. Remove the link to uninstall; the download is untouched.
#
# With no game dir given, common Steam/GOG/retail locations are probed.
set -euo pipefail

# Installers disagree on the folder name - Steam uses roman numerals, GOG ships
# "Star Wars - Battlefront 2". Keep in sync with PhxGamePathDetector.
install_names=(
  "Star Wars Battlefront II"
  "Star Wars - Battlefront II"
  "Star Wars Battlefront 2"
  "Star Wars - Battlefront 2"
  "STAR WARS Battlefront II"
  "Battlefront II"
  "SWBF2"
)
containers=("GOG Games" "GOG Galaxy/Games" "Games" "LucasArts")

# Folder name -> what it actually is, for the BF3 Legacy 3.1 pack. Mirrors
# PhxBF3LegacyContent.ComponentTable on the runtime side.
bf3_component() {
  case "$1" in
    BF3)             echo "Pre-Demo 3.0 (main mod)" ;;
    BF3Era)          echo "Era Mod 1.4" ;;
    BF3MoreMaps)     echo "MoreMaps Patch 1.9" ;;
    BF3GCWSpaceDemo) echo "GCW Space Demo" ;;
    BF3Vjun)         echo "Extended Engagements (Vjun/Sulon/Lucrehulk)" ;;
    BF3Venator)      echo "Venator" ;;
    BF3Cato-Hunt)    echo "Cato Neimoidia: Hunt" ;;
    *)               echo "" ;;
  esac
}
BF3_COMPONENT_COUNT=7

is_game_dir() { [[ -f "$1/GameData/data/_lvl_pc/common.lvl" ]]; }

search_roots() {
  echo "$HOME"
  echo "$HOME/Desktop"
  echo "$HOME/Documents"
  echo "$HOME/Downloads"
  echo "/c/Program Files (x86)"
  echo "/c/Program Files"
  # drive roots, when running under Git Bash / WSL
  for d in /c /d /e /f /mnt/c /mnt/d; do
    [[ -d "$d" ]] && echo "$d"
  done
  echo "/opt"
}

find_game_dir() {
  local given="${1:-}"
  if [[ -n "$given" ]] && is_game_dir "$given"; then echo "$given"; return 0; fi

  local -a probe=()

  # Steam: default roots plus every library in libraryfolders.vdf
  local steam_roots=(
    "$HOME/.steam/steam"
    "$HOME/.local/share/Steam"
    "$HOME/Library/Application Support/Steam"
    "/c/Program Files (x86)/Steam"
    "/c/Steam"
  )
  for steam in "${steam_roots[@]}"; do
    for n in "${install_names[@]}"; do
      probe+=("$steam/steamapps/common/$n")
    done
    local vdf="$steam/steamapps/libraryfolders.vdf"
    [[ -f "$vdf" ]] || continue
    while IFS= read -r lib; do
      for n in "${install_names[@]}"; do
        probe+=("$lib/steamapps/common/$n")
      done
    done < <(grep -oE '"path"[[:space:]]+"[^"]+"' "$vdf" 2>/dev/null \
             | sed -E 's/.*"path"[[:space:]]+"([^"]+)".*/\1/' | sed 's/\\\\/\//g')
  done

  # GOG / retail / manual copies, plus one level inside game libraries so a
  # renamed install is still found.
  while IFS= read -r root; do
    [[ -d "$root" ]] || continue
    for n in "${install_names[@]}"; do
      probe+=("$root/$n")
    done
    for c in "${containers[@]}"; do
      for n in "${install_names[@]}"; do
        probe+=("$root/$c/$n")
      done
      if [[ -d "$root/$c" ]]; then
        while IFS= read -r child; do
          probe+=("$child")
        done < <(find "$root/$c" -maxdepth 1 -mindepth 1 -type d 2>/dev/null)
      fi
    done
  done < <(search_roots)

  for p in "${probe[@]}"; do
    if is_game_dir "$p"; then echo "$p"; return 0; fi
  done
  return 1
}

# ---- where the runtime reads its config ----
# PhxGame falls back to PhxGamePathDetector, which reads GamePathOverride from
# bf3legacy.json in Unity's persistent data path. Setting it there means the
# editor scene needs no edits - Game Path String stays empty and still works.
unity_config_path() {
  local repo settings company product
  repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
  settings="$repo/UnityProject/ProjectSettings/ProjectSettings.asset"
  company="Ben1138"; product="Phoenix"
  if [[ -f "$settings" ]]; then
    local c p
    c="$(grep -m1 -E '^\s*companyName:' "$settings" | sed -E 's/^\s*companyName:\s*//' || true)"
    p="$(grep -m1 -E '^\s*productName:' "$settings" | sed -E 's/^\s*productName:\s*//' || true)"
    [[ -n "$c" ]] && company="$c"
    [[ -n "$p" ]] && product="$p"
  fi

  case "$(uname -s)" in
    Darwin)          echo "$HOME/Library/Application Support/$company/$product/bf3legacy.json" ;;
    MINGW*|MSYS*|CYGWIN*)
                     echo "$HOME/AppData/LocalLow/$company/$product/bf3legacy.json" ;;
    *)               echo "$HOME/.config/unity3d/$company/$product/bf3legacy.json" ;;
  esac
}

set_unity_game_path() {
  local game="$1" cfg
  cfg="$(unity_config_path)"
  mkdir -p "$(dirname "$cfg")"

  if [[ ! -f "$cfg" ]]; then
    printf '{\n    "GamePathOverride": "%s"\n}\n' "$game" > "$cfg"
  elif grep -q '"GamePathOverride"' "$cfg"; then
    # preserve every other setting the user has tuned
    local escaped
    escaped="$(printf '%s' "$game" | sed -e 's/[\/&]/\\&/g')"
    sed -i.bak -E "s/(\"GamePathOverride\"[[:space:]]*:[[:space:]]*)\"[^\"]*\"/\1\"$escaped\"/" "$cfg"
    rm -f "$cfg.bak"
  else
    # insert right after the opening brace
    local escaped
    escaped="$(printf '%s' "$game" | sed -e 's/[\/&]/\\&/g')"
    sed -i.bak -E "0,/\{/s//{\n    \"GamePathOverride\": \"$escaped\",/" "$cfg"
    rm -f "$cfg.bak"
  fi
  echo "$cfg"
}

list_mods() {
  local addon="$1" found=0 known=0 have_base=0
  [[ -d "$addon" ]] || { echo "(none installed yet)"; return 0; }
  for d in "$addon"/*/; do
    [[ -d "$d" ]] || continue
    local name state comp link
    name="$(basename "$d")"
    if   [[ -f "$d/addme.script"     ]]; then state="enabled "
    elif [[ -f "$d/addme.script.off" ]]; then state="disabled"
    else                                      state="no addme"
    fi
    link=""
    [[ -L "${d%/}" ]] && link=" (linked)"
    comp="$(bf3_component "$name")"
    if [[ -n "$comp" ]]; then
      echo "  [$state] $name$link - BF3 Legacy: $comp"
      known=$((known + 1))
      [[ "$name" == "BF3" ]] && have_base=1
    else
      echo "  [$state] $name$link"
    fi
    found=1
  done
  [[ "$found" == 1 ]] || { echo "(none installed yet)"; return 0; }
  if [[ "$known" -gt 0 ]]; then
    echo "  BF3 Legacy: $known/$BF3_COMPONENT_COUNT components installed."
    [[ "$have_base" == 1 ]] || \
      echo "  WARNING: the main 'BF3' folder is missing; the others depend on it."
  fi
}

MODE="install"
LINK=0
case "${1:-}" in
  --list)   MODE="list";   shift ;;
  --verify) MODE="verify"; shift ;;
  --setup)  MODE="setup";  shift ;;
  --link)   LINK=1;        shift ;;
esac

if [[ "$MODE" == "list" || "$MODE" == "verify" || "$MODE" == "setup" ]]; then
  GAME_DIR="$(find_game_dir "${1:-}")" || {
    echo "ERROR: no BF2 install found. Pass it: ./install_mod.sh --$MODE \"/path/to/game\"" >&2; exit 1; }
  ADDON="$GAME_DIR/GameData/addon"

  if [[ "$MODE" == "setup" ]]; then
    cfg="$(set_unity_game_path "$GAME_DIR")"
    echo "Game found:   $GAME_DIR"
    echo "Unity set up: $cfg"
    echo
    echo "Open UnityProject, open Runtime/Scenes/PhxMainScene and press Play."
    exit 0
  fi

  if [[ "$MODE" == "verify" ]]; then
    echo "Game:   $GAME_DIR"
    missing=""
    for f in common.lvl core.lvl ingame.lvl inshell.lvl mission.lvl shell.lvl; do
      [[ -f "$GAME_DIR/GameData/data/_lvl_pc/$f" ]] || missing="$missing $f"
    done
    if [[ -n "$missing" ]]; then
      echo "  MISSING required lvl files:$missing"
    else
      echo "  All 6 required lvl files present."
    fi
    echo "Addon:  $ADDON"
    list_mods "$ADDON"
    cfg="$(unity_config_path)"
    if [[ -f "$cfg" ]] && grep -q '"GamePathOverride"' "$cfg"; then
      echo "Unity:  configured - $(grep -o '"GamePathOverride"[^,}]*' "$cfg")"
    else
      echo "Unity:  not configured. Run './install_mod.sh --setup' to set it up."
    fi
    exit 0
  fi

  echo "Addon folder: $ADDON"
  list_mods "$ADDON"
  exit 0
fi

SRC="${1:?Usage: install_mod.sh [--link] <mod.zip | mod.7z | extracted-mod-dir> [bf2-game-dir]}"
GAME_DIR="$(find_game_dir "${2:-}")" || {
  echo "ERROR: could not find a valid BF2 install. Pass it explicitly:" >&2
  echo "  ./install_mod.sh \"$SRC\" \"/path/to/Star Wars Battlefront II\"" >&2
  exit 1; }

# ---- extract archives to a temp dir ----
CLEANUP=""
trap '[[ -n "$CLEANUP" ]] && rm -rf "$CLEANUP"' EXIT

if [[ -f "$SRC" ]]; then
  if [[ "$LINK" == 1 ]]; then
    echo "ERROR: --link needs an extracted folder to point at, not an archive." >&2
    exit 1
  fi
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
  if [[ "$LINK" == 1 ]]; then
    ln -s "$(cd "$d" && pwd)" "$ADDON/$name"
    echo "Linked:    $name  ->  $(cd "$d" && pwd)"
  else
    echo "Copying:   $name..."
    cp -r "$d" "$ADDON/"
    echo "Installed: $name"
  fi
  installed=$((installed + 1))
done < <(find "$MOD_DIR" -maxdepth 4 -iname 'addme.script' 2>/dev/null)

if [[ "$installed" == 0 ]]; then
  echo "ERROR: no addme.script found under '$SRC'." >&2
  echo "That file marks a SWBF2 addon - make sure this is a mod download." >&2
  exit 1
fi

echo
echo "Done - $installed mod folder(s) into: $ADDON"
list_mods "$ADDON"

if [[ -n "$(bf3_component "$(basename "$(dirname "$(find "$MOD_DIR" -maxdepth 4 -iname 'addme.script' | head -1)")")")" ]]; then
  echo
  echo "You do NOT need the 1.3 patch or the UI Remaster the pack's readme asks for -"
  echo "Phoenix provides those shell helpers itself."
fi

cfg="$(set_unity_game_path "$GAME_DIR")"
echo
echo "Unity set up: $cfg"
echo "Open UnityProject, open Runtime/Scenes/PhxMainScene and press Play - no scene edits needed."
echo
echo "Load order (optional): create '$ADDON/modorder.txt', one folder per line, '!name' to disable."
echo "Verify with: ./install_mod.sh --verify"
