"""Native deck selections: observable progress, preview boundaries, and effects."""

import json
import re
import time
from bridge_test import read_json, send_command, wait_for, wait_state


def exercise_deck_selection(process, sandbox, path, output):
    bridge = path.parent

    def command(kind, **fields):
        return send_command(process, bridge, kind, **fields)

    def selection(kind):
        return wait_state(process, path, kind, lambda s: s.get("card_selection", {}).get("kind") == kind)

    def selected(ids, confirm):
        return wait_state(process, path, f"selected {ids}", lambda s:
                          s.get("card_selection", {}).get("selected_card_ids") == sorted(ids)
                          and s["card_selection"].get("confirm_available") is confirm)

    def begin(step, kind):
        (sandbox / f"fixture-deck-{step}").touch()
        s = selection(kind)
        assert s["card_selection"]["selected_card_ids"] == []
        return s

    def done(step):
        wait_for(process, step + " applied", lambda: (sandbox / f"fixture-deck-{step}-done").exists())
        s = wait_state(process, path, "selection closed", lambda s: s["scene"] == "event")
        command("confirm_card_selection", expected_status="error")
        return s

    def preview(ids, before):
        s = selected(ids, True)
        assert s["card_selection"]["require_manual_confirmation"] is True
        assert not any(c["playable"] for c in s["card_selection"]["cards"])
        assert s["deck"] == before["deck"], "Choosing must not apply the effect before confirmation"
        # Even selected cards cannot be clicked through the modal preview.
        for id in [ids[0], next(c["id"] for c in s["card_selection"]["cards"] if c["id"] not in ids)]:
            command("select_card", card_id=id, expected_status="error")
        command("proceed", expected_status="error")
        time.sleep(3.5)
        assert read_json(path)["card_selection"] == s["card_selection"]
        assert read_json(path)["deck"] == before["deck"]
        return s

    initial = wait_state(process, path, "Trial branch", lambda s:
                         s.get("run", {}).get("event_id") == "TRIAL" and len(s.get("choices", [])) == 1)
    description = initial["choice_context"]["description"]
    assert re.search(r"Entrant \[blue\]\d{3}\[/blue\]", description)
    assert "{" not in description
    command("select_choice", choice_id=initial["choices"][0]["id"])
    before = selection("deck_transform")
    ctx = before["card_selection"]
    assert ctx["min_select"] == ctx["max_select"] == 2
    assert ctx["require_manual_confirmation"] is True  # Native prefs is false.
    assert ctx["selected_card_ids"] == [] and ctx["confirm_available"] is False
    ids = [c["id"] for c in ctx["cards"] if c["model_id"] == "DEFEND_IRONCLAD"][:2]
    command("confirm_card_selection", expected_status="error")
    command("select_card", card_id=ids[0])
    partial = selected(ids[:1], False)
    assert partial["state_id"] != before["state_id"]
    assert partial["waiting_for_input"] and partial["deck"] == before["deck"]
    (output / "transform-partial.json").write_text(json.dumps(partial, indent=2))
    command("select_card", card_id=ids[0])
    selected([], False)
    command("select_card", card_id=ids[1])
    selected(ids[1:], False)
    command("confirm_card_selection", expected_status="error")
    command("select_card", card_id=ids[0])
    (output / "transform-preview.json").write_text(json.dumps(preview(ids, before), indent=2))
    command("confirm_card_selection")
    after = wait_state(process, path, "Trial finished", lambda s:
                       s["scene"] == "event" and bool(s.get("proceed_context")))
    assert after["choice_context"]["description"] and "{" not in after["choice_context"]["description"]
    old = {c["id"]: c for c in before["deck"]}
    new = {c["id"]: c for c in after["deck"]}
    assert old.keys() - new.keys() == set(ids)
    assert len(new.keys() - old.keys()) == 2
    assert all(new[id] == old[id] for id in old.keys() & new.keys())
    assert sum(c["model_id"] == "DOUBT" for c in after["deck"]) == 1
    command("select_card", card_id=ids[0], expected_status="error")

    before = begin("remove", "deck_card_select")
    bash = next(c["id"] for c in before["card_selection"]["cards"] if c["model_id"] == "BASH")
    command("select_card", card_id=bash)
    selected([bash], True)
    (sandbox / "fixture-deck-cover").touch()
    wait_state(process, path, "covered selector", lambda s: s["scene"] == "map")
    command("select_card", card_id=bash, expected_status="error")
    command("confirm_card_selection", expected_status="error")
    (sandbox / "fixture-deck-uncover").touch()
    selected([bash], True)
    # Range selection has a Continue step, then a separate preview Confirm.
    command("confirm_card_selection")
    wait_state(process, path, "removal preview", lambda s:
               s.get("card_selection", {}).get("kind") == "deck_card_select"
               and not any(c["playable"] for c in s["card_selection"]["cards"]))
    preview([bash], before)
    command("confirm_card_selection")
    after = done("remove")
    assert after["deck"] == [c for c in before["deck"] if c["id"] != bash]

    before = begin("upgrade", "deck_upgrade")
    ids = [c["id"] for c in before["card_selection"]["cards"] if c["model_id"] == "STRIKE_IRONCLAD"][:2]
    command("select_card", card_id=ids[0])
    selected(ids[:1], False)
    command("select_card", card_id=ids[1])
    preview(ids, before)
    command("confirm_card_selection")
    after = done("upgrade")
    old = {c["id"]: c for c in before["deck"]}
    assert {c["id"] for c in after["deck"] if c != old[c["id"]]} == set(ids)
    assert all(c["upgrade_level"] == 1 and c["base_values"]["Damage"] == 9
               for c in after["deck"] if c["id"] in ids)

    before = begin("enchant", "deck_enchant")
    id = next(c["id"] for c in before["card_selection"]["cards"] if c["model_id"] == "STRIKE_IRONCLAD")
    command("select_card", card_id=id)
    preview([id], before)
    command("confirm_card_selection")
    after = done("enchant")
    old = {c["id"]: c for c in before["deck"]}
    assert [c["id"] for c in after["deck"] if c != old[c["id"]]] == [id]
    assert next(c for c in after["deck"] if c["id"] == id)["enchantment"] == "SWIFT"

    before = begin("simple", "simple_card_select")
    assert before["card_selection"]["confirm_available"] is True
    ids = [c["id"] for c in before["card_selection"]["cards"]][:3]
    for id in ids[:2]:
        command("select_card", card_id=id)
    s = selected(ids[:2], True)
    assert all(c["playable"] == (c["id"] in ids[:2]) for c in s["card_selection"]["cards"])
    command("select_card", card_id=ids[2], expected_status="error")
    for id in ids[:2]:
        command("select_card", card_id=id)
    selected([], True)
    command("confirm_card_selection")
    assert done("simple")["deck"] == before["deck"]

    before = begin("automatic", "simple_card_select")
    assert before["card_selection"]["require_manual_confirmation"] is False
    id = next(c["id"] for c in before["card_selection"]["cards"] if c["model_id"] == "DOUBT")
    command("select_card", card_id=id)
    assert done("automatic")["deck"] == before["deck"]
