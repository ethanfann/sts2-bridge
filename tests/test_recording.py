import json
from pathlib import Path
import tempfile
import unittest

import recording


class RecordingTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.bridge = Path(self.temporary.name)

    def test_summary_reads_all_segments_and_reports_incomplete_tail(self):
        session = self.bridge / "recordings/test-session"
        session.mkdir(parents=True)
        (self.bridge / "latest-session.json").write_text(json.dumps({
            "session_id": "test-session", "started_at": "start"}))
        (session / "timeline-0001.jsonl").write_text(json.dumps({
            "type": "state", "data": {"scene": "unsupported", "timestamp": "earlier",
                                       "screen": {"supported": False, "type": "NCrystalSphereScreen"}}}) + "\n")
        entries = [
            {"type": "marker", "timestamp": "middle", "data": {"note": "manual intervention"}},
            {"type": "state", "data": {"scene": "event", "timestamp": "later"}},
            {"type": "export_error", "data": {"error": "example"}},
            {"type": "session_end", "data": {}},
        ]
        (session / "timeline-0002.jsonl").write_text(
            "".join(json.dumps(e) + "\n" for e in entries) + '{"partial":')
        result = recording.status(self.bridge)
        self.assertEqual(result["counts"], {"state": 2, "marker": 1, "export_error": 1, "session_end": 1})
        self.assertEqual(result["unsupported_screens"], ["NCrystalSphereScreen"])
        self.assertEqual(result["last_snapshot"]["scene"], "event")
        self.assertEqual(result["last_snapshot"]["timestamp"], "later")
        self.assertEqual(result["markers"], [{"timestamp": "middle", "note": "manual intervention"}])
        self.assertTrue(result["clean_exit_recorded"])
        self.assertEqual(result["partial_lines"], 1)

    def test_marker_does_not_overwrite_another_command(self):
        command = self.bridge / "command.json"
        command.write_text('{"type":"end_turn","command_id":"other-client"}')
        before = command.read_bytes()
        with self.assertRaises(FileExistsError):
            recording.mark(self.bridge, "my note")
        self.assertEqual(command.read_bytes(), before)
        self.assertEqual(list(self.bridge.glob("*.tmp")), [])

    def test_marker_ignores_stale_acknowledgement_and_retains_pending_command(self):
        (self.bridge / "command-result.json").write_text(json.dumps({
            "command_id": "old-marker", "status": "ok"}))
        with self.assertRaises(TimeoutError):
            recording.mark(self.bridge, "new marker", timeout=0.01)
        command = json.loads((self.bridge / "command.json").read_text())
        self.assertEqual(command["type"], "mark")
        self.assertEqual(command["note"], "new marker")
        self.assertNotEqual(command["command_id"], "old-marker")
        self.assertEqual(list(self.bridge.glob("*.tmp")), [])


if __name__ == "__main__":
    unittest.main()
