#!/usr/bin/env python3
"""Read STS2 Bridge observations and issue actions. Python 3.8+; no dependencies."""

import argparse
from contextlib import contextmanager
import json
import math
import os
from pathlib import Path
import time
import uuid


# Required and optional fields, using the mod's protocol names unchanged.
ACTIONS = {
    "play_card": (("card_id",), ("target_id",)),
    "end_turn": ((), ()),
    "proceed": ((), ()),
    "enter_merchant": ((), ()),
    "leave_shop": ((), ()),
    "open_treasure_chest": ((), ()),
    "select_choice": (("choice_id",), ()),
    "select_map_point": (("point_id",), ()),
    "select_card": (("card_id",), ()),
    "confirm_card_selection": ((), ()),
    "select_card_reward_alternative": (("option_id",), ()),
    "select_relic": (("relic_id",), ()),
    "take_reward": (("reward_id",), ()),
    "skip_reward": (("reward_id",), ()),
    "purchase_shop_item": (("item_id",), ()),
    "select_rest_site_option": (("option_id",), ()),
    "use_potion": (("potion_id",), ("target_id",)),
    "discard_potion": (("potion_id",), ()),
    "select_crystal_sphere_tool": (("tool_id",), ()),
    "reveal_crystal_sphere_cell": (("cell_id",), ()),
    "mark": (("note",), ()),
}

VIEWS = {
    # Omit only known, explicitly requested sections. New/unknown fields survive.
    "decision": {"deck", "piles", "cards_played"},
    "combat": {"deck", "map"},
    "choices": {"deck", "piles", "cards_played", "hand", "enemies"},
    "deck": {"piles", "cards_played", "hand", "enemies", "map"},
    "full": set(),
}


def default_bridge_dir():
    return Path(os.environ.get("XDG_DATA_HOME") or Path.home() / ".local/share") / "SlayTheSpire2/sts2-bridge"


def read_json(path):
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (FileNotFoundError, json.JSONDecodeError):
        return None


def read_state(directory):
    state = read_json(directory / "state.json")
    if not isinstance(state, dict) or state.get("protocol_version") != 1 or not state.get("state_id"):
        raise ValueError("No valid protocol-v1 state.json. Check the game and --bridge-dir.")
    return state


def observation(state, view="decision"):
    omitted = VIEWS[view].intersection(state)
    return {"state": {key: value for key, value in state.items() if key not in omitted},
            "omitted_sections": sorted(omitted),
            "note": "Saved observation, not a liveness or action-completion guarantee."}


def positive_seconds(value):
    seconds = float(value)
    if not math.isfinite(seconds) or seconds <= 0:
        raise ValueError("Timeout must be a finite positive number of seconds.")
    return seconds


@contextmanager
def writer(directory):
    """Cooperative ownership for a whole action/sequence, including observation."""
    lock = directory / "client.lock"
    try:
        lock.mkdir()
    except FileExistsError:
        raise FileExistsError("Another client holds client.lock. Do not remove a live client's lock.") from None
    try:
        yield
    finally:
        lock.rmdir()


def send_command(directory, command_type, timeout=5, **fields):
    """Send once; return the consumed acknowledgement, including game rejections."""
    with writer(directory):
        return _send_command(directory, command_type, timeout, **fields)


def _send_command(directory, command_type, timeout, **fields):
    timeout = positive_seconds(timeout)
    if {"command_id", "type"}.intersection(fields):
        raise ValueError("command_id and type are owned by the transport.")
    command_id = f"client-{uuid.uuid4()}"
    pending = directory / f"{command_id}.tmp"
    result = None
    try:
        pending.write_text(json.dumps({"command_id": command_id, "type": command_type, **fields}),
                           encoding="utf-8")
        # Atomic no-overwrite publication on the same filesystem.
        os.link(pending, directory / "command.json")
    finally:
        pending.unlink(missing_ok=True)
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        candidate = read_json(directory / "command-result.json")
        if isinstance(candidate, dict) and candidate.get("command_id") == command_id:
            result = candidate
            if not (directory / "command.json").exists():
                return result
        time.sleep(0.05)
    detail = "no matching acknowledgement" if result is None else f"acknowledged {result.get('status')}, command not consumed"
    # Never delete command.json on failure: it may already be executing.
    raise TimeoutError(f"Command {command_id}: {detail}. Outcome may be incomplete; do not resend blindly.")


def require_guard(state):
    if "expected_state_id" not in state.get("command_guards", []):
        raise ValueError("This action needs a newer DLL advertising expected_state_id guards.")


def act(directory, command_type, *, view="decision", timeout=5, if_state=None, **fields):
    timeout = positive_seconds(timeout)
    if view not in VIEWS:
        raise ValueError(f"Unknown observation view: {view}")
    required, optional = ACTIONS[command_type]
    if set(fields) - set(required + optional) or any(not fields.get(key) for key in required):
        raise ValueError(f"Invalid fields for {command_type}; required: {required}, optional: {optional}.")
    with writer(directory):
        before = read_state(directory)
        if if_state is not None:
            require_guard(before)
            fields["expected_state_id"] = if_state
        result = _send_command(directory, command_type, timeout, **fields)
        state = before
        # Bound the observation wait separately; rejection need not change state.
        deadline = time.monotonic() + min(timeout, 2)
        while True:
            candidate = read_json(directory / "state.json")
            if isinstance(candidate, dict) and candidate.get("protocol_version") == 1 and candidate.get("state_id"):
                state = candidate
            changed = state["state_id"] != before["state_id"]
            if changed or result.get("status") != "ok" or time.monotonic() >= deadline:
                break
            time.sleep(0.05)
        return {"result": result, "state_changed": changed,
                "observation": observation(state, view), "effects_settled": None}


def sequence_boundary(state, initial):
    if state.get("card_selection") or state.get("relic_selection"):
        return "selection_required"
    if state.get("scene") != "combat" or (state.get("run") or {}).get("is_game_over"):
        return "scene_changed"
    if state.get("run") != initial.get("run"):
        return "run_changed"
    current, original = state.get("combat") or {}, initial.get("combat") or {}
    if any(current.get(key) != original.get(key) for key in ("round", "player_turn")):
        return "turn_changed"
    if state.get("screen", {}).get("supported") is not True:
        return "unsupported_screen"
    return None


def play_sequence(directory, plays, *, if_state, timeout=10, view="decision"):
    """Execute a bounded same-turn plan, stopping at a new decision boundary.

    Each step is still an ordinary guarded play_card command. No rollback,
    automatic retries, automatic end-turn, or automatic selector choices.
    """
    timeout = positive_seconds(timeout)
    if view not in VIEWS:
        raise ValueError(f"Unknown observation view: {view}")
    if not isinstance(plays, list) or not 1 <= len(plays) <= 20:
        raise ValueError("A sequence requires 1–20 card plays.")
    for play in plays:
        if (not isinstance(play, dict) or set(play) - {"card_id", "target_id"}
                or not isinstance(play.get("card_id"), str) or not play["card_id"]
                or (play.get("target_id") is not None and not isinstance(play["target_id"], str))):
            raise ValueError("Each play needs card_id and an optional target_id; no other fields.")
    with writer(directory):
        initial = state = read_state(directory)
        require_guard(initial)
        if initial["state_id"] != if_state:
            raise ValueError("Sequence state is stale. Nothing was sent; observe and replan.")
        steps = []

        def finish(reason):
            return {"status": "completed" if reason is None else "stopped",
                    "stop_reason": reason, "steps": steps, "unsubmitted": plays[len(steps):],
                    "observation": observation(state, view),
                    "note": "Partial execution is not rolled back. Never resubmit the entire sequence blindly."}

        for play in plays:
            reason = sequence_boundary(state, initial)
            if reason or not state.get("waiting_for_input"):
                return finish(reason or "not_ready")
            card = next((card for card in state["hand"] if card["id"] == play["card_id"]), None)
            if (card is None or not card.get("playable")
                    or not any(target.get("target_id") == play.get("target_id") for target in card.get("play_targets", []))):
                return finish("card_or_target_unavailable")
            before = state
            step = {"play": play, "completion_observed": False}
            steps.append(step)
            try:
                step["result"] = _send_command(directory, "play_card", timeout,
                                               expected_state_id=before["state_id"], **play)
            except OSError as error:
                step["error"] = str(error)
                return finish("transport_error")
            if step["result"].get("status") != "ok":
                state = read_json(directory / "state.json") or state
                return finish("rejected")
            deadline = time.monotonic() + timeout
            while time.monotonic() < deadline:
                try:
                    state = read_state(directory)
                except (OSError, ValueError) as error:
                    step["error"] = str(error)
                    return finish("observation_unavailable")
                reason = sequence_boundary(state, initial)
                if reason:
                    return finish(reason)
                history = state.get("cards_played", [])
                old_history = before.get("cards_played", [])
                if history[:len(old_history)] != old_history:
                    return finish("history_changed")
                new_plays = history[len(old_history):]
                if any(entry["card_id"] != play["card_id"] for entry in new_plays):
                    return finish("unexpected_play")
                completed = any(entry["card_id"] == play["card_id"] and not entry["auto_play"] for entry in new_plays)
                if completed and state.get("waiting_for_input"):
                    step["completion_observed"] = True
                    step["state_id"] = state["state_id"]
                    expected_hand = {card["id"] for card in before["hand"]} - {play["card_id"]}
                    if {card["id"] for card in state["hand"]} != expected_hand:
                        return finish("hand_changed")
                    break
                time.sleep(0.05)
            else:
                return finish("completion_timeout")
        return finish(None)


def main():
    parser = argparse.ArgumentParser(description=__doc__, epilog=(
        "Run from any working directory using the script's absolute path. "
        "The game log prints 'STS2 Bridge writing state to .../state.json'; "
        "use its parent directory as --bridge-dir, especially outside native Linux. "
        "This option selects existing data; it does not configure or start the mod. "
        "Discover syntax with observe --help, act --help, act play_card --help, "
        "and play-sequence --help. Help is read-only; do not probe actions by "
        "omitting arguments (act end_turn executes). Commands return JSON. "
        "Exit codes: 0 success, 1 error or stopped sequence, 2 argument error."))
    parser.add_argument("--bridge-dir", type=Path, default=default_bridge_dir(), help=(
        "Bridge data directory, NOT the mods or skill folder (default: %(default)s). "
        "Put before the subcommand. Relative paths resolve from the working directory."))
    view_help = "Observation view (default: %(default)s). Omitted sections: " + "; ".join(
        f"{view}: {', '.join(sorted(omitted)) or 'none'}" for view, omitted in VIEWS.items())
    commands = parser.add_subparsers(dest="command", required=True)
    observe_parser = commands.add_parser("observe", help="Read saved state; works offline too", description=(
        "Read a saved observation without sending a command. A snapshot may persist "
        "after the game exits; it is not proof of liveness. JSON includes state and "
        "omitted_sections. Use combat for piles/history, deck for the permanent deck, "
        "or full for all context. Unknown fields and mechanics are preserved."))
    observe_parser.add_argument("--view", choices=VIEWS, default="decision", help=view_help)
    act_parser = commands.add_parser("act", help="Send one action and observe; never retries", description=(
        "Send one action and return result plus observation. Use current instance IDs "
        "and action eligibility from observe, not model names or array positions. "
        "An ok result is acceptance, NOT settled effects (effects_settled is null). "
        "A changed state_id alone is not completion. Never blindly retry a timeout. "
        "Place --view, --timeout, and --if-state before the action name."))
    act_parser.add_argument("--view", choices=VIEWS, default="decision", help=view_help)
    act_parser.add_argument("--timeout", type=positive_seconds, default=5, help=(
        "Acknowledgement wait in seconds (default: %(default)s); followed by an "
        "observation wait of up to 2 seconds, capped at this timeout"))
    act_parser.add_argument("--if-state", help=(
        "Observed state_id; require the DLL to reject stale state before execution. "
        "Requires expected_state_id in the observation's command_guards."))
    sequence_parser = commands.add_parser("play-sequence", help="Play cards in order; stop at new decisions", description=(
        "Submit 1-20 card plays sequentially under one cooperative writer lock. "
        "Each play checks current eligibility and a fresh DLL state guard, then waits "
        "for completed manual-play history AND input readiness. Requires a ready, "
        "supported combat observation advertising expected_state_id in command_guards. "
        "Stops on rejection/timeout, selectors, room/turn changes, unexpected plays, "
        "or hand changes beyond removing the played card (including draw/recovery). "
        "Never ends turns, answers selectors, retries, or rolls back. JSON reports "
        "status, stop_reason, attempted steps with completion_observed, unsubmitted "
        "plays, and observation. Inspect partial results; do not replay the whole plan."))
    sequence_parser.add_argument("plays", help=(
        'JSON array: [{"card_id":"...","target_id":"..."}, ...]. Use current instance IDs; '
        'omit target_id for a targetless play. Each entry is a card play, not an arbitrary action.'))
    sequence_parser.add_argument("--if-state", required=True, help="State ID used to plan this sequence")
    sequence_parser.add_argument("--timeout", type=positive_seconds, default=10, help=(
        "Seconds for each acknowledgement and each completion wait separately "
        "(default: %(default)s for each wait per play)"))
    sequence_parser.add_argument("--view", choices=VIEWS, default="decision", help=view_help)
    action_parsers = act_parser.add_subparsers(dest="action", required=True)
    for name, (required, optional) in ACTIONS.items():
        action_parser = action_parsers.add_parser(name, epilog=(
            f"Parent options go before the action: act --if-state '<state_id>' {name} ... . "
            "Read act --help for result and timeout semantics."))
        for field in required + optional:
            action_parser.add_argument(f"--{field.replace('_', '-')}", required=field in required,
                                       help="Recording annotation; no gameplay action" if field == "note" else
                                       "Instance/option ID from the current observation, not a model name or index")
    args = parser.parse_args()
    try:
        if args.command == "observe":
            response = observation(read_state(args.bridge_dir), args.view)
        elif args.command == "play-sequence":
            response = play_sequence(args.bridge_dir, json.loads(args.plays), if_state=args.if_state,
                                     timeout=args.timeout, view=args.view)
        else:
            required, optional = ACTIONS[args.action]
            fields = {key: getattr(args, key) for key in required + optional if getattr(args, key) is not None}
            response = act(args.bridge_dir, args.action, view=args.view, timeout=args.timeout,
                           if_state=args.if_state, **fields)
    except (OSError, ValueError) as error:
        print(json.dumps({"error": str(error)}))
        return 1
    print(json.dumps(response, indent=2))
    return int((args.command == "act" and response["result"].get("status") != "ok")
               or (args.command == "play-sequence" and response["status"] != "completed"))


if __name__ == "__main__":
    raise SystemExit(main())
