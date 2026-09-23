"""Native combat-pile selectors driven only by ordinary bridge commands."""

import json
import time
from bridge_test import read_json, send_command, wait_for, wait_state
from bridge import play_sequence


def exercise_pile_selection(process, sandbox, path, output):
    bridge = path.parent

    def save(name, state):
        (output / f"{name}.json").write_text(json.dumps(state, indent=2))
        return state

    def combat(name, predicate):
        return save(name, wait_state(process, path, name, lambda s:
                    s["scene"] == "combat" and s["waiting_for_input"] and predicate(s)))

    def selection(name, selected=None):
        state = wait_state(process, path, name, lambda s:
                           s["screen"]["type"] == "NCombatPileCardSelectScreen"
                           and (selected is None or
                                set(s.get("card_selection", {}).get("selected_card_ids", [])) == set(selected)))
        save(name, state)
        assert state["scene"] == "card_selection" and state["waiting_for_input"], "Combat-pile selector must be actionable"
        assert state["screen"]["supported"] and not state.get("proceed_context")
        assert state["card_selection"]["kind"] == "combat_pile"
        assert state["card_selection"]["cards"] and "{" not in state["card_selection"]["prompt"]
        return state

    def card(state, model, zone="hand"):
        return next(c for c in state[zone] if c["model_id"] == model)

    def ids(cards):
        return {c["id"] for c in cards}

    initial = combat("pile-initial", lambda s: len(s["hand"]) == 2 and bool(s["enemies"][0]["powers"]))
    target = initial["enemies"][0]["id"]
    hemo = card(initial, "HEMOKINESIS")
    fury = card(initial, "NEOWS_FURY")
    send_command(process, bridge, "confirm_card_selection", expected_status="error")
    send_command(process, bridge, "play_card", card_id=hemo["id"], target_id=target)
    hit = combat("hemo-first-play", lambda s: len(s["cards_played"]) == 1)
    assert hit["enemies"][0]["hp"] == initial["enemies"][0]["hp"] - 22
    assert hit["player"]["hp"] == initial["player"]["hp"] - 2
    stopped = play_sequence(bridge, [
        {"card_id": fury["id"], "target_id": target},
        {"card_id": hemo["id"], "target_id": target},
    ], if_state=hit["state_id"])
    assert stopped["stop_reason"] == "selection_required" and len(stopped["steps"]) == 1
    assert not stopped["steps"][0]["completion_observed"]
    assert stopped["unsubmitted"] == [{"card_id": hemo["id"], "target_id": target}]
    state = selection("fury-discard-selection")
    context = state["card_selection"]
    assert context["source_pile"] == "discard"
    assert (context["min_select"], context["max_select"]) == (0, 2)
    assert context["require_manual_confirmation"] and context["confirm_available"]
    assert not context["cancelable"] and context["selected_card_ids"] == []
    assert ids(context["cards"]) == ids(state["piles"]["discard"])
    assert state["enemies"][0]["hp"] == initial["enemies"][0]["hp"] - 37
    assert state["player"]["energy"] == 1 and len(state["cards_played"]) == 1
    # No underlying combat/room action may run through an active selector.
    for action in ("end_turn", "proceed", "use_potion"):
        send_command(process, bridge, action, expected_status="error")
    send_command(process, bridge, "play_card", card_id=hemo["id"], target_id=target, expected_status="error")
    for wrong_id in (fury["id"], state["deck"][0]["id"], state["piles"]["draw"][0]["id"], "missing"):
        send_command(process, bridge, "select_card", card_id=wrong_id, expected_status="error")

    strikes = [c for c in context["cards"] if c["model_id"] == "STRIKE_IRONCLAD"]
    assert len(strikes) == 2 and strikes[0]["id"] != strikes[1]["id"]
    send_command(process, bridge, "select_card", card_id=hemo["id"])
    selection("fury-one-selected", [hemo["id"]])
    send_command(process, bridge, "select_card", card_id=strikes[0]["id"])
    maximum = selection("fury-at-limit", [hemo["id"], strikes[0]["id"]])
    assert maximum["card_selection"]["confirm_available"], "Even max choices must await manual confirmation"
    assert not next(c for c in maximum["card_selection"]["cards"] if c["id"] == strikes[1]["id"])["playable"]
    send_command(process, bridge, "select_card", card_id=strikes[1]["id"], expected_status="error")
    # Toggle one off, then confirm fewer than max (not cancellation).
    send_command(process, bridge, "select_card", card_id=strikes[0]["id"])
    selection("fury-deselected", [hemo["id"]])
    send_command(process, bridge, "confirm_card_selection")
    recovered = combat("fury-returned-hemo", lambda s: len(s["cards_played"]) == 2)
    assert ids(recovered["hand"]) == {hemo["id"]}
    assert ids(recovered["piles"]["discard"]) == ids(strikes)
    assert ids(recovered["piles"]["exhaust"]) == {fury["id"]}
    send_command(process, bridge, "confirm_card_selection", expected_status="error")
    send_command(process, bridge, "play_card", card_id=hemo["id"], target_id=target)
    won = save("hemo-fury-hemo-win", wait_state(process, path, "combo victory", lambda s: s["scene"] == "rewards"))
    assert not won["enemies"] and won["player"]["hp"] == initial["player"]["hp"]

    (sandbox / "fixture-pile-next").touch()
    wait_for(process, "new pile combat", lambda: (sandbox / "fixture-pile-reset").exists())
    reset = combat("pile-reset", lambda s: len(s["hand"]) == 4 and not s["cards_played"])
    fury = card(reset, "NEOWS_FURY")
    send_command(process, bridge, "play_card", card_id=fury["id"], target_id=reset["enemies"][0]["id"])
    empty_choice = selection("fury-zero-selected")
    send_command(process, bridge, "confirm_card_selection")
    zero = combat("fury-returned-zero", lambda s: len(s["cards_played"]) == 1)
    assert ids(zero["hand"]) == ids(reset["hand"]) - {fury["id"]}
    assert zero["piles"]["discard"] == empty_choice["piles"]["discard"]

    hologram = card(zero, "HOLOGRAM")
    send_command(process, bridge, "play_card", card_id=hologram["id"])
    single = selection("hologram-required-one")
    assert (single["card_selection"]["min_select"], single["card_selection"]["max_select"]) == (1, 1)
    assert not single["card_selection"]["require_manual_confirmation"]
    assert not single["card_selection"]["confirm_available"]
    assert single["player"]["block"] == 3
    send_command(process, bridge, "confirm_card_selection", expected_status="error")
    chosen = single["card_selection"]["cards"][-1]
    send_command(process, bridge, "select_card", card_id=chosen["id"])
    one = combat("hologram-returned-one", lambda s: len(s["cards_played"]) == 2)
    assert ids(one["hand"]) == ids(zero["hand"]) - {hologram["id"]} | {chosen["id"]}
    assert chosen["id"] not in ids(one["piles"]["discard"])

    for index, (model, allowed) in enumerate((("SECRET_TECHNIQUE", "Skill"), ("SECRET_WEAPON", "Attack"))):
        before = read_json(path)
        tutor = card(before, model)
        send_command(process, bridge, "play_card", card_id=tutor["id"])
        draw = selection(f"{model.lower()}-draw-selection")
        context = draw["card_selection"]
        assert context["source_pile"] == "draw" and not draw["piles"]["draw_order_known"]
        assert ids(context["cards"]) == ids([c for c in draw["piles"]["draw"] if c["type"] == allowed])
        assert len(context["cards"]) > 1 and all(c["type"] == allowed for c in context["cards"])
        excluded = next(c for c in draw["piles"]["draw"] if c["type"] != allowed)
        send_command(process, bridge, "select_card", card_id=excluded["id"], expected_status="error")
        if index == 0:
            (sandbox / "fixture-pile-reverse").touch()
            wait_for(process, "reversed draw with selector open", lambda: (sandbox / "fixture-pile-reversed").exists())
            time.sleep(3.5)
            assert read_json(path)["card_selection"] == context, "Do not export hidden order, even among duplicate cards"
        chosen = context["cards"][-1]
        send_command(process, bridge, "select_card", card_id=chosen["id"])
        after = combat(f"{model.lower()}-retrieved", lambda s: len(s["cards_played"]) == 3 + index)
        assert chosen["id"] in ids(after["hand"]) and chosen["id"] not in ids(after["piles"]["draw"])
        assert tutor["id"] in ids(after["piles"]["exhaust"])
        send_command(process, bridge, "select_card", card_id=chosen["id"], expected_status="error")

    (sandbox / "fixture-pile-exhaust").touch()
    exhaust = selection("exhaust-manual-selection")
    assert exhaust["card_selection"]["source_pile"] == "exhaust"
    assert (exhaust["card_selection"]["min_select"], exhaust["card_selection"]["max_select"]) == (1, 2)
    assert not exhaust["card_selection"]["confirm_available"]
    send_command(process, bridge, "confirm_card_selection", expected_status="error")
    chosen = next(c for c in exhaust["card_selection"]["cards"] if c["upgrade_level"] == 1)
    send_command(process, bridge, "select_card", card_id=chosen["id"])
    selection("exhaust-one-selected", [chosen["id"]])
    (sandbox / "fixture-pile-remove-selected").touch()
    wait_for(process, "selected card removed from pile", lambda: (sandbox / "fixture-pile-removed").exists())
    changed = selection("exhaust-selection-invalidated", [])
    assert len(changed["card_selection"]["cards"]) == 2 and not changed["card_selection"]["confirm_available"]
    send_command(process, bridge, "select_card", card_id=chosen["id"], expected_status="error")
    send_command(process, bridge, "confirm_card_selection", expected_status="error")
    picked = []
    for candidate in changed["card_selection"]["cards"]:
        send_command(process, bridge, "select_card", card_id=candidate["id"])
        picked.append(candidate["id"])
        selection(f"exhaust-picked-{len(picked)}", picked)
    send_command(process, bridge, "confirm_card_selection")
    wait_for(process, "exhaust selection completed", lambda: (sandbox / "fixture-pile-exhaust-result").exists())
    assert (sandbox / "fixture-pile-exhaust-result").read_text() == "2"
    returned = combat("exhaust-returned-two", lambda s: set(picked) <= ids(s["hand"]))
    assert not set(picked) & ids(returned["piles"]["exhaust"])
    send_command(process, bridge, "confirm_card_selection", expected_status="error")
