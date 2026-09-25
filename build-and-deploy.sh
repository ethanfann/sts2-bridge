#!/usr/bin/env bash
set -euo pipefail

PROJECT_DIR="$(dirname "$(realpath "${BASH_SOURCE[0]}")")"
DIST_DIR="$PROJECT_DIR/dist/local"
GAME_DIR="$HOME/.local/share/Steam/steamapps/common/Slay the Spire 2"
ACTION=build

usage() {
  cat <<'EOF'
Usage: ./build-and-deploy.sh [build|install|smoke|events|combat] [--game-dir PATH]

  build    Build a DLL-only package in dist/local (default; does not install).
  install  Install the existing dist/local package into the game's mods folder.
  smoke    Test the existing package in a disposable, offline sandbox.
  events   Test events, upgrades, shop/rest, treasure, and map travel offline.
  combat   Test combat context, relic triggers, and pile/hand selectors offline.

Requires a .NET 9 SDK for build/fixtures, Python 3 and bubblewrap for tests.
Use --game-dir for a non-default Steam library. On Windows use the .ps1 script.
EOF
}

if [[ ${1:-} == build || ${1:-} == install || ${1:-} == smoke || ${1:-} == events || ${1:-} == combat ]]; then
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
    rm -f "$DIST_DIR/sts2-bridge.dll" "$DIST_DIR/sts2-bridge.json"
    dotnet build "$PROJECT_DIR/sts2-bridge.csproj" --configuration Release \
      "-p:GameDataDir=$GAME_DIR/data_sts2_linuxbsd_x86_64"
    mkdir -p "$DIST_DIR"
    install -m 644 "$PROJECT_DIR/.godot/mono/temp/bin/Release/sts2-bridge.dll" "$DIST_DIR/sts2-bridge.dll"
    install -m 644 "$PROJECT_DIR/mod_manifest.json" "$DIST_DIR/sts2-bridge.json"
    printf 'Built DLL-only package: %s\nNo game files were changed.\n' "$DIST_DIR"
    ;;
  install)
    require_file "$DIST_DIR/sts2-bridge.dll"
    require_file "$DIST_DIR/sts2-bridge.json"
    if pgrep -x SlayTheSpire2 >/dev/null; then
      printf 'Close Slay the Spire 2 before installing the mod.\n' >&2
      exit 1
    fi
    if [[ -d "$GAME_DIR/mods/FirstMod" ]]; then
      printf 'Move the old mods/FirstMod folder outside mods before installing sts2-bridge; do not load both.\n' >&2
      exit 1
    fi
    MOD_DIR="$GAME_DIR/mods/sts2-bridge"
    mkdir -p "$MOD_DIR"
    install -m 644 "$DIST_DIR/sts2-bridge.dll" "$MOD_DIR/sts2-bridge.dll"
    install -m 644 "$DIST_DIR/sts2-bridge.json" "$MOD_DIR/sts2-bridge.json"
    printf 'Installed: %s\nEnable mods in the game and restart if prompted.\n' "$MOD_DIR"
    ;;
  smoke)
    require_file "$DIST_DIR/sts2-bridge.dll"
    require_file "$DIST_DIR/sts2-bridge.json"
    exec python3 "$PROJECT_DIR/tests/smoke.py" --game-dir "$GAME_DIR" --package-dir "$DIST_DIR"
    ;;
  events|combat)
    require_file "$DIST_DIR/sts2-bridge.dll"
    require_file "$DIST_DIR/sts2-bridge.json"
    dotnet build "$PROJECT_DIR/tests/Fixtures/BridgeFixtures.csproj" --configuration Release \
      --artifacts-path "$PROJECT_DIR/.godot/fixture-build" \
      "-p:GameDataDir=$GAME_DIR/data_sts2_linuxbsd_x86_64"
    FIXTURE_DIR="$PROJECT_DIR/dist/fixtures"
    mkdir -p "$FIXTURE_DIR"
    install -m 644 "$PROJECT_DIR/.godot/fixture-build/bin/BridgeFixtures/release/BridgeFixtures.dll" "$FIXTURE_DIR/BridgeFixtures.dll"
    install -m 644 "$PROJECT_DIR/tests/Fixtures/BridgeFixtures.json" "$FIXTURE_DIR/BridgeFixtures.json"
    cases=(neow-mechanics morphic-loner wellspring-bottle wellspring-bathe crystal-sphere crystal-sphere-gold battleworn-dummy round-tea-party architect-dialogue deck-upgrade deck-selection shop-rest-context fake-merchant-buy fake-merchant-skip treasure-take treasure-skip treasure-empty map-normal map-boss)
    if [[ "$ACTION" == combat ]]; then
      cases=(combat-context combat-pile-selection combat-hand-selection potion-rewards card-rewards)
    fi
    for event_case in "${cases[@]}"; do
      python3 "$PROJECT_DIR/tests/smoke.py" --game-dir "$GAME_DIR" --package-dir "$DIST_DIR" \
        --fixture-dir "$FIXTURE_DIR" --case "$event_case"
    done
    ;;
esac
