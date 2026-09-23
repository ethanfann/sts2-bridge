import copy
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

import bridge


class BridgeTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.directory = Path(temporary.name)
        self.state = {
            "protocol_version": 1, "command_guards": ["expected_state_id"], "state_id": "s0",
            "scene": "combat", "screen": {"supported": True}, "waiting_for_input": True,
            "run": {"seed": "fixture", "total_floor": 7}, "combat": {"round": 2, "player_turn": 2},
            "hand": [{"id": name, "playable": True, "play_targets": [{"target_id": "e1"}]}
                     for name in ("a", "b")],
            "cards_played": [], "deck": [{"id": "permanent"}], "piles": {"draw": []},
            "future_mechanic": {"important": True},
        }
        self.plays = [{"card_id": "a", "target_id": "e1"}, {"card_id": "b", "target_id": "e1"}]
        self.save()

    def save(self):
        (self.directory / "state.json").write_text(json.dumps(self.state))

    def resolve(self, card_id):
        self.state["state_id"] += "x"
        self.state["hand"] = [card for card in self.state["hand"] if card["id"] != card_id]
        self.state["cards_played"].append({"card_id": card_id, "auto_play": False})
        self.save()

    def test_views_preserve_unknown_mechanics_and_identify_omissions(self):
        original = copy.deepcopy(self.state)
        result = bridge.observation(self.state)
        self.assertEqual(result["omitted_sections"], ["cards_played", "deck", "piles"])
        self.assertEqual(result["state"]["future_mechanic"], {"important": True})
        self.assertEqual(result["state"]["hand"], self.state["hand"])
        self.assertEqual(bridge.observation(self.state, "full")["state"], original)
        self.assertEqual(self.state, original)

    def test_transport_waits_for_matching_ack_and_consumption(self):
        (self.directory / "command-result.json").write_text('{"command_id":"old","status":"ok"}')
        ticks = []

        def advance(_):
            command = bridge.read_json(self.directory / "command.json")
            ticks.append(command)
            if len(ticks) == 1:
                (self.directory / "command-result.json").write_text(json.dumps({
                    "command_id": command["command_id"], "status": "error", "message": "Rejected"}))
            else:
                (self.directory / "command.json").unlink()

        with patch("bridge.time.sleep", side_effect=advance):
            result = bridge.send_command(self.directory, "play_card", card_id="a", target_id="e1")
        self.assertEqual(len(ticks), 2, "Matching ack alone must not release the writer")
        self.assertEqual(ticks[0], ticks[1])
        self.assertEqual(ticks[0]["card_id"], "a")
        self.assertEqual(result["status"], "error")
        self.assertFalse((self.directory / "client.lock").exists())

    def test_pending_command_and_writer_are_not_overwritten(self):
        command = self.directory / "command.json"
        command.write_text("existing payload")
        with self.assertRaises(FileExistsError):
            bridge.send_command(self.directory, "end_turn")
        self.assertEqual(command.read_text(), "existing payload")
        self.assertEqual(list(self.directory.glob("*.tmp")), [])
        with bridge.writer(self.directory):
            with self.assertRaises(FileExistsError):
                bridge.send_command(self.directory, "end_turn")
            self.assertTrue((self.directory / "client.lock").exists())

    def test_timeout_preserves_command_and_does_not_retry(self):
        with self.assertRaisesRegex(TimeoutError, "do not resend blindly"):
            bridge.send_command(self.directory, "end_turn", timeout=0.01)
        command = bridge.read_json(self.directory / "command.json")
        self.assertEqual(command["type"], "end_turn")
        with self.assertRaises(FileExistsError):
            bridge.send_command(self.directory, "end_turn")
        self.assertEqual(bridge.read_json(self.directory / "command.json"), command)

    def test_sequence_waits_for_completion_and_uses_each_new_guard(self):
        sent = []
        pending = []

        def send(directory, kind, timeout, **fields):
            self.assertEqual(fields["expected_state_id"], self.state["state_id"])
            self.assertEqual(kind, "play_card")
            self.assertEqual(pending, [], "Do not enqueue another card from an ack alone")
            with self.assertRaises(FileExistsError):
                bridge.send_command(directory, "end_turn")
            sent.append(fields["card_id"])
            pending.append(fields["card_id"])
            return {"status": "ok", "command_id": f"cmd-{len(sent)}"}

        def complete(_):
            self.resolve(pending.pop())

        with patch("bridge._send_command", side_effect=send), patch("bridge.time.sleep", side_effect=complete):
            result = bridge.play_sequence(self.directory, self.plays, if_state="s0")
        self.assertEqual(sent, ["a", "b"])
        self.assertEqual(result["status"], "completed")
        self.assertTrue(all(step["completion_observed"] for step in result["steps"]))
        self.assertEqual(result["unsubmitted"], [])

    def test_sequence_stops_on_new_decision_even_when_ack_says_ok(self):
        for reason in ("selection_required", "hand_changed", "scene_changed", "turn_changed", "history_changed"):
            with self.subTest(reason=reason):
                self.state["state_id"] = "s0"
                self.state["scene"] = "combat"
                self.state["combat"]["player_turn"] = 2
                self.state.pop("card_selection", None)
                self.state["hand"] = [{"id": name, "playable": True, "play_targets": [{"target_id": "e1"}]}
                                      for name in ("a", "b")]
                self.state["cards_played"] = [{"card_id": "earlier", "auto_play": False}]
                self.save()

                def send(*_, **fields):
                    if reason == "selection_required":
                        self.state["card_selection"] = {"kind": "combat_hand"}
                    elif reason == "scene_changed":
                        self.state["scene"] = "rewards"
                    elif reason == "turn_changed":
                        self.state["combat"]["player_turn"] = 3
                    elif reason == "history_changed":
                        self.state["cards_played"] = []  # Reload/reset, not an empty new turn.
                    else:
                        self.resolve(fields["card_id"])
                        self.state["hand"].append({"id": "drawn"})
                    self.save()
                    return {"status": "ok"}

                with patch("bridge._send_command", side_effect=send) as sender:
                    result = bridge.play_sequence(self.directory, self.plays, if_state="s0")
                self.assertEqual(sender.call_count, 1)
                self.assertEqual(result["stop_reason"], reason)
                self.assertEqual(result["unsubmitted"], [self.plays[1]])

    def test_sequence_rejection_or_timeout_keeps_partial_report(self):
        for outcome, reason in (({"status": "error", "message": "stale"}, "rejected"),
                                (TimeoutError("uncertain"), "transport_error"),
                                ({"status": "ok"}, "completion_timeout")):
            with self.subTest(reason=reason), patch("bridge._send_command") as sender:
                if isinstance(outcome, Exception):
                    sender.side_effect = outcome
                else:
                    sender.return_value = outcome
                result = bridge.play_sequence(self.directory, self.plays, if_state="s0", timeout=0.01)
                self.assertEqual(sender.call_count, 1)
                self.assertEqual(result["stop_reason"], reason)
                self.assertFalse(result["steps"][0]["completion_observed"])
                self.assertEqual(result["unsubmitted"], [self.plays[1]])

    def test_stale_or_unsupported_sequence_sends_nothing(self):
        with patch("bridge._send_command") as sender:
            with self.assertRaisesRegex(ValueError, "stale"):
                bridge.play_sequence(self.directory, self.plays, if_state="old-session")
            del self.state["command_guards"]
            self.save()
            with self.assertRaisesRegex(ValueError, "newer DLL"):
                bridge.play_sequence(self.directory, self.plays, if_state="s0")
            sender.assert_not_called()

    def test_sequence_waits_for_ready_and_stops_before_unavailable_target(self):
        def send(*_, **fields):
            self.resolve(fields["card_id"])
            self.state["waiting_for_input"] = False
            self.save()
            return {"status": "ok"}

        def ready(_):
            self.state["waiting_for_input"] = True
            # The next target died/became unhittable while the first play resolved.
            self.state["hand"][0]["play_targets"] = []
            self.save()

        with patch("bridge._send_command", side_effect=send) as sender, patch("bridge.time.sleep", side_effect=ready) as sleeper:
            result = bridge.play_sequence(self.directory, self.plays, if_state="s0")
        self.assertEqual(sender.call_count, 1)
        sleeper.assert_called_once()
        self.assertTrue(result["steps"][0]["completion_observed"])
        self.assertEqual(result["stop_reason"], "card_or_target_unavailable")
        self.assertEqual(result["unsubmitted"], [self.plays[1]])

    def test_invalid_plans_and_timeouts_send_nothing(self):
        for plays in ([], self.plays * 11, [{"card_id": "a", "type": "end_turn"}], [{"target_id": "e1"}]):
            with self.subTest(plays=plays), self.assertRaises(ValueError):
                bridge.play_sequence(self.directory, plays, if_state="s0")
        for timeout in (0, -1, float("nan"), float("inf")):
            with self.subTest(timeout=timeout), self.assertRaises(ValueError):
                bridge.play_sequence(self.directory, self.plays, if_state="s0", timeout=timeout)
        self.assertFalse((self.directory / "command.json").exists())

    def test_skill_is_self_contained_and_help_never_sends_commands(self):
        root = Path(__file__).resolve().parent.parent
        skill = root / ".agents/skills/playing-sts2"
        installed = self.directory / "installed skill"
        shutil.copytree(skill, installed, ignore=shutil.ignore_patterns("__pycache__"))
        workspace = self.directory / "unrelated workspace"
        workspace.mkdir()
        self.assertEqual((installed / "LICENSE").read_bytes(), (root / "LICENSE").read_bytes())
        # Only the copied skill is importable: neither the checkout nor cwd can
        # supply a missing script dependency. Paths with spaces must work too.
        command = [sys.executable, "-I", "-B", str(installed / "scripts/sts2_bridge.py")]
        absent = self.directory / "do not create"
        help_paths = [[], ["observe"], ["act"], ["play-sequence"]]
        help_paths += [["act", action] for action in bridge.ACTIONS]
        for help_path in help_paths:
            with self.subTest(help_path=help_path):
                result = subprocess.run(command + ["--bridge-dir", str(absent)] + help_path + ["--help"],
                                        cwd=workspace, capture_output=True, text=True)
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertIn("usage:", result.stdout)
                self.assertFalse(absent.exists(), "Help must not need the game or touch bridge files")
        result = subprocess.run(command + ["--bridge-dir", str(self.directory), "observe", "--view", "full"],
                                cwd=workspace, capture_output=True, text=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(json.loads(result.stdout)["state"], self.state)
        self.assertFalse((self.directory / "command.json").exists())
        self.assertFalse((self.directory / "client.lock").exists())
        self.assertEqual(list(workspace.iterdir()), [])

    def test_cli_observe_and_invalid_play(self):
        checkout_entry = Path(__file__).resolve().parent.parent / "bridge.py"
        for script in (checkout_entry, Path(bridge.__file__)):
            with self.subTest(script=script):
                command = [sys.executable, str(script), "--bridge-dir", str(self.directory)]
                result = subprocess.run(command + ["observe", "--view", "deck"], cwd=self.directory,
                                        capture_output=True, text=True)
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertEqual(json.loads(result.stdout)["state"]["deck"], [{"id": "permanent"}])
                invalid = subprocess.run(command + ["act", "play_card"], cwd=self.directory,
                                         capture_output=True, text=True)
                self.assertEqual(invalid.returncode, 2)
                self.assertFalse((self.directory / "command.json").exists())


if __name__ == "__main__":
    unittest.main()
