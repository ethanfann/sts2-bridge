"""Decision context checked against ordinary actions in the real game."""

import json
from pathlib import Path
import subprocess
import sys
import time
from bridge_test import read_json, send_command, wait_for, wait_state


def combat_cards(state):
    return state["hand"] + [card for pile in ("draw", "discard", "exhaust", "play")
                            for card in state["piles"][pile]]


def fan(state):
    return next(r for r in state["player"]["relics"] if r["model_id"] == "ORNAMENTAL_FAN")


def exercise_combat(process, sandbox, path, output):
    bridge = path.parent

    def settled(description, predicate):
        state = wait_state(process, path, description, lambda s:
                           s["scene"] == "combat" and s["waiting_for_input"] and predicate(s))
        (output / f"{description}.json").write_text(json.dumps(state, indent=2))
        return state

    initial = settled("combat-initial", lambda s:
                      len(s["hand"]) == 5 and len(s["player"]["powers"]) == 4
                      and len(s["enemies"][0]["powers"]) == 2)
    assert initial["combat"]["player_turn"] == 1 and not initial["cards_played"]
    assert initial["player"]["energy"] == 3
    assert len(initial["deck"]) == initial["player"]["deck_count"] == 10
    deck_ids = {card["id"] for card in initial["deck"]}
    all_cards = combat_cards(initial)
    all_ids = {card["id"] for card in all_cards}
    assert len(all_cards) == len(all_ids) == 11, "Duplicate types need distinct instance IDs"
    assert not all_ids & deck_ids, "Combat copies are distinct from permanent deck cards"
    assert {c["deck_card_id"] for c in all_cards if "deck_card_id" in c} == deck_ids
    assert initial["piles"]["draw_order_known"] is False
    for pile in ("draw", "discard", "exhaust", "play"):
        assert len(initial["piles"][pile]) == initial[f"{pile}_pile_count"]
    assert all("{" not in c["description"] for c in all_cards)
    assert all(p["descriptions"] and all("{" not in d for d in p["descriptions"])
               for p in initial["player"]["powers"] + initial["enemies"][0]["powers"])
    assert fan(initial)["counter"] == 0
    assert "4" in fan(initial)["description"] and "3" in fan(initial)["description"]
    enemy = initial["enemies"][0]
    assert enemy["intents"][0]["damage_per_hit"] == 3  # floor(4 × Weak's 0.75)
    assert enemy["intents"][0]["hits"] == 1 and enemy["intents"][0]["total_damage"] == 3

    (sandbox / "fixture-guard").touch()
    wait_for(process, "live-state guard checks", lambda: (sandbox / "fixture-guard-checked").exists())
    # Reversing the secret draw sequence must not change the exported membership.
    (sandbox / "fixture-reverse-draw").touch()
    wait_for(process, "reverse draw fixture", lambda: (sandbox / "fixture-reversed").exists())
    time.sleep(3.5)  # Include an export fallback poll, even if no event was emitted.
    assert read_json(path)["piles"]["draw"] == initial["piles"]["draw"]
    assert read_json(path)["player"] == initial["player"], "Observation must not spend resources"

    strike = next(c for c in initial["hand"] if c["model_id"] == "STRIKE_IRONCLAD" and c["upgrade_level"] == 1)
    defend = next(c for c in initial["hand"] if c["model_id"] == "DEFEND_IRONCLAD")
    slimed = next(c for c in initial["hand"] if c["model_id"] == "SLIMED")
    bash = next(c for c in initial["hand"] if c["model_id"] == "BASH")
    assert any(t.get("title") == "Vulnerable" and "50" in t["description"] for t in bash["hover_tips"])
    assert any(t.get("title") == "Block" for t in defend["hover_tips"])
    assert any(t.get("title") == "Exhaust" for t in slimed["hover_tips"])
    assert any(t.get("title") == "Block" for t in fan(initial)["hover_tips"])
    # The temporary exhaust override (e.g. Havoc) is distinct from the keyword.
    assert "deck_card_id" not in slimed and "Exhaust" in slimed["keywords"]
    assert slimed["exhaust_on_next_play"] is False
    assert strike["base_values"]["Damage"] == 9
    assert strike["play_targets"] == [{"target_id": enemy["id"], "values": {"Damage": 13.5}}]
    assert defend["play_targets"][0]["values"]["Block"] == 5.25  # (5 + 2) × 0.75
    assert defend["play_targets"][0].get("target_id") is None

    rejected = send_command(process, bridge, "play_card", expected_status="error",
                            expected_state_id="old-session", card_id=strike["id"], target_id=enemy["id"])
    assert "stale" in rejected["message"] and read_json(path)["player"] == initial["player"]
    assert initial["command_guards"] == ["expected_state_id"]
    send_command(process, bridge, "play_card", expected_state_id=initial["state_id"],
                 card_id=strike["id"], target_id=enemy["id"])
    after_strike = settled("after-strike", lambda s: len(s["cards_played"]) == 1)
    assert after_strike["enemies"][0]["hp"] == enemy["hp"] - 13
    assert after_strike["piles"]["discard"][0]["id"] == strike["id"]
    assert fan(after_strike)["counter"] == 1
    stale = send_command(process, bridge, "play_card", expected_status="error",
                         expected_state_id=initial["state_id"], card_id=defend["id"])
    assert "stale" in stale["message"] and read_json(path)["player"]["block"] == 0
    send_command(process, bridge, "play_card", card_id=defend["id"])
    after_defend = settled("after-defend", lambda s: len(s["cards_played"]) == 2)
    assert after_defend["player"]["block"] == 5 and after_defend["player"]["energy"] == 1
    unavailable = next(c for c in after_defend["hand"] if c["id"] == bash["id"])
    assert unavailable["energy_cost"] == 2 and not unavailable["playable"] and not unavailable["play_targets"]
    send_command(process, bridge, "play_card", expected_status="error", card_id=bash["id"], target_id=enemy["id"])
    send_command(process, bridge, "play_card", card_id=slimed["id"])
    after_slimed = settled("after-slimed", lambda s: len(s["cards_played"]) == 3)
    assert after_slimed["player"]["energy"] == 0
    assert after_slimed["piles"]["exhaust"][0]["id"] == slimed["id"]
    assert after_slimed["draw_pile_count"] == initial["draw_pile_count"] - 1
    assert not any(c["playable"] or c["play_targets"] for c in after_slimed["hand"])
    assert [p["card_id"] for p in after_slimed["cards_played"]] == [strike["id"], defend["id"], slimed["id"]]
    assert [p["energy_spent"] for p in after_slimed["cards_played"]] == [1, 1, 1]
    assert all(p["this_turn"] and not p["auto_play"] for p in after_slimed["cards_played"])

    send_command(process, bridge, "end_turn")
    second = settled("turn-two", lambda s: s["combat"]["player_turn"] == 2)
    assert second["player"]["hp"] == initial["player"]["hp"]
    assert len(second["cards_played"]) == 3, "Unplayed end-turn discards are not plays"
    assert all(not p["this_turn"] and p["last_player_turn"] for p in second["cards_played"])
    assert second["enemies"][0]["intents"][0]["type"] == "Buff"
    assert "total_damage" not in second["enemies"][0]["intents"][0]
    assert fan(second)["counter"] == 0
    send_command(process, bridge, "end_turn")
    third = settled("turn-three", lambda s: s["combat"]["player_turn"] == 3)
    assert {c["id"] for c in combat_cards(third)} == all_ids, "Identity must survive reshuffle"
    assert third["enemies"][0]["intents"][0]["total_damage"] == 11  # 4 + 7 Strength
    assert all(not p["this_turn"] and not p["last_player_turn"] for p in third["cards_played"])

    (sandbox / "fixture-intents").touch()
    intents = settled("intent-edge-cases", lambda s: len(s["enemies"][0]["intents"]) == 3)["enemies"][0]["intents"]
    assert (intents[0]["damage_per_hit"], intents[0]["hits"], intents[0]["total_damage"]) == (12, 3, 36)
    assert all("damage_per_hit" not in i and "total_damage" not in i for i in intents[1:])

    (sandbox / "fixture-next-combat").touch()
    wait_for(process, "second combat initialized", lambda: (sandbox / "fixture-reset").exists())
    reset = settled("combat-reset", lambda s: s["combat"]["player_turn"] == 1
                    and sum(c["model_id"] == "ANGER" for c in s["hand"]) == 3)
    assert not reset["cards_played"] and not reset["piles"]["exhaust"]
    assert {c["id"] for c in reset["deck"]} == deck_ids
    assert not {c["id"] for c in combat_cards(reset)} & all_ids
    assert fan(reset)["counter"] == 0
    target = reset["enemies"][0]["id"]
    # A real after-play relic effect that is absent from Attack card previews.
    # Dexterity +2 / Frail must NOT turn the Fan's unpowered 4 block into 5.
    attacks = [c for c in reset["hand"] if c["model_id"] == "ANGER"]
    for card in attacks:
        assert "Block" not in card["play_targets"][0]["values"]
    plays = [{"card_id": card["id"], "target_id": target} for card in attacks]
    sequence = subprocess.run([
        sys.executable, str(Path(__file__).resolve().parent.parent / "bridge.py"),
        "--bridge-dir", str(bridge), "play-sequence", json.dumps(plays),
        "--if-state", reset["state_id"], "--view", "combat"],
        capture_output=True, text=True, timeout=45)
    assert sequence.returncode == 0, sequence.stdout + sequence.stderr
    result = json.loads(sequence.stdout)
    assert result["status"] == "completed" and len(result["steps"]) == 3
    assert all(step["completion_observed"] for step in result["steps"])
    state = settled("fan-sequence", lambda s: len(s["cards_played"]) == 3 and fan(s)["counter"] == 0)
    assert [entry["card_id"] for entry in state["cards_played"]] == [c["id"] for c in attacks]
    assert state["enemies"][0]["hp"] == reset["enemies"][0]["hp"] - 18
    assert state["player"]["block"] == 4 and state["player"]["energy"] == reset["player"]["energy"]

    finisher = next(c for c in state["hand"] if c["model_id"] == "BLUDGEON")
    send_command(process, bridge, "play_card", card_id=finisher["id"], target_id=target)
    rewards = wait_state(process, path, "combat rewards", lambda s: s["scene"] == "rewards")
    assert not rewards["enemies"] and rewards["rewards"]["rewards"]
    # Leave the card reward until last, matching the live run's failure. IDs can
    # change when another row is removed; always use the next observed snapshot.
    while any(r["reward_type"] != "Card" for r in rewards["rewards"]["rewards"]):
        reward = next(r for r in rewards["rewards"]["rewards"] if r["reward_type"] != "Card")
        count = len(rewards["rewards"]["rewards"])
        send_command(process, bridge, "take_reward", reward_id=reward["id"])
        rewards = wait_state(process, path, "remaining rewards", lambda s:
                             s["scene"] == "rewards" and len(s["rewards"]["rewards"]) < count)
    assert len(rewards["rewards"]["rewards"]) == 1
    reward = rewards["rewards"]["rewards"][0]
    send_command(process, bridge, "take_reward", reward_id=reward["id"])
    selection = wait_state(process, path, "last card reward selector", lambda s: s["scene"] == "card_selection")
    assert not selection.get("rewards") and not selection.get("proceed_context")
    # The underlying reward screen must not handle commands while covered.
    send_command(process, bridge, "proceed", expected_status="error")
    card = selection["card_selection"]["cards"][0]
    send_command(process, bridge, "select_card", card_id=card["id"])
    completed = wait_state(process, path, "completed rewards still open", lambda s:
                           s["screen"]["type"] == "NRewardsScreen"
                           and s["player"]["deck_count"] == selection["player"]["deck_count"] + 1)
    (output / "completed-rewards.json").write_text(json.dumps(completed, indent=2))
    assert completed["scene"] == "rewards", "Collected rewards must remain active until Proceed closes the screen"
    assert completed["waiting_for_input"] is True
    assert completed["rewards"] == {"proceed_enabled": True, "rewards": []}
    assert not completed.get("proceed_context")
    assert len([c for c in completed["deck"] if c["model_id"] == card["model_id"]]) == (
        len([c for c in selection["deck"] if c["model_id"] == card["model_id"]]) + 1)
    send_command(process, bridge, "take_reward", expected_status="error", reward_id=reward["id"])
    send_command(process, bridge, "proceed")
    after = wait_state(process, path, "map after completed rewards", lambda s: s["scene"] == "map")
    (output / "rewards-to-map.json").write_text(json.dumps(after, indent=2))
    assert not after.get("rewards") and not after.get("proceed_context")
    assert after["deck"] == completed["deck"] and after["player"]["gold"] == completed["player"]["gold"]
    send_command(process, bridge, "proceed", expected_status="error")
