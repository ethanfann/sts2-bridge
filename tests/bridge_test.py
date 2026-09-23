"""JSON protocol assertions shared by the real-game smoke and event tests."""

import json
from pathlib import Path
import sys
import time

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from bridge import send_command as client_send_command


def read_json(path):
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


def wait_state(process, path, description, predicate):
    def check():
        state = read_json(path)
        return state if state and predicate(state) else None
    return wait_for(process, description, check)


def send_command(process, bridge, command_type, expected_status="ok", **fields):
    if process.poll() is not None:
        raise RuntimeError(f"Game exited ({process.returncode}) before {command_type}")
    result = client_send_command(bridge, command_type, timeout=10, **fields)
    if result.get("status") != expected_status or not result.get("processed_at"):
        raise RuntimeError(f"Unexpected command result: {result}")
    return result
