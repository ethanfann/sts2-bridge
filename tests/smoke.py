#!/usr/bin/env python3
"""Load the real game/mod offline without exposing the user's saves or Steam IPC."""

import argparse
import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import time
import uuid


def read_json(path):
    # The bridge currently copies its temporary JSON over the destination.
    try:
        return json.loads(path.read_text())
    except (FileNotFoundError, json.JSONDecodeError):
        return None


def wait_for(process, description, check, timeout=45):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        result = check()
        if result:
            return result
        if process.poll() is not None:
            raise RuntimeError(f"Game exited ({process.returncode}) before {description}")
        time.sleep(0.1)
    raise RuntimeError(f"Timed out waiting for {description}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--game-dir", type=Path, required=True)
    parser.add_argument("--package-dir", type=Path, required=True)
    args = parser.parse_args()
    if not shutil.which("bwrap"):
        parser.error("bubblewrap (bwrap) is required; there is no unsandboxed fallback")
    game = args.game_dir.resolve()
    package = args.package_dir.resolve()
    for path in (game / "SlayTheSpire2", game / "SlayTheSpire2.pck",
                 package / "FirstMod.dll", package / "FirstMod.json"):
        if not path.is_file():
            parser.error(f"Missing file: {path}")

    artifacts = package.parent / "smoke"
    artifacts.mkdir(parents=True, exist_ok=True)
    output = Path(tempfile.mkdtemp(prefix="run-", dir=artifacts))
    print(f"Smoke artifacts: {output}", flush=True)
    with tempfile.TemporaryDirectory(prefix="first-mod-smoke-") as temporary:
        sandbox = Path(temporary)
        # Only this temporary directory is writable on the host. In particular,
        # neither the real home nor Steam's sockets are mounted into the sandbox.
        command = ["bwrap", "--unshare-all", "--die-with-parent", "--new-session",
                   "--clearenv", "--ro-bind", "/usr", "/usr",
                   "--ro-bind", "/etc", "/etc", "--ro-bind", "/bin", "/bin"]
        for library_path in ("/lib", "/lib64"):
            if Path(library_path).exists():
                command += ["--ro-bind", library_path, library_path]
        command += ["--proc", "/proc", "--dev", "/dev", "--tmpfs", "/tmp",
                    "--tmpfs", "/run", "--bind", str(sandbox), "/test",
                    "--dir", "/game"]
        # Mount files individually so adding sandbox-only mod directories never
        # requires creating mount points in the installed game's directory.
        for entry in sorted(game.iterdir()):
            if entry.name not in ("mods", "mods_STEAMTEST"):
                command += ["--ro-bind", str(entry), f"/game/{entry.name}"]
        command += ["--tmpfs", "/game/mods", "--tmpfs", "/game/mods_STEAMTEST",
                    "--ro-bind", str(package), "/game/mods/FirstMod",
                    "--setenv", "HOME", "/test/home",
                    "--setenv", "XDG_DATA_HOME", "/test/data",
                    "--setenv", "XDG_CONFIG_HOME", "/test/config",
                    "--setenv", "XDG_CACHE_HOME", "/test/cache",
                    "--setenv", "LANG", "C.UTF-8",
                    "--setenv", "PATH", "/usr/bin:/bin",
                    # Harmony's MonoMod helper needs globally visible unwind
                    # symbols, which aren't loaded by the headless renderer.
                    "--setenv", "LD_PRELOAD", "libgcc_s.so.1",
                    "--setenv", "LD_LIBRARY_PATH", "/game:/game/data_sts2_linuxbsd_x86_64",
                    "--chdir", "/game", "/game/SlayTheSpire2",
                    "--headless", "--force-steam", "off", "--max-fps", "30",
                    "--quit-after", "300"]

        # Let the installed game generate its current settings schema and paths.
        # Then enable mods only in that disposable account, never the real one.
        with (output / "bootstrap.log").open("w") as log:
            subprocess.run(command + ["--nomods"], stdout=log,
                           stderr=subprocess.STDOUT, timeout=60, check=True)
        settings_paths = list(sandbox.rglob("settings.save"))
        if len(settings_paths) != 1:
            raise RuntimeError(f"Expected one generated settings file, found {len(settings_paths)}")
        settings_path = settings_paths[0]
        settings = json.loads(settings_path.read_text())
        settings["mod_settings"] = {"mods_enabled": True}
        settings["skip_intro_logo"] = True
        settings["fps_limit"] = 30
        settings_path.write_text(json.dumps(settings))

        with (output / "game.log").open("w") as log:
            process = subprocess.Popen(command, stdout=log, stderr=subprocess.STDOUT)
            try:
                state_path = wait_for(process, "state.json", lambda: next(
                    sandbox.rglob("first-mod-bridge/state.json"), None))
                state = wait_for(process, "valid state JSON", lambda: read_json(state_path))
                if (state.get("protocol_version") != 1 or state.get("scene") != "main_menu"
                        or state.get("waiting_for_input") is not False
                        or state.get("run") is not None or state.get("player") is not None
                        or not state.get("state_id") or not state.get("timestamp")):
                    raise RuntimeError(f"Unexpected fresh-game snapshot: {state}")
                shutil.copy2(state_path, output / "state.json")

                bridge = state_path.parent
                command_id = f"smoke-{uuid.uuid4()}"
                pending = bridge / "command.json.tmp"
                pending.write_text(json.dumps({"command_id": command_id, "type": "smoke_probe"}))
                pending.replace(bridge / "command.json")
                result_path = bridge / "command-result.json"
                result = wait_for(process, "command result", lambda: read_json(result_path))
                if (result.get("command_id") != command_id or result.get("status") != "error"
                        or result.get("message") != "Unsupported command type 'smoke_probe'."
                        or not result.get("processed_at")):
                    raise RuntimeError(f"Unexpected command result: {result}")
                wait_for(process, "command consumption", lambda: not (bridge / "command.json").exists())
                shutil.copy2(result_path, output / "command-result.json")
                # Let Godot quit itself, detecting delayed startup failures too.
                if process.wait(timeout=45) != 0:
                    raise RuntimeError(f"Game exited with {process.returncode}")
            finally:
                if process.poll() is None:
                    process.terminate()
                    try:
                        process.wait(timeout=5)
                    except subprocess.TimeoutExpired:
                        process.kill()
                        process.wait()

        log_text = (output / "game.log").read_text()
        for required in ("Steam initialization skipped", "FirstMod bridge export hooks applied.",
                         "FirstMod bridge writing state to", "Loaded 1 mods (1 total)",
                         "main menu loaded (complete)"):
            if required not in log_text:
                raise RuntimeError(f"Missing startup evidence: {required}")
        for failure in ("FirstMod bridge export failed", "FirstMod bridge command failed",
                        "HarmonyException", "[ERROR]"):
            if failure in log_text:
                raise RuntimeError(f"Game log contains: {failure}")
    print("PASS: DLL loaded, hooks applied, main-menu state exported, command rejected and consumed.")
    print("This checks bridge transport/startup, not successful combat or event actions.")


if __name__ == "__main__":
    main()
