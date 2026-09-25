#!/usr/bin/env python3
"""Manually upload native STS2 runs to Spire Codex. Linux, Python 3.8+, no background service."""

import argparse
from contextlib import contextmanager
from email.utils import parsedate_to_datetime
import fcntl
import hashlib
from http.client import HTTPException
import json
import os
from pathlib import Path
import re
import tempfile
import time
from urllib.error import HTTPError, URLError
from urllib.parse import urlencode
from urllib.request import Request, urlopen


SITE = "https://spire-codex.com"
UPLOAD_URL = SITE + "/api/runs"
MAX_BYTES = 512 * 1024
SETTLE_SECONDS = 2


class NotReady(Exception):
    pass


class UploadError(Exception):
    def __init__(self, message, retryable, retry_after=0):
        super().__init__(message)
        self.retryable = retryable
        self.retry_after = retry_after


@contextmanager
def state_lock(directory, initialize=False):
    if initialize:
        directory.mkdir(parents=True, exist_ok=True, mode=0o700)
    with (directory / "lock").open("a" if initialize else "r") as lock:
        try:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            raise RuntimeError("Another uploader is using this state directory") from None
        yield


def save_state(directory, state):
    with tempfile.NamedTemporaryFile(mode="w", encoding="utf-8", dir=directory,
                                     prefix=".state-", delete=False) as stream:
        temporary = Path(stream.name)
        try:
            json.dump(state, stream, indent=2)
            stream.write("\n")
            stream.flush()
            os.fsync(stream.fileno())
            os.replace(temporary, directory / "state.json")
        finally:
            temporary.unlink(missing_ok=True)


def initialize(directory, history, steam_id):
    if (directory / "state.json").exists():
        raise ValueError("Already initialized; use the existing ledger, not a new baseline")
    history = history.expanduser().resolve(strict=True)
    if not history.is_dir():
        raise ValueError("--history-dir must be a directory")
    if not re.fullmatch(r"[0-9]{17}", steam_id):
        raise ValueError("--steam-id must be a 17-digit SteamID64")
    state = {
        "version": 1,
        "history_dir": str(history),
        "steam_id": steam_id,
        "runs": {path.name: {"status": "excluded"}
                 for path in sorted(history.glob("*.run"))},
    }
    save_state(directory, state)
    return {"status": "initialized", "history_dir": str(history), "steam_id": steam_id,
            "excluded": len(state["runs"])}


def read_run(path):
    before = path.stat()
    if time.time() - before.st_mtime < SETTLE_SECONDS:
        raise NotReady("Waiting for the final .run file to settle")
    with path.open("rb") as stream:
        body = stream.read(MAX_BYTES + 1)
    after = path.stat()
    if (before.st_ino, before.st_size, before.st_mtime_ns) != (
            after.st_ino, after.st_size, after.st_mtime_ns):
        raise NotReady("Run file changed while reading")
    if len(body) > MAX_BYTES:
        raise ValueError("Run exceeds Spire Codex's 512 KiB limit")
    try:
        data = json.loads(body)
    except (ValueError, UnicodeError):
        raise NotReady("Run file is not complete JSON yet") from None
    if not isinstance(data, dict) or not all(
            isinstance(data.get(key), list) and data[key]
            for key in ("players", "map_point_history", "acts")):
        raise ValueError("Not a completed native run: missing players, map history, or acts")
    if not isinstance(data.get("win"), bool) or not isinstance(data.get("seed"), str):
        raise ValueError("Not a native run: missing win or seed")
    return body, {"seed": data["seed"], "win": data["win"],
                  "ascension": data.get("ascension"), "build_id": data.get("build_id"),
                  "sha256": hashlib.sha256(body).hexdigest()}


def retry_seconds(value):
    try:
        return max(0, float(value))
    except (ValueError, TypeError):
        try:
            return max(0, parsedate_to_datetime(value).timestamp() - time.time())
        except (ValueError, TypeError, OverflowError):
            return 0


def post_run(body, steam_id):
    request = Request(UPLOAD_URL + "?" + urlencode({"steam_id": steam_id}), data=body,
                      headers={"Content-Type": "application/json",
                               "User-Agent": "sts2-bridge-codex/1"}, method="POST")
    try:
        with urlopen(request, timeout=30) as response:
            result = json.load(response)
    except HTTPError as error:
        with error:
            message = error.read(1024).decode("utf-8", errors="replace")
        raise UploadError("HTTP {}: {}".format(error.code, message),
                          error.code == 429 or error.code >= 500,
                          retry_seconds(error.headers.get("Retry-After"))) from error
    except (URLError, OSError, HTTPException, ValueError) as error:
        # An ambiguous response may follow a successful POST. The server's run hash
        # deduplicates the next attempt; never treat an unacknowledged upload as done.
        raise UploadError(str(error), True) from error
    if (not isinstance(result, dict) or result.get("success") is not True
            or not isinstance(result.get("run_hash"), str)
            or not re.fullmatch(r"[0-9a-f]{16}", result["run_hash"])
            or result.get("url") != SITE + "/runs/" + result["run_hash"]):
        raise UploadError("Upload response did not confirm a public run URL", True)
    return {key: result[key] for key in ("run_hash", "url", "duplicate") if key in result}


def upload(directory, state, path, dry_run=False, explicit=False, note=None, thread=None):
    path = path.expanduser().resolve(strict=True)
    if path.parent != Path(state["history_dir"]) or path.suffix != ".run":
        raise ValueError("Upload only .run files directly inside the initialized history directory")
    old = state["runs"].get(path.name, {})
    if old.get("status") == "uploaded" or (
            not explicit and old.get("status") in ("excluded", "rejected")):
        if not dry_run and (note is not None or thread is not None):
            old.update({key: value for key, value in (("note", note), ("thread", thread))
                        if value is not None})
            save_state(directory, state)
        return {"file": path.name, **old}
    now = time.time()
    if state.get("retry_at", 0) > now:
        return {"file": path.name, "status": "deferred", "retry_at": state["retry_at"]}
    entry = dict(old)
    entry.update({key: value for key, value in (("note", note), ("thread", thread))
                  if value is not None})
    try:
        body, metadata = read_run(path)
        entry.update(metadata)
    except NotReady as error:
        return {"file": path.name, "status": "waiting", "error": str(error)}
    except ValueError as error:
        entry.update(status="rejected", error=str(error))
    else:
        if dry_run:
            return {"file": path.name, "steam_id": state["steam_id"],
                    **entry, "status": "would_upload", "bytes": len(body)}
        # Save provenance before the side effect, including across crashes/retries.
        entry.update(status="pending", attempts=entry.get("attempts", 0) + 1)
        state["runs"][path.name] = entry
        save_state(directory, state)
        try:
            result = post_run(body, state["steam_id"])
        except UploadError as error:
            entry.update(status="retry" if error.retryable else "rejected", error=str(error))
            if error.retryable:
                delay = max(min(3600, 60 * 2 ** min(entry["attempts"] - 1, 6)), error.retry_after)
                state["retry_at"] = time.time() + delay
        else:
            entry.pop("error", None)
            entry.update(result, status="uploaded", uploaded_at=time.time())
            state.pop("retry_at", None)
    if not dry_run:
        state["runs"][path.name] = entry
        save_state(directory, state)
    return {"file": path.name, **entry}


def sync(directory, state, dry_run=False):
    history = Path(state["history_dir"])
    if not history.is_dir():
        raise ValueError("Configured history directory is missing; refusing to change the baseline")
    results = []
    for path in sorted(history.glob("*.run")):
        if state["runs"].get(path.name, {}).get("status") in ("excluded", "uploaded", "rejected"):
            continue
        result = upload(directory, state, path, dry_run=dry_run)
        results.append(result)
        if result["status"] in ("retry", "deferred"):
            break  # Honor rate limits/outages across the whole queue, not per file.
    return {"runs": results}


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--state-dir", type=Path, default=Path(
        os.environ.get("XDG_STATE_HOME", Path.home() / ".local/state")) / "sts2-codex")
    commands = parser.add_subparsers(dest="command", required=True)
    init = commands.add_parser("init", help="Select one profile and EXCLUDE its existing history")
    init.add_argument("--history-dir", type=Path, required=True)
    init.add_argument("--steam-id", required=True)
    single = commands.add_parser("upload", help="Upload one run, including an explicitly chosen old run")
    single.add_argument("file", type=Path)
    single.add_argument("--note", help="Local-only provenance; never included in the public upload")
    single.add_argument("--thread", help="Local-only controller thread URL")
    single.add_argument("--dry-run", action="store_true", help="Read only; no POST or ledger changes")
    batch = commands.add_parser("sync", help="Scan once, upload eligible runs, then exit; never watch or schedule")
    batch.add_argument("--dry-run", action="store_true", help="Read only; no POST or ledger changes")
    commands.add_parser("status", help="Show local configuration, exclusions, uploads, and errors")
    args = parser.parse_args(argv)
    directory = args.state_dir.expanduser().resolve()
    try:
        with state_lock(directory, initialize=args.command == "init"):
            if args.command == "init":
                result = initialize(directory, args.history_dir, args.steam_id)
            else:
                state = json.loads((directory / "state.json").read_text(encoding="utf-8"))
                if state["version"] != 1:
                    raise ValueError("Unsupported ledger version")
                if args.command == "status":
                    result = state
                elif args.command == "sync":
                    result = sync(directory, state, dry_run=args.dry_run)
                else:
                    result = upload(directory, state, args.file, dry_run=args.dry_run,
                                    explicit=True, note=args.note, thread=args.thread)
        print(json.dumps(result, indent=2))
        entries = result.get("runs", [result])
        if isinstance(entries, list) and any(
                entry.get("status") in ("retry", "rejected", "waiting", "deferred") for entry in entries):
            return 1
        return 0
    except (OSError, ValueError, RuntimeError, KeyError) as error:
        parser.exit(1, "{}\nUse init once before uploading; never reset a missing/corrupt ledger blindly.\n".format(error))


if __name__ == "__main__":
    raise SystemExit(main())
