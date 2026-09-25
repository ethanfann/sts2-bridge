from contextlib import redirect_stderr, redirect_stdout
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import io
import json
import os
from pathlib import Path
import tempfile
import threading
import unittest
from unittest.mock import patch
from urllib.parse import parse_qs, urlsplit

import codex


STEAM_ID = "76561198000000001"
ACK = {"success": True, "run_hash": "a1b2c3d4e5f67890",
       "url": "https://spire-codex.com/runs/a1b2c3d4e5f67890"}


class CodexTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.history = self.root / "history"
        self.history.mkdir()
        self.directory = self.root / "ledger"
        self.requests = []
        self.responses = []
        owner = self

        class Handler(BaseHTTPRequestHandler):
            def do_POST(self):
                owner.requests.append((self.path, dict(self.headers),
                                       self.rfile.read(int(self.headers["Content-Length"]))))
                status, body, headers = owner.responses.pop(0) if owner.responses else (200, ACK, {})
                self.send_response(status)
                for key, value in headers.items():
                    self.send_header(key, value)
                self.end_headers()
                self.wfile.write(json.dumps(body).encode())

            def log_message(self, *args):
                pass

        server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        worker = threading.Thread(target=server.serve_forever, kwargs={"poll_interval": 0.01})
        worker.start()
        self.addCleanup(server.server_close)
        self.addCleanup(worker.join)
        self.addCleanup(server.shutdown)
        endpoint = patch.object(codex, "UPLOAD_URL", "http://127.0.0.1:{}/api/runs".format(server.server_port))
        endpoint.start()
        self.addCleanup(endpoint.stop)
        clock = patch("codex.time.time", return_value=1000000)
        self.clock = clock.start()
        self.addCleanup(clock.stop)

    def run_file(self, name="new.run", **fields):
        data = {"players": [{"character": "CHARACTER.IRONCLAD", "deck": []}],
                "map_point_history": [[{"rooms": [{"room_type": "monster"}]}]],
                "acts": ["ACT.OVERGROWTH"], "win": False, "seed": "TEST-ONLY",
                "ascension": 7, "build_id": "v0.107.1", "start_time": 999000,
                "run_time": 800}
        data.update(fields)
        path = self.history / name
        path.write_text(json.dumps(data, indent=2))
        os.utime(path, (999990, 999990))
        return path

    def init(self):
        with codex.state_lock(self.directory, initialize=True):
            return codex.initialize(self.directory, self.history, STEAM_ID)

    def state(self):
        return json.loads((self.directory / "state.json").read_text())

    def cli(self, *args):
        output = io.StringIO()
        with redirect_stdout(output):
            code = codex.main(["--state-dir", str(self.directory), *map(str, args)])
        return code, json.loads(output.getvalue())

    def test_cli_requires_an_explicit_command_without_side_effects(self):
        with redirect_stderr(io.StringIO()), self.assertRaises(SystemExit) as error:
            self.cli()
        self.assertEqual(error.exception.code, 2)
        self.assertFalse(self.directory.exists())
        self.assertEqual(self.requests, [])

    def test_baseline_excludes_old_runs_and_cannot_be_reset(self):
        self.run_file("old.run")
        self.run_file("ignored.tmp")
        self.assertEqual(self.init()["excluded"], 1)
        self.assertEqual(self.state()["runs"], {"old.run": {"status": "excluded"}})
        before = (self.directory / "state.json").read_bytes()
        with self.assertRaisesRegex(ValueError, "Already initialized"):
            self.init()
        self.assertEqual((self.directory / "state.json").read_bytes(), before)
        self.assertEqual(self.requests, [])

    def test_explicit_backfill_sends_original_bytes_and_keeps_provenance_local(self):
        path = self.run_file("old.run", win=True)
        self.init()
        original = path.read_bytes()
        self.responses.append((200, {**ACK, "duplicate": True}, {}))
        code, result = self.cli("upload", path, "--note", "Human handled event",
                                "--thread", "https://ampcode.com/threads/test-only")
        self.assertEqual(code, 0)
        self.assertEqual(result["status"], "uploaded")
        self.assertTrue(result["duplicate"])
        query, headers, sent = self.requests[0]
        self.assertEqual(urlsplit(query).path, "/api/runs")
        self.assertEqual(parse_qs(urlsplit(query).query), {"steam_id": [STEAM_ID]})
        self.assertEqual(headers["Content-Type"], "application/json")
        self.assertEqual(sent, original)
        self.assertEqual(path.read_bytes(), original)
        self.assertEqual(self.state()["runs"][path.name]["note"], "Human handled event")
        self.assertEqual(self.state()["runs"][path.name]["url"], ACK["url"])
        self.cli("upload", path)
        self.assertEqual(self.cli("sync"), (0, {"runs": []}))
        self.assertEqual(len(self.requests), 1)

    def test_dry_run_does_not_post_or_mutate_exclusion_or_notes(self):
        path = self.run_file("old.run")
        self.init()
        before = {p.name: p.read_bytes() for p in self.directory.iterdir()}
        code, result = self.cli("upload", path, "--dry-run", "--note", "not saved")
        self.assertEqual(code, 0)
        self.assertEqual(result["status"], "would_upload")
        self.assertFalse(result["win"])
        self.assertEqual(result["steam_id"], STEAM_ID)
        self.run_file("new.run")
        self.assertEqual(self.cli("sync", "--dry-run")[1]["runs"][0]["file"], "new.run")
        self.assertEqual({p.name: p.read_bytes() for p in self.directory.iterdir()}, before)
        self.assertEqual(self.requests, [])

    def test_sync_uploads_new_wins_and_losses_but_not_backlog_or_other_profiles(self):
        self.run_file("old.run", win=True)
        self.init()
        self.run_file("a-loss.run")
        self.run_file("b-win.run", win=True)
        self.run_file("not-final.run.tmp")
        other = self.root / "other.run"
        other.write_bytes((self.history / "a-loss.run").read_bytes())
        code, result = self.cli("sync")
        self.assertEqual(code, 0)
        self.assertEqual([r["file"] for r in result["runs"]], ["a-loss.run", "b-win.run"])
        self.assertEqual([json.loads(r[2])["win"] for r in self.requests], [False, True])
        with self.assertRaisesRegex(ValueError, "initialized history"):
            codex.upload(self.directory, self.state(), other, explicit=True)
        self.assertEqual(self.state()["runs"]["old.run"]["status"], "excluded")

    def test_partial_and_recent_files_wait_then_upload_after_atomic_rename(self):
        self.init()
        recent = self.run_file("recent.run")
        os.utime(recent, (1000000, 1000000))
        partial = self.history / "partial.run"
        partial.write_text('{"players":')
        os.utime(partial, (999990, 999990))
        self.assertEqual([r["status"] for r in self.cli("sync")[1]["runs"]], ["waiting", "waiting"])
        self.assertEqual(self.state()["runs"], {})
        self.assertEqual(self.requests, [])
        replacement = self.run_file("save.tmp")
        replacement.replace(partial)
        self.clock.return_value = 1000003
        self.assertEqual(self.cli("sync")[0], 0)
        self.assertEqual(len(self.requests), 2)

    def test_429_backoff_survives_restart_and_blocks_other_new_uploads(self):
        self.init()
        self.run_file("first.run")
        self.run_file("second.run")
        self.responses.append((429, {"detail": "slow down"}, {"Retry-After": "180"}))
        self.assertEqual(self.cli("sync")[0], 1)
        self.assertEqual(self.state()["retry_at"], 1000180)
        self.assertEqual(self.state()["runs"]["first.run"]["status"], "retry")
        self.clock.return_value = 1000179
        self.assertEqual(self.cli("sync")[1]["runs"][0]["status"], "deferred")
        self.assertEqual(len(self.requests), 1)
        self.clock.return_value = 1000180
        self.assertEqual(self.cli("sync")[0], 0)
        self.assertEqual(len(self.requests), 3)
        self.assertEqual(self.state()["runs"]["first.run"]["attempts"], 2)
        self.assertNotIn("retry_at", self.state())

    def test_transient_errors_and_unconfirmed_success_remain_retryable(self):
        self.init()
        path = self.run_file()
        cases = [(503, {"detail": "unavailable"}, {}),
                 (200, {"success": True, "run_hash": "abc", "url": ACK["url"]}, {}),
                 (200, {**ACK, "url": "https://other.invalid/run"}, {})]
        for index, response in enumerate(cases):
            self.responses.append(response)
            self.assertEqual(self.cli("upload", path)[1]["status"], "retry")
            self.assertEqual(self.state()["retry_at"], self.clock.return_value + 60 * 2 ** index)
            self.clock.return_value = self.state()["retry_at"]
        with patch("codex.urlopen", side_effect=TimeoutError("response lost")):
            self.assertEqual(self.cli("upload", path)[1]["status"], "retry")
        self.clock.return_value = self.state()["retry_at"]
        self.responses.append((200, {**ACK, "duplicate": True}, {}))
        self.assertEqual(self.cli("upload", path)[1]["status"], "uploaded")

    def test_permanent_rejection_does_not_loop_or_block_other_runs(self):
        self.init()
        self.run_file("a-rejected.run")
        self.run_file("b-good.run")
        self.responses.append((400, {"detail": "bad run"}, {}))
        code, result = self.cli("sync")
        self.assertEqual(code, 1)
        self.assertEqual([r["status"] for r in result["runs"]], ["rejected", "uploaded"])
        self.assertEqual(self.cli("sync"), (0, {"runs": []}))
        self.assertEqual(len(self.requests), 2)

    def test_local_validation_and_size_boundary_never_send_invalid_data(self):
        self.init()
        self.run_file("bad.run", map_point_history=[])
        path = self.run_file("sized.run")
        body = path.read_bytes()
        path.write_bytes(body + b" " * (512 * 1024 - len(body)))
        os.utime(path, (999990, 999990))
        self.assertEqual(len(codex.read_run(path)[0]), 512 * 1024)
        with path.open("ab") as stream:
            stream.write(b" ")
        os.utime(path, (999990, 999990))
        self.assertEqual([r["status"] for r in self.cli("sync")[1]["runs"]], ["rejected", "rejected"])
        self.assertEqual(self.requests, [])

    def test_exclusive_lock_and_atomic_state_preserve_previous_acknowledgements(self):
        self.init()
        with codex.state_lock(self.directory):
            with self.assertRaisesRegex(RuntimeError, "Another uploader"):
                with codex.state_lock(self.directory):
                    self.fail("second writer acquired the lock")
        before = (self.directory / "state.json").read_bytes()
        with patch("codex.os.replace", side_effect=OSError("disk failure")):
            with self.assertRaises(OSError):
                codex.save_state(self.directory, {"would": "lose acknowledgements"})
        self.assertEqual((self.directory / "state.json").read_bytes(), before)
        self.assertEqual(sorted(p.name for p in self.directory.iterdir()), ["lock", "state.json"])

    def test_retry_after_supports_http_date_and_invalid_values(self):
        self.assertEqual(codex.retry_seconds("Mon, 12 Jan 1970 13:48:20 GMT"), 100)
        self.assertEqual(codex.retry_seconds("-10"), 0)
        self.assertEqual(codex.retry_seconds("bad"), 0)
        self.assertEqual(codex.retry_seconds(None), 0)


if __name__ == "__main__":
    unittest.main()
