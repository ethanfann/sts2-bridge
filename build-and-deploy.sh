#!/usr/bin/env bash
set -euo pipefail

PROJECT_DIR="$(dirname "$(realpath "${BASH_SOURCE[0]}")")"
DIST_DIR="$PROJECT_DIR/dist/linux"
GAME_DIR="$HOME/.local/share/Steam/steamapps/common/Slay the Spire 2"
ACTION=build

usage() {
  cat <<'EOF'
Usage: ./build-and-deploy.sh [build|install|smoke] [--game-dir PATH]

  build    Build a DLL-only package in dist/linux (default; does not install).
  install  Install the existing dist/linux package into the game's mods folder.
  smoke    Test the existing package in a disposable, offline sandbox.

Requires a .NET 9 SDK for build, Python 3 and bubblewrap for smoke.
Use --game-dir for a non-default Steam library. Windows scripts are unchanged.
EOF
}

if [[ ${1:-} == build || ${1:-} == install || ${1:-} == smoke ]]; then
  ACTION="$1"
  shift
fi
while (($#)); do
  case "$1" in
    --game-dir)
      [[ $# -ge 2 && -n "$2" ]] || { usage >&2; exit 2; }
      GAME_DIR="$2"
      shift 2
      ;;
    -h|--help) usage; exit 0 ;;
    *) usage >&2; exit 2 ;;
  esac
done

require_file() {
  [[ -f "$1" ]] || { printf 'Missing file: %s\n' "$1" >&2; exit 1; }
}

require_file "$GAME_DIR/data_sts2_linuxbsd_x86_64/sts2.dll"
require_file "$GAME_DIR/data_sts2_linuxbsd_x86_64/0Harmony.dll"
GAME_DIR="$(realpath "$GAME_DIR")"

case "$ACTION" in
  build)
    if ! command -v dotnet >/dev/null || [[ -z "$(dotnet --list-sdks)" ]]; then
      printf 'A .NET 9 SDK is required. With mise: mise install dotnet@9; mise exec dotnet@9 -- ./build-and-deploy.sh build\n' >&2
      exit 1
    fi
    # Do not leave an older package installable if compilation fails.
    rm -f "$DIST_DIR/FirstMod.dll" "$DIST_DIR/FirstMod.json"
    dotnet build "$PROJECT_DIR/FirstMod.csproj" --configuration Release \
      "-p:GameDataDir=$GAME_DIR/data_sts2_linuxbsd_x86_64"
    mkdir -p "$DIST_DIR"
    install -m 644 "$PROJECT_DIR/.godot/mono/temp/bin/Release/FirstMod.dll" "$DIST_DIR/FirstMod.dll"
    install -m 644 "$PROJECT_DIR/mod_manifest.json" "$DIST_DIR/FirstMod.json"
    printf 'Built DLL-only package: %s\nNo game files were changed.\n' "$DIST_DIR"
    ;;
  install)
    require_file "$DIST_DIR/FirstMod.dll"
    require_file "$DIST_DIR/FirstMod.json"
    if pgrep -x SlayTheSpire2 >/dev/null; then
      printf 'Close Slay the Spire 2 before installing the mod.\n' >&2
      exit 1
    fi
    MOD_DIR="$GAME_DIR/mods/FirstMod"
    mkdir -p "$MOD_DIR"
    install -m 644 "$DIST_DIR/FirstMod.dll" "$MOD_DIR/FirstMod.dll"
    install -m 644 "$DIST_DIR/FirstMod.json" "$MOD_DIR/FirstMod.json"
    printf 'Installed: %s\nEnable mods in the game and restart if prompted.\n' "$MOD_DIR"
    ;;
  smoke)
    require_file "$DIST_DIR/FirstMod.dll"
    require_file "$DIST_DIR/FirstMod.json"
    exec python3 "$PROJECT_DIR/tests/smoke.py" --game-dir "$GAME_DIR" --package-dir "$DIST_DIR"
    ;;
esac
