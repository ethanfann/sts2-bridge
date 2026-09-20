# FirstMod bridge

A file-based Slay the Spire 2 state/action bridge. The current Linux baseline is
**game v0.107.1**, .NET 9, and Godot 4.5.1. No AI runtime is required.

## Native Linux build

Install a .NET 9 **SDK** (the runtime alone cannot compile the mod). With mise:

```sh
mise install dotnet@9
mise exec dotnet@9 -- ./build-and-deploy.sh build
```

If the SDK is already on `PATH`, run `./build-and-deploy.sh build` directly.
The default action is also `build`; it never installs or starts the game.

The script reads `sts2.dll` and `0Harmony.dll` directly from the installed game's
`data_sts2_linuxbsd_x86_64` directory. Old repository-root DLLs are not used on
Linux. The Godot SDK supplies the C# bindings and source generator; no Godot
editor or PCK export is needed. The package contains only `FirstMod.dll` and
`FirstMod.json` in `dist/linux/`.

The default game directory is
`~/.local/share/Steam/steamapps/common/Slay the Spire 2`. For another library,
append `--game-dir "/path/to/Slay the Spire 2"` to any command. Direct MSBuild
users can pass `-p:GameDataDir="/path/to/data_sts2_linuxbsd_x86_64"`.

## Repeatable smoke test

Requires Python 3 and bubblewrap (`bwrap`), in addition to the installed game:

```sh
./build-and-deploy.sh smoke
```

This tests the existing package; rebuild first after changing C# or the manifest.
It runs the real game headlessly twice: once to generate fresh settings, then
with mod loading enabled in that disposable account. It asserts:

- The DLL initializes and all Harmony patches apply.
- Startup reaches the main menu and exports a protocol-v1 `state.json`.
- An unsupported `smoke_probe` command receives the matching error result and
  its command file is consumed.

The test mounts game files read-only, exposes no real home directory or Steam
sockets, disables networking and Steam initialization, and uses temporary save
data. No game install, real save, or Steam Cloud data is changed. Other installed
mods are excluded. There is no unsandboxed fallback.

The launcher preloads `libgcc_s.so.1` for Harmony's native unwind helper in
headless mode. The game also emits FMOD and dummy-renderer/exit warnings in this
mode, including without the mod; those are retained in the logs. Managed errors
and bridge startup/export/command failures fail the test.

Logs, state, and command results are retained under `dist/smoke/run-*/`; temporary
save data is deleted. This is a startup/transport check, **not** validation of
successful gameplay actions, potion descriptions, combat transitions, or event
coverage. Mystery-room fixtures and richer turn-state coverage remain next work.

## Install for normal play

Close the game, then explicitly install the package you built:

```sh
./build-and-deploy.sh install
```

This copies only the two package files into `<game>/mods/FirstMod/`. Other mods
are left alone. An old `FirstMod.pck`, if present, is not deleted and is ignored
by the DLL-only manifest. Enable mods in the game and restart if prompted.

During normal play, bridge files live in Godot's `user://first-mod-bridge`
directory (normally `~/.local/share/SlayTheSpire2/first-mod-bridge` on Linux):
`state.json`, `command.json`, `command-result.json`, and `trace.log`. Write
commands to a temporary file and rename it to `command.json`, using a unique
`command_id`; wait for the matching result before submitting another command.

The legacy Windows `.cmd`/PowerShell scripts are retained but have not been
retested. They still use their old Godot editor/PCK export workflow.
