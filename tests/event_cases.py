"""Known event paths; each case starts a fresh, seeded, unsaved run."""

import json
import re
import time
from bridge_test import read_json, send_command, wait_for, wait_state
from combat_case import exercise_combat
from pile_selection_case import exercise_pile_selection
from hand_selection_case import exercise_hand_selection
from upgrade_case import exercise_upgrade
from deck_selection_case import exercise_deck_selection
from shop_rest_case import exercise_shop_rest
from fake_merchant_case import exercise_fake_merchant
from treasure_case import exercise_treasure
from map_case import exercise_map
from potion_reward_case import exercise_potion_rewards
from card_reward_case import exercise_card_rewards
from crystal_sphere_case import exercise_crystal_sphere

CASE_EVENTS = {
    "neow-mechanics": "NEOW",
    "morphic-loner": "MORPHIC_GROVE",
    "wellspring-bottle": "WELLSPRING",
    "wellspring-bathe": "WELLSPRING",
    "crystal-sphere": "CRYSTAL_SPHERE",
    "crystal-sphere-gold": "CRYSTAL_SPHERE",
    "battleworn-dummy": "BATTLEWORN_DUMMY",
    "round-tea-party": "ROUND_TEA_PARTY",
    "architect-dialogue": "THE_ARCHITECT",
    "combat-context": "COMBAT_CONTEXT",
    "combat-pile-selection": "COMBAT_PILE_SELECTION",
    "combat-hand-selection": "COMBAT_HAND_SELECTION",
    "potion-rewards": "POTION_REWARDS",
    "card-rewards": "CARD_REWARDS",
    "deck-upgrade": "DECK_UPGRADE",
    "deck-selection": "DECK_SELECTION",
    "shop-rest-context": "SHOP_REST_CONTEXT",
    "fake-merchant-buy": "FAKE_MERCHANT",
    "fake-merchant-skip": "FAKE_MERCHANT",
    "treasure-take": "TREASURE_CONTEXT",
    "treasure-skip": "TREASURE_CONTEXT",
    "treasure-empty": "TREASURE_EMPTY",
    "map-normal": "MAP_NORMAL",
    "map-boss": "MAP_BOSS",
}


def exercise_event(process, sandbox, path, case, output):
    def ready():
        error = sandbox / "fixture-error.txt"
        if error.exists():
            raise RuntimeError(error.read_text())
        return read_json(sandbox / "fixture-ready.json")

    wait_for(process, "fixture initialization", ready, timeout=60)
    if case == "combat-context":
        exercise_combat(process, sandbox, path, output)
        return
    if case == "combat-pile-selection":
        exercise_pile_selection(process, sandbox, path, output)
        return
    if case == "combat-hand-selection":
        exercise_hand_selection(process, sandbox, path, output)
        return
    if case == "potion-rewards":
        exercise_potion_rewards(process, sandbox, path, output)
        return
    if case == "card-rewards":
        exercise_card_rewards(process, sandbox, path, output)
        return
    if case == "deck-upgrade":
        exercise_upgrade(process, sandbox, path, output)
        return
    if case == "deck-selection":
        exercise_deck_selection(process, sandbox, path, output)
        return
    if case == "shop-rest-context":
        exercise_shop_rest(process, sandbox, path, output)
        return
    if case in ("fake-merchant-buy", "fake-merchant-skip"):
        exercise_fake_merchant(process, sandbox, path, case, output)
        return
    if case in ("treasure-take", "treasure-skip", "treasure-empty"):
        exercise_treasure(process, sandbox, path, case, output)
        return
    if case in ("map-normal", "map-boss"):
        exercise_map(process, sandbox, path, case, output)
        return
    if case in ("crystal-sphere", "crystal-sphere-gold"):
        exercise_crystal_sphere(process, sandbox, path, case, output)
        return
    event_id = CASE_EVENTS[case]
    neow_relics = {"SCROLL_BOXES", "NEOWS_TORMENT", "SILKEN_TRESS"}
    initial = wait_state(process, path, "initial event choices", lambda s:
                         s.get("run", {}).get("event_id") == event_id and bool(s.get("choices"))
                         and (case != "neow-mechanics" or
                              {c.get("relic_model_id") for c in s["choices"]} == neow_relics))
    assert initial["run"]["seed"] == "BRIDGEFIXTURE"
    assert initial["screen"]["type"] == "NEventRoom"
    assert not initial.get("proceed_context"), "An unfinished event must not expose Proceed"
    assert not initial.get("combat")
    (output / "event-initial.json").write_text(json.dumps(initial, indent=2))
    bridge = path.parent
    rejected = send_command(process, bridge, "proceed", expected_status="error")
    assert rejected["message"] == "Proceed is not available."

    if case == "architect-dialogue":
        state = initial
        for title in ("Threaten", "Continue", "Proceed"):
            assert len(state["choices"]) == 1
            choice = state["choices"][0]
            assert choice["title"] == title, choice
            assert choice["description"] == "", choice
            assert not choice["locked"]
            (output / f"architect-{title.lower()}.json").write_text(json.dumps(state, indent=2))
            if title != "Proceed":
                send_command(process, bridge, "select_choice", choice_id=choice["id"])
                state = wait_state(process, path, "next Architect dialogue choice", lambda s:
                                   bool(s.get("choices")) and s["choices"][0]["id"] != choice["id"])
        # Only exercise dialogue here, not act completion from a debug room.
        # Results/epoch dismissal remains outside the bridge's supported scope.
        return

    choice_index = 1 if case in ("morphic-loner", "wellspring-bathe", "round-tea-party") else 0
    choice = initial["choices"][choice_index]
    if case == "neow-mechanics":
        offers = {c["relic_model_id"]: c for c in initial["choices"]}
        choice = offers["NEOWS_TORMENT"]
        tips = choice["hover_tips"]
        fury = next(t for t in tips if t["kind"] == "card")
        assert fury["card"]["model_id"] == "NEOWS_FURY"
        assert fury["card"]["cost"] == "1" and fury["card"]["upgrade_level"] == 0
        assert fury["card"]["type"] == "Attack" and fury["card"]["target_type"] == "AnyEnemy"
        assert "id" not in fury["card"] and "playable" not in fury["card"]
        assert not any(c["model_id"] == "NEOWS_FURY" for c in initial["deck"])
        text = re.sub(r"\[[^\]]*\]", "", fury["description"]).lower()
        assert re.findall(r"\d+", text) == ["10", "2"] and "discard" in text and "exhaust" in text
        exhaust = next(t for t in tips if t.get("title") == "Exhaust")
        assert "combat" in exhaust["description"].lower()
        glam_tips = {t["title"]: t for t in offers["SILKEN_TRESS"]["hover_tips"]}
        assert "once per combat" in glam_tips["Glam"]["description"].lower()
        assert "additional time" in glam_tips["Replay"]["description"].lower()
        assert offers["SCROLL_BOXES"]["hover_tips"] == []
        assert all(t.get("description") and "{" not in t["description"]
                   for offer in offers.values() for t in offer["hover_tips"])
        assert initial["choice_context"]["description"] == "", "Don't export Neow's missing localization key"
        time.sleep(3.5)  # Include a fallback export poll, not just a cached read.
        polled = read_json(path)
        assert polled["choices"] == initial["choices"], "Preview cards must not acquire fresh action IDs"
        assert polled["deck"] == initial["deck"] and polled["player"] == initial["player"]
    assert not choice["locked"]
    assert "{" not in choice["description"], "Event variables must be resolved"
    if case == "morphic-loner":
        assert choice["description"] == "Gain [green]5[/green] Max HP."
    elif case == "round-tea-party":
        assert choice["title"] == "Pick a Fight"
        assert "11" in choice["description"] and "Relic" in choice["description"]
    send_command(process, bridge, "select_choice", choice_id=choice["id"])

    if case == "round-tea-party":
        state = wait_state(process, path, "Tea Party Continue choice", lambda s:
                           len(s.get("choices", [])) == 1 and s["choices"][0]["title"] == "Continue")
        assert state["choices"][0]["description"] == "", state["choices"][0]
        assert state["player"]["hp"] == initial["player"]["hp"]
        assert state["player"]["relics"] == initial["player"]["relics"]
        (output / "tea-party-continue.json").write_text(json.dumps(state, indent=2))
        send_command(process, bridge, "select_choice", choice_id=state["choices"][0]["id"])
    elif case == "wellspring-bottle":
        state = wait_state(process, path, "potion reward", lambda s: s["scene"] == "rewards")
        assert not state["choices"] and not state.get("proceed_context")
        rewards = state["rewards"]["rewards"]
        assert len(rewards) == 1
        send_command(process, bridge, "take_reward", reward_id=rewards[0]["id"])
    elif case == "wellspring-bathe":
        state = wait_state(process, path, "deck removal selector", lambda s: s["scene"] == "card_selection")
        assert not state["choices"] and not state.get("proceed_context")
        assert state["card_selection"]["min_select"] == 1
        bash = next(c for c in state["card_selection"]["cards"] if c["model_id"] == "BASH")
        assert any(t.get("title") == "Vulnerable" and "50" in t["description"] for t in bash["hover_tips"])
        send_command(process, bridge, "select_card", card_id=state["card_selection"]["cards"][0]["id"])
        wait_state(process, path, "removal confirmation", lambda s:
                   s.get("card_selection", {}).get("confirm_available") is True)
        send_command(process, bridge, "confirm_card_selection")
    elif case == "battleworn-dummy":
        # Let the dummy time out through ordinary end-turn actions, without
        # killing enemies, buffing the player, or replacing the card selector.
        for turn in range(1, 4):
            state = wait_state(process, path, f"combat turn {turn}", lambda s:
                               s["scene"] == "combat" and s["waiting_for_input"]
                               and s.get("combat", {}).get("player_turn") == turn)
            assert state["run"]["event_id"] == event_id
            assert state["run"]["base_room_type"] == "Event"
            assert state["combat"]["player_phase"] == "Play" and not state["choices"]
            assert state["enemies"] and state["hand"]
            send_command(process, bridge, "end_turn")

    event_scene = "neow" if case == "neow-mechanics" else "event"
    final = wait_state(process, path, "finished event with Proceed", lambda s:
                       s["scene"] == event_scene and bool(s.get("proceed_context")))
    assert not final["choices"]
    if case == "neow-mechanics":
        owned = next(r for r in final["player"]["relics"] if r["model_id"] == "NEOWS_TORMENT")
        assert owned["hover_tips"] == choice["hover_tips"]
        obtained = next(c for c in final["deck"] if c["model_id"] == "NEOWS_FURY")
        assert obtained["description"] == fury["description"] and obtained["id"] != fury["id"]
        assert len(final["deck"]) == len(initial["deck"]) + 1
        assert any(t["id"] == exhaust["id"] for t in obtained["hover_tips"])
        time.sleep(3.5)
        assert read_json(path)["player"]["relics"] == final["player"]["relics"], "Owned relic previews must also stay stable"
    elif case == "morphic-loner":
        assert final["player"]["max_hp"] == initial["player"]["max_hp"] + 5
    elif case == "wellspring-bottle":
        assert final["player"]["potion_count"] == initial["player"]["potion_count"] + 1
        assert final["potions"][0]["description"]
        assert isinstance(final["potions"][0]["hover_tips"], list)
    elif case == "wellspring-bathe":
        assert final["player"]["deck_count"] == initial["player"]["deck_count"]
    elif case == "round-tea-party":
        assert final["player"]["hp"] == initial["player"]["hp"] - 11
        assert len(final["player"]["relics"]) == len(initial["player"]["relics"]) + 1
    (output / "event-final.json").write_text(json.dumps(final, indent=2))
    send_command(process, bridge, "proceed")
    wait_state(process, path, "map after event", lambda s: s["scene"] == "map")
