#!/usr/bin/env bash
set -euo pipefail

printf 'This WSL script is disabled. Use build-and-deploy.cmd or build-and-deploy.ps1 from Windows PowerShell.\n' >&2
exit 1

PROJECT_NAME="FirstMod"
PROJECT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DIST_DIR="$PROJECT_DIR/dist"
GAME_DIR_DEFAULT="/mnt/c/Program Files (x86)/Steam/steamapps/common/Slay the Spire 2"
GODOT_EXE_DEFAULT="/mnt/c/Users/ecfan/Downloads/Godot_v4.5.1-stable_mono_win64/Godot_v4.5.1-stable_mono_win64/Godot_v4.5.1-stable_mono_win64_console.exe"
GAME_DIR="${STS2_GAME_DIR:-$GAME_DIR_DEFAULT}"
MOD_DIR="${STS2_MOD_DIR:-$GAME_DIR/mods/$PROJECT_NAME}"
GAME_DLL="$GAME_DIR/data_sts2_windows_x86_64/sts2.dll"

find_godot() {
  if [[ -f "$GODOT_EXE_DEFAULT" ]]; then
    printf '%s\n' "$GODOT_EXE_DEFAULT"
    return
  fi

  printf '%s\n' ""
}

require_file() {
  local path="$1"
  local label="$2"

  if [[ ! -f "$path" ]]; then
    printf 'Missing %s: %s\n' "$label" "$path" >&2
    exit 1
  fi
}

run_windows_godot() {
  local exe_win="$1"
  shift

  if ! command -v powershell.exe >/dev/null 2>&1; then
    printf 'powershell.exe not found from WSL. Enable WSL Windows interop or run from PowerShell.\n' >&2
    exit 1
  fi

  local ps_script
  ps_script="$1"
  powershell.exe -NoProfile -Command "$ps_script"
}

copy_artifact() {
  local from="$1"
  local to="$2"

  require_file "$from" "artifact"
  cp "$from" "$to"
}

require_file "$PROJECT_DIR/mod_manifest.json" "mod manifest"
require_file "$PROJECT_DIR/FirstMod.csproj" "csproj"
require_file "$PROJECT_DIR/export_presets.cfg" "export preset"
require_file "$GAME_DLL" "game sts2.dll"

if ! grep -q '"pck_name": "FirstMod"' "$PROJECT_DIR/mod_manifest.json"; then
  printf 'Manifest must contain: "pck_name": "FirstMod"\n' >&2
  exit 1
fi

if ! grep -q 'Godot.NET.Sdk/4.5.1' "$PROJECT_DIR/FirstMod.csproj"; then
  printf 'Warning: FirstMod.csproj is not pinned to Godot.NET.Sdk/4.5.1\n' >&2
fi

if ! command -v wslpath >/dev/null 2>&1; then
  printf 'This script expects WSL bash with wslpath available.\n' >&2
  exit 1
fi

GODOT_EXE_RESOLVED="$(find_godot)"
if [[ -z "$GODOT_EXE_RESOLVED" ]]; then
  printf 'Godot 4.5.1 mono exe not found: %s\n' "$GODOT_EXE_DEFAULT" >&2
  exit 1
fi

GODOT_EXE_WIN="$(wslpath -w "$GODOT_EXE_RESOLVED")"
PROJECT_WIN="$(wslpath -w "$PROJECT_DIR")"
PCK_WIN="$(wslpath -w "$DIST_DIR/$PROJECT_NAME.pck")"

printf 'Using Godot: %s\n' "$GODOT_EXE_RESOLVED"
printf 'Using game dir: %s\n' "$GAME_DIR"

mkdir -p "$DIST_DIR"
rm -f "$DIST_DIR/$PROJECT_NAME.dll" "$DIST_DIR/$PROJECT_NAME.pck" "$DIST_DIR/$PROJECT_NAME.json"
cp "$GAME_DLL" "$PROJECT_DIR/sts2.dll"

run_windows_godot "$GODOT_EXE_WIN" "& '$GODOT_EXE_WIN' --headless --path '$PROJECT_WIN' --build-solutions --quit; exit \$LASTEXITCODE"
run_windows_godot "$GODOT_EXE_WIN" "& '$GODOT_EXE_WIN' --headless --path '$PROJECT_WIN' --export-pack 'Windows Desktop' '$PCK_WIN' --quit; exit \$LASTEXITCODE"

DLL_SOURCE=""
for candidate in \
  "$PROJECT_DIR/.godot/mono/temp/bin/ExportDebug/win-x64/$PROJECT_NAME.dll" \
  "$PROJECT_DIR/.godot/mono/temp/bin/Debug/win-x64/$PROJECT_NAME.dll" \
  "$PROJECT_DIR/.godot/mono/temp/bin/Debug/$PROJECT_NAME.dll"
do
  if [[ -f "$candidate" ]]; then
    DLL_SOURCE="$candidate"
    break
  fi
done

if [[ -z "$DLL_SOURCE" ]]; then
  printf 'Could not find built DLL output.\n' >&2
  exit 1
fi

copy_artifact "$DLL_SOURCE" "$DIST_DIR/$PROJECT_NAME.dll"
copy_artifact "$PROJECT_DIR/mod_manifest.json" "$DIST_DIR/$PROJECT_NAME.json"

mkdir -p "$MOD_DIR"
copy_artifact "$DIST_DIR/$PROJECT_NAME.dll" "$MOD_DIR/$PROJECT_NAME.dll"
copy_artifact "$DIST_DIR/$PROJECT_NAME.pck" "$MOD_DIR/$PROJECT_NAME.pck"
copy_artifact "$DIST_DIR/$PROJECT_NAME.json" "$MOD_DIR/$PROJECT_NAME.json"

printf 'Built and deployed %s\n' "$PROJECT_NAME"
printf 'DLL: %s\n' "$MOD_DIR/$PROJECT_NAME.dll"
printf 'PCK: %s\n' "$MOD_DIR/$PROJECT_NAME.pck"
printf 'JSON: %s\n' "$MOD_DIR/$PROJECT_NAME.json"
