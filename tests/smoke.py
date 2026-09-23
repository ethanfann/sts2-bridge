#!/usr/bin/env python3
"""Load the real game/mod offline without exposing the user's saves or Steam IPC."""

import argparse
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
from bridge_test import read_json, send_command, wait_for
from event_cases import CASE_EVENTS, exercise_event


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--game-dir", type=Path, required=True)
    parser.add_argument("--package-dir", type=Path, required=True)
    parser.add_argument("--fixture-dir", type=Path)
    parser.add_argument("--case", choices=CASE_EVENTS)
    parser.add_argument("--recording-failure", action="store_true",
                        help="Block recording storage to verify gameplay/transport stay usable")
    args = parser.parse_args()
    if bool(args.case) != bool(args.fixture_dir):
        parser.error("--case and --fixture-dir must be provided together")
    if not shutil.which("bwrap"):
        parser.error("bubblewrap (bwrap) is required; there is no unsandboxed fallback")
    game = args.game_dir.resolve()
    package = args.package_dir.resolve()
    for path in (game / "SlayTheSpire2", game / "SlayTheSpire2.pck",
                 package / "sts2-bridge.dll", package / "sts2-bridge.json"):
        if not path.is_file():
            parser.error(f"Missing file: {path}")

    artifacts = package.parent / "smoke"
    artifacts.mkdir(parents=True, exist_ok=True)
    output = Path(tempfile.mkdtemp(prefix="run-", dir=artifacts))
    print(f"Smoke artifacts: {output}", flush=True)
    with tempfile.TemporaryDirectory(prefix="sts2-bridge-smoke-") as temporary:
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
                    "--ro-bind", str(package), "/game/mods/sts2-bridge"]
        if args.fixture_dir:
            command += ["--ro-bind", str(args.fixture_dir.resolve()), "/game/mods/BridgeFixtures"]
        command += ["--setenv", "HOME", "/test/home",
                    "--setenv", "XDG_DATA_HOME", "/test/data",
                    "--setenv", "XDG_CONFIG_HOME", "/test/config",
                    "--setenv", "XDG_CACHE_HOME", "/test/cache",
                    "--setenv", "LANG", "C.UTF-8",
                    "--setenv", "PATH", "/usr/bin:/bin",
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
        settings["seen_ea_disclaimer"] = True
        settings["fps_limit"] = 30
        settings_path.write_text(json.dumps(settings))

        if args.recording_failure:
            bridge = sandbox / "data/SlayTheSpire2/sts2-bridge"
            bridge.mkdir(parents=True)
            (bridge / "recordings").write_text("Not a directory: simulated recording storage failure")

        if args.case:
            command[-1] = "2400"
            command += ["--bridge-fixture-event", CASE_EVENTS[args.case]]

        with (output / "game.log").open("w") as log:
            process = subprocess.Popen(command, stdout=log, stderr=subprocess.STDOUT)
            try:
                state_path = wait_for(process, "state.json", lambda: next(
                    sandbox.rglob("sts2-bridge/state.json"), None))
                state = wait_for(process, "valid state JSON", lambda: read_json(state_path))
                if (state.get("protocol_version") != 1 or state.get("scene") != "main_menu"
                        or state.get("waiting_for_input") is not False
                        or state.get("run") is not None or state.get("player") is not None
                        or not state.get("state_id") or not state.get("timestamp")):
                    raise RuntimeError(f"Unexpected fresh-game snapshot: {state}")
                shutil.copy2(state_path, output / "state.json")

                bridge = state_path.parent
                result = send_command(process, bridge, "smoke_probe", expected_status="error")
                if result.get("message") != "Unsupported command type 'smoke_probe'.":
                    raise RuntimeError(f"Unexpected command result: {result}")
                (output / "command-result.json").write_text(json.dumps(result))
                marker = subprocess.run([sys.executable, str(Path(__file__).resolve().parent.parent / "recording.py"),
                                         "--bridge-dir", str(bridge), "mark", "smoke: recording transport verified"],
                                        capture_output=True, text=True, timeout=10)
                if args.recording_failure:
                    assert marker.returncode == 1, marker
                    assert "Recording is unavailable; marker was not saved." in marker.stderr
                else:
                    assert marker.returncode == 0, marker.stderr
                    assert json.loads(marker.stdout)["status"] == "ok"
                wait_for(process, "marker consumption", lambda: not (bridge / "command.json").exists())
                if args.case:
                    exercise_event(process, sandbox, state_path, args.case, output)
                    # The test-only mod watches this file and quits cleanly.
                    (sandbox / "fixture-stop").touch()
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
                recording = next(sandbox.rglob("sts2-bridge/recordings"), None)
                if recording and recording.is_dir():
                    shutil.copytree(recording, output / "recordings")
                    latest = recording.parent / "latest-session.json"
                    if latest.exists():
                        shutil.copy2(latest, output / latest.name)
                for name in ("fixture-ready.json", "fixture-error.txt", "event-catalog.json"):
                    if (sandbox / name).exists():
                        shutil.copy2(sandbox / name, output / name)

        log_text = (output / "game.log").read_text()
        mod_count = 2 if args.case else 1
        for required in ("Steam initialization skipped", "STS2 Bridge export hooks applied.",
                         "STS2 Bridge writing state to", f"Loaded {mod_count} mods ({mod_count} total)",
                         "main menu loaded (complete)"):
            if required not in log_text:
                raise RuntimeError(f"Missing startup evidence: {required}")
        for failure in ("STS2 Bridge export failed", "STS2 Bridge command failed",
                        "HarmonyException", "[ERROR]"):
            if failure in log_text:
                raise RuntimeError(f"Game log contains: {failure}")
        if args.recording_failure:
            assert log_text.count("STS2 Bridge recording disabled") == 1
            assert not (output / "recordings").exists()
            print(f"PASS: {args.case or 'transport'} with recording storage unavailable")
            return
        assert "STS2 Bridge recording disabled" not in log_text
        sessions = list((output / "recordings").iterdir())
        assert len(sessions) == 1, sessions
        entries = [json.loads(line) for segment in sorted(sessions[0].glob("timeline-*.jsonl"))
                   for line in segment.read_text().splitlines()]
        assert [e["sequence"] for e in entries] == list(range(1, len(entries) + 1))
        assert entries[0]["type"] == "session_start" and entries[-1]["type"] == "session_end"
        assert any(e["type"] == "state" and e["data"] == state for e in entries)
        assert any(e["type"] == "command_result" and e["data"] == result for e in entries)
        assert any(e["type"] == "marker" and e["data"]["note"] == "smoke: recording transport verified" for e in entries)
    print(f"PASS: {args.case or 'startup, state/command transport, and session recording'}")


if __name__ == "__main__":
    main()
