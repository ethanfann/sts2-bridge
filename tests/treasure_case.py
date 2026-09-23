"""Native singleplayer treasure: take, skip, and genuinely empty chests."""

import json
import re
import time
from bridge_test import read_json, send_command, wait_for, wait_state


def exercise_treasure(process, sandbox, path, case, output):
    bridge = path.parent
    initial = wait_state(process, path, "closed treasure chest", lambda s:
                         s.get("treasure", {}).get("chest_open_available"))
    assert initial["scene"] == "treasure" and initial["screen"]["type"] == "NTreasureRoom"
    assert initial["treasure"]["relics"] == []
    assert not initial["treasure"]["proceed_available"]
    (output / "treasure-closed.json").write_text(json.dumps(initial, indent=2))
    send_command(process, bridge, "select_relic", relic_id="not-a-treasure-relic", expected_status="error")
    send_command(process, bridge, "open_treasure_chest")
    # This must fail even while opening/extra rewards are still in progress.
    send_command(process, bridge, "open_treasure_chest", expected_status="error")

    if case == "treasure-empty":
        opened = wait_state(process, path, "empty chest completed", lambda s:
                            s.get("treasure", {}).get("proceed_available")
                            and not s["treasure"]["chest_open_available"])
        assert opened["treasure"]["relics"] == []
        assert opened["player"]["relics"] == initial["player"]["relics"]
        assert opened["player"]["gold"] == initial["player"]["gold"]
    else:
        opened = wait_state(process, path, "single treasure offer and Skip", lambda s:
                            len(s.get("treasure", {}).get("relics", [])) == 1
                            and s["treasure"]["proceed_available"])
        assert not opened["treasure"]["chest_open_available"]
        offer = opened["treasure"]["relics"][0]
        assert offer["title"] == "Blood Vial"
        description = re.sub(r"\[[^\]]*\]", "", offer["description"]).lower()
        assert re.findall(r"\d+", description) == ["2"] and "combat" in description and "heal" in description
        assert "{" not in offer["description"] and isinstance(offer["hover_tips"], list)
        assert opened["player"]["relics"] == initial["player"]["relics"]
        assert 42 <= opened["player"]["gold"] - initial["player"]["gold"] <= 52
        (sandbox / "fixture-treasure-check").touch()

        def native_checked():
            error = sandbox / "fixture-error.txt"
            if error.exists():
                raise RuntimeError(error.read_text())
            return read_json(sandbox / "fixture-treasure-native.json")

        native = wait_for(process, "native hidden-holder checks", native_checked)
        assert native["hidden_uninitialized"] >= 2
        assert offer["description"] == native["description"]
        (output / "treasure-native.json").write_text(json.dumps(native, indent=2))

    # A fallback export must not crash, reveal hidden models, or mutate rewards.
    time.sleep(3.5)
    polled = read_json(path)
    for key in ("treasure", "player", "deck"):
        assert polled[key] == opened[key], key
    send_command(process, bridge, "open_treasure_chest", expected_status="error")
    send_command(process, bridge, "select_relic", relic_id="not-a-treasure-relic", expected_status="error")
    (output / "treasure-open.json").write_text(json.dumps(opened, indent=2))

    if case == "treasure-take":
        send_command(process, bridge, "select_relic", relic_id=offer["id"])
        awarded = wait_state(process, path, "Blood Vial awarded and Proceed", lambda s:
                             s.get("treasure", {}).get("proceed_available")
                             and not s["treasure"]["relics"]
                             and any(r["model_id"] == "BLOOD_VIAL" for r in s["player"]["relics"]))
        assert len(awarded["player"]["relics"]) == len(initial["player"]["relics"]) + 1
        assert sum(r["model_id"] == "BLOOD_VIAL" for r in awarded["player"]["relics"]) == 1
        assert awarded["player"]["gold"] == opened["player"]["gold"]
        send_command(process, bridge, "select_relic", relic_id=offer["id"], expected_status="error")
        send_command(process, bridge, "open_treasure_chest", expected_status="error")
        (output / "treasure-awarded.json").write_text(json.dumps(awarded, indent=2))

    send_command(process, bridge, "proceed")
    final = wait_state(process, path, "map after treasure", lambda s: s["scene"] == "map")
    assert not final.get("treasure")
    assert final["player"]["gold"] == opened["player"]["gold"], "Opening must award gold only once"
    assert final["deck"] == initial["deck"]
    if case == "treasure-take":
        assert final["player"]["relics"] == awarded["player"]["relics"]
    else:
        assert final["player"]["relics"] == initial["player"]["relics"]
    (output / "treasure-final.json").write_text(json.dumps(final, indent=2))
