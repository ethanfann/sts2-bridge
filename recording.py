#!/usr/bin/env python3
"""Inspect the latest local recording or add a note while playing. No dependencies."""

import argparse
import json
from pathlib import Path

from bridge import default_bridge_dir, send_command


def read_json(path):
    return json.loads(path.read_text())


def status(bridge):
    latest = read_json(bridge / "latest-session.json")
    session = bridge / "recordings" / latest["session_id"]
    counts = {}
    unsupported = set()
    markers = []
    last_state = None
    ended = False
    partial_lines = 0
    for segment in sorted(session.glob("timeline-*.jsonl")):
        with segment.open() as stream:
            for line in stream:
                # A live append (or a crash) may leave an incomplete final line.
                if not line.endswith("\n"):
                    partial_lines += 1
                    continue
                entry = json.loads(line)
                kind = entry["type"]
                counts[kind] = counts.get(kind, 0) + 1
                if kind == "state":
                    last_state = entry["data"]
                    screen = last_state.get("screen", {})
                    if screen.get("supported") is False:
                        unsupported.add(screen.get("type"))
                elif kind == "marker":
                    markers.append({"timestamp": entry["timestamp"], "note": entry["data"]["note"]})
                elif kind == "session_end":
                    ended = True
    return {
        "session": str(session),
        "started_at": latest["started_at"],
        "clean_exit_recorded": ended,
        "counts": counts,
        "unsupported_screens": sorted(unsupported),
        "markers": markers,
        "partial_lines": partial_lines,
        "last_snapshot": {key: last_state.get(key) for key in
                          ("timestamp", "scene", "run", "combat")} if last_state else None,
        "note": "Saved observations, not a liveness check. A successful mark confirms live recording.",
    }


def mark(bridge, note, timeout=5):
    result = send_command(bridge, "mark", timeout=timeout, note=note)
    if result["status"] != "ok":
        raise RuntimeError(result["message"])
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--bridge-dir", type=Path, default=default_bridge_dir())
    commands = parser.add_subparsers(dest="command", required=True)
    commands.add_parser("status", help="Summarize saved observations (also works after exit)")
    marker = commands.add_parser("mark", help="Record a note in the running game's session")
    marker.add_argument("note")
    args = parser.parse_args()
    try:
        result = status(args.bridge_dir) if args.command == "status" else mark(args.bridge_dir, args.note)
    except (OSError, ValueError, RuntimeError) as error:
        parser.exit(1, f"{error}\nCheck that the mod is loaded and --bridge-dir points to its data directory.\n")
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
