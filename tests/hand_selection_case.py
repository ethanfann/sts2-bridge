"""Native hand choices: real card effects plus bounds/filter/visibility contracts."""

import json
from bridge_test import send_command, wait_for, wait_state


def exercise_hand_selection(process, sandbox, path, output):
    bridge = path.parent

    def wait(name, predicate):
        state = wait_state(process, path, name, predicate)
        (output / f"{name}.json").write_text(json.dumps(state, indent=2))
        return state

    def signal(name):
        (sandbox / f"fixture-hand-{name}").touch()

    def done(name):
        def check():
            error = sandbox / "fixture-error.txt"
            if error.exists():
                raise RuntimeError(error.read_text())
            return (sandbox / f"fixture-hand-{name}").exists()
        wait_for(process, name, check)

    def command(action, **fields):
        return send_command(process, bridge, action, **fields)

    def ids(cards):
        return {c["id"] for c in cards}

    def card(state, model, upgrade=None):
        return next(c for c in state["hand"] if c["model_id"] == model
                    and (upgrade is None or c["upgrade_level"] == upgrade))

    def combat(name, predicate):
        return wait(name, lambda s: s["scene"] == "combat" and s["waiting_for_input"] and predicate(s))

    def selection(name, selected=(), kind="combat_hand", minimum=1, maximum=1):
        state = wait(name, lambda s: s.get("card_selection", {}).get("kind") == kind
                     and set(s["card_selection"]["selected_card_ids"]) == set(selected)
                     and s["card_selection"]["min_select"] == minimum
                     and s["card_selection"]["max_select"] == maximum)
        context = state["card_selection"]
        assert state["scene"] == "card_selection" and state["waiting_for_input"]
        assert state["screen"]["type"] == "NCombatRoom" and state["screen"]["supported"]
        assert context["source_pile"] == "hand" and context["require_manual_confirmation"]
        assert not context["cancelable"] and not state.get("proceed_context")
        assert context["prompt"] and "{" not in context["prompt"]
        assert not any(c["playable"] or c["play_targets"] for c in state["hand"])
        assert not any(p["discard_available"] for p in state["potions"])
        assert context["confirm_available"] == (minimum <= len(selected) <= maximum)
        return state

    def pick(card_id):
        command("select_card", card_id=card_id)

    initial = combat("hand-initial", lambda s: len(s["hand"]) == 3)
    thinking = card(initial, "THINKING_AHEAD")
    chosen = card(initial, "STRIKE_IRONCLAD", 1)
    command("confirm_card_selection", expected_status="error")
    command("play_card", card_id=thinking["id"])
    state = selection("thinking-ahead-choice")
    assert len(state["hand"]) == 4 and not state["cards_played"]
    assert ids(state["card_selection"]["cards"]) == ids(state["hand"])
    assert thinking["id"] in ids(state["piles"]["play"])
    for action in ("end_turn", "proceed", "confirm_card_selection"):
        command(action, expected_status="error")
    command("play_card", card_id=chosen["id"], target_id=state["enemies"][0]["id"], expected_status="error")
    for action in ("use_potion", "discard_potion"):
        command(action, potion_id=state["potions"][0]["id"], expected_status="error")
    for invalid in (thinking["id"], state["piles"]["draw"][0]["id"], state["deck"][0]["id"], "missing"):
        command("select_card", card_id=invalid, expected_status="error")
    pick(chosen["id"])
    selected = selection("thinking-ahead-selected", [chosen["id"]])
    other = next(c for c in selected["card_selection"]["cards"] if c["id"] != chosen["id"])
    assert not other["playable"]
    command("select_card", card_id=other["id"], expected_status="error")
    pick(chosen["id"])
    selection("thinking-ahead-deselected")
    pick(chosen["id"])
    selection("thinking-ahead-reselected", [chosen["id"]])
    command("confirm_card_selection")
    after = combat("thinking-ahead-resolved", lambda s: len(s["cards_played"]) == 1)
    assert ids(after["hand"]) == ids(state["hand"]) - {chosen["id"]}
    assert chosen["id"] in ids(after["piles"]["draw"])
    assert thinking["id"] in ids(after["piles"]["exhaust"])
    assert after["player"]["energy"] == 3 and not after["piles"]["draw_order_known"]
    command("select_card", card_id=chosen["id"], expected_status="error")
    command("confirm_card_selection", expected_status="error")
    signal("redraw")
    done("redrawn")
    combat("thinking-ahead-redrawn", lambda s: chosen["id"] in ids(s["hand"]))

    signal("effects")
    done("effects-ready")
    effects = combat("hand-effects", lambda s: len(s["hand"]) == 8 and not s["cards_played"])
    for index, (model, target_model, pile, block) in enumerate((
            ("TRUE_GRIT", "STRIKE_IRONCLAD", "exhaust", 9),
            ("SURVIVOR", "DEFEND_IRONCLAD", "discard", 17))):
        source = card(effects, model)
        target = card(effects, target_model, 0)
        command("play_card", card_id=source["id"])
        pending = selection(f"{model.lower()}-choice")
        assert pending["player"]["block"] == block
        pick(target["id"])
        selection(f"{model.lower()}-selected", [target["id"]])
        command("confirm_card_selection")
        after = combat(f"{model.lower()}-resolved", lambda s: len(s["cards_played"]) == index + 1)
        assert target["id"] in ids(after["piles"][pile]) and target["id"] not in ids(after["hand"])

    armaments = card(effects, "ARMAMENTS")
    command("play_card", card_id=armaments["id"])
    upgrade = selection("armaments-choice", kind="combat_hand_upgrade")
    assert {c["model_id"] for c in upgrade["card_selection"]["cards"]} == {"BASH", "SHRUG_IT_OFF"}
    target = card(upgrade, "BASH")
    excluded = card(upgrade, "STRIKE_IRONCLAD", 1)
    command("select_card", card_id=excluded["id"], expected_status="error")
    preview = next(c["upgrade_preview"] for c in upgrade["card_selection"]["cards"] if c["id"] == target["id"])
    assert preview["base_values"]["Damage"] == 10 and preview["base_values"]["VulnerablePower"] == 3
    pick(target["id"])
    selection("armaments-selected", [target["id"]], kind="combat_hand_upgrade")
    pick(target["id"])
    selection("armaments-deselected", kind="combat_hand_upgrade")
    pick(target["id"])
    selection("armaments-reselected", [target["id"]], kind="combat_hand_upgrade")
    command("confirm_card_selection")
    after = combat("armaments-resolved", lambda s: len(s["cards_played"]) == 3)
    assert card(after, "BASH")["upgrade_level"] == 1 and card(after, "BASH")["id"] == target["id"]
    assert card(after, "SHRUG_IT_OFF")["upgrade_level"] == 0
    assert after["player"]["block"] == 22 and after["player"]["energy"] == 0

    signal("contract")
    state = selection("hand-filtered", maximum=2)
    candidates = state["card_selection"]["cards"]
    assert len(candidates) == 3 and all(c["type"] == "Attack" for c in candidates)
    command("select_card", card_id=card(state, "DEFEND_IRONCLAD")["id"], expected_status="error")
    changing = card(state, "BASH")
    strikes = [c for c in candidates if c["model_id"] == "STRIKE_IRONCLAD"]
    assert len(ids(strikes)) == 2
    pick(changing["id"])
    pick(strikes[0]["id"])
    selection("hand-multi-limit", [changing["id"], strikes[0]["id"]], maximum=2)
    command("select_card", card_id=strikes[1]["id"], expected_status="error")
    pick(strikes[0]["id"])
    selection("hand-multi-partial", [changing["id"]], maximum=2)

    signal("peek")
    wait("hand-peeking", lambda s: not s.get("card_selection") and not s["waiting_for_input"])
    for action in ("select_card", "confirm_card_selection", "end_turn", "play_card"):
        command(action, card_id=changing["id"], expected_status="error")
    signal("unpeek")
    selection("hand-unpeeked", [changing["id"]], maximum=2)
    signal("cover")
    covered = wait("hand-covered", lambda s: s["screen"]["type"] == "NMapScreen")
    assert not covered.get("card_selection")
    for action in ("select_card", "confirm_card_selection", "end_turn"):
        command(action, card_id=changing["id"], expected_status="error")
    signal("uncover")
    selection("hand-uncovered", [changing["id"]], maximum=2)
    signal("invalidate")
    done("invalidated")
    invalidated = selection("hand-revalidated", maximum=2)
    assert changing["id"] not in ids(invalidated["card_selection"]["cards"])
    command("select_card", card_id=changing["id"], expected_status="error")
    command("confirm_card_selection", expected_status="error")
    pick(strikes[1]["id"])
    selection("hand-partial-confirm", [strikes[1]["id"]], maximum=2)
    command("confirm_card_selection")
    done("contract-result")
    assert (sandbox / "fixture-hand-contract-result").read_text() == "1"
    combat("hand-partial-result", lambda s: strikes[1]["id"] in ids(s["piles"]["discard"]))
    signal("optional")
    selection("hand-zero-allowed", minimum=0, maximum=2)
    command("confirm_card_selection")
    done("optional-result")
    combat("hand-zero-result", lambda s: not s.get("card_selection"))
    command("confirm_card_selection", expected_status="error")
