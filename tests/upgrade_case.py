"""Upgrade observations versus the native checkbox and an actual event upgrade."""

import json
import re
import time
from bridge_test import read_json, send_command, wait_for, wait_state


def exercise_upgrade(process, sandbox, path, output):
    bridge = path.parent
    initial = wait_state(process, path, "Aroma upgrade event", lambda s:
                         s.get("run", {}).get("event_id") == "AROMA_OF_CHAOS" and len(s.get("choices", [])) == 2)
    assert all("upgrade_preview" not in card for card in initial["deck"])
    send_command(process, bridge, "select_choice", choice_id=initial["choices"][1]["id"])
    state = wait_state(process, path, "deck upgrade choices", lambda s:
                       s.get("card_selection", {}).get("kind") == "deck_upgrade")
    (output / "upgrade-choices.json").write_text(json.dumps(state, indent=2))
    cards = state["card_selection"]["cards"]
    assert state["waiting_for_input"] and len(cards) > 30
    assert len({c["id"] for c in cards}) == len(cards)
    assert not any(c["upgrade_level"] or c["model_id"] == "SLIMED" for c in cards)
    for card in cards:
        preview = card["upgrade_preview"]
        assert preview["upgrade_level"] == 1 and card["upgrade_level"] == 0
        assert not {"id", "deck_card_id", "playable", "play_targets"} & preview.keys()
        assert preview["description"] and "{" not in preview["description"]
    by_model = {c["model_id"]: c for c in cards}
    hemo = [c for c in cards if c["model_id"] == "HEMOKINESIS"]
    assert sorted((c["base_values"]["Damage"], c["upgrade_preview"]["base_values"]["Damage"]) for c in hemo) == [(15, 20), (18, 23)]
    assert all(c["upgrade_preview"]["base_values"]["HpLoss"] == 2 for c in hemo)
    pommel = by_model["POMMEL_STRIKE"]
    assert pommel["upgrade_preview"]["base_values"] == {"Damage": 10, "Cards": 2}
    slam = by_model["BODY_SLAM"]
    assert slam["energy_cost"] == 1 and slam["upgrade_preview"]["energy_cost"] == 0
    assert slam["upgrade_preview"]["cost"] == "0"
    grit = by_model["TRUE_GRIT"]
    assert "random" in grit["description"].lower()
    assert "random" not in grit["upgrade_preview"]["description"].lower()
    assert grit["upgrade_preview"]["base_values"]["Block"] == 9
    assert any(t["title"] == "Exhaust" for t in grit["upgrade_preview"]["hover_tips"])

    def unchanged():
        time.sleep(3.5)  # Force a fallback export poll, not just a cached read.
        polled = read_json(path)
        assert polled["card_selection"] == state["card_selection"]
        assert polled["deck"] == state["deck"] and polled["player"] == state["player"]

    unchanged()
    (sandbox / "fixture-upgrade-checkbox").touch()
    native = wait_for(process, "native checkbox previews", lambda: read_json(sandbox / "fixture-upgrades-native.json"))
    assert 0 < len(native) < len(cards), "Test must include cards outside the visible grid"
    for shown in native:
        matches = [c for c in cards if c["model_id"] == shown["model_id"]
                   and c["base_values"].get("Damage") == shown["before_damage"]]
        assert matches
        for key in ("name", "description", "energy_cost", "upgrade_level"):
            assert matches[0]["upgrade_preview"][key] == shown[key], (key, shown)
    (output / "upgrade-native-checkbox.json").write_text(json.dumps(native, indent=2))
    unchanged()
    (sandbox / "fixture-upgrade-uncheck").touch()
    wait_for(process, "native checkbox reset", lambda: (sandbox / "fixture-upgrades-unchecked").exists())
    unchanged()

    # Selecting the original ID must still upgrade exactly that card. No
    # preview object or tooltip identifier is a new action handle.
    send_command(process, bridge, "select_card", card_id=pommel["id"])
    pending = wait_state(process, path, "upgrade confirmation", lambda s:
                         s.get("card_selection", {}).get("confirm_available") is True)
    assert pending["card_selection"]["selected_card_ids"] == [pommel["id"]]
    assert pending["deck"] == state["deck"]
    assert not any(c["playable"] for c in pending["card_selection"]["cards"])
    send_command(process, bridge, "confirm_card_selection")
    final = wait_state(process, path, "finished upgrade event", lambda s:
                       s["scene"] == "event" and bool(s.get("proceed_context")))
    (output / "upgrade-applied.json").write_text(json.dumps(final, indent=2))
    before = {c["id"]: c for c in state["deck"]}
    after = {c["id"]: c for c in final["deck"]}
    assert before.keys() == after.keys()
    assert [id for id in before if before[id] != after[id]] == [pommel["id"]]
    upgraded = after[pommel["id"]]
    for key in ("name", "energy_cost", "upgrade_level", "base_values", "keywords", "hover_tips"):
        assert upgraded[key] == pommel["upgrade_preview"][key]
    plain = lambda text: re.sub(r"\[[^\]]*\]", "", text)
    assert plain(upgraded["description"]) == plain(pommel["upgrade_preview"]["description"])
    assert "upgrade_preview" not in upgraded
    send_command(process, bridge, "proceed")
    wait_state(process, path, "map after upgrade", lambda s: s["scene"] == "map")
