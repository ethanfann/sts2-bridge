"""Native fake shop: prices/effects, input guards, purchase, reopening, and skip."""

import json
import time
from bridge_test import read_json, send_command, wait_for, wait_state


def exercise_fake_merchant(process, sandbox, path, case, output):
    bridge = path.parent
    native = {o["model_id"]: o for o in read_json(sandbox / "fixture-ready.json")["offers"]}
    initial = wait_state(process, path, "fake merchant room", lambda s:
                         s.get("merchant", {}).get("enter_shop_available")
                         and s["player"]["gold"] == native["FAKE_LEES_WAFFLE"]["cost"])
    assert initial["scene"] == "merchant_room" and initial["screen"]["type"] == "NFakeMerchant"
    assert initial["screen"]["supported"] and initial["waiting_for_input"]
    assert initial["run"]["event_id"] == "FAKE_MERCHANT" and initial["run"]["base_room_type"] == "Event"
    assert initial["merchant"]["proceed_available"] and not initial["merchant"]["items"]
    assert not initial["choices"] and not initial.get("proceed_context")
    send_command(process, bridge, "leave_shop", expected_status="error")

    (sandbox / "fixture-room-block").touch()
    blocked = wait_state(process, path, "blocked fake room", lambda s:
                         s.get("merchant", {}).get("kind") == "room" and not s["waiting_for_input"])
    assert not blocked["merchant"]["enter_shop_available"] and not blocked["merchant"]["proceed_available"]
    send_command(process, bridge, "enter_merchant", expected_status="error")
    send_command(process, bridge, "proceed", expected_status="error")
    (sandbox / "fixture-room-unblock").touch()
    wait_state(process, path, "unblocked room", lambda s: s.get("merchant", {}).get("enter_shop_available"))
    send_command(process, bridge, "enter_merchant")
    opened = wait_state(process, path, "six fake offers", lambda s:
                        s.get("merchant", {}).get("leave_available") and len(s["merchant"]["items"]) == 6)
    assert opened["scene"] == "shop" and opened["screen"]["type"] == "NFakeMerchantInventory"
    assert opened["screen"]["supported"] and opened["waiting_for_input"]
    assert not opened["merchant"]["proceed_available"] and not opened["merchant"]["enter_shop_available"]
    assert opened["player"] == initial["player"] and opened["deck"] == initial["deck"]
    offers = opened["merchant"]["items"]
    assert len({o["id"] for o in offers}) == 6 and {o["model_id"] for o in offers} == set(native)
    for offer in offers:
        assert offer["kind"] == "relic" and offer["model_id"].startswith("FAKE_")
        assert offer["cost"] == native[offer["model_id"]]["cost"] and 42 <= offer["cost"] <= 58
        assert offer["description"] == native[offer["model_id"]]["description"]
        assert offer["description"] and "{" not in offer["description"]
        assert offer["purchasable"] == offer["affordable"] == (offer["cost"] <= opened["player"]["gold"])
    waffle = next(o for o in offers if o["model_id"] == "FAKE_LEES_WAFFLE")
    assert waffle["base_values"] == {"Heal": 10} and waffle["cost"] == opened["player"]["gold"]
    assert waffle["purchasable"]
    (output / "fake-merchant-offers.json").write_text(json.dumps(opened, indent=2))
    send_command(process, bridge, "proceed", expected_status="error")
    send_command(process, bridge, "purchase_shop_item", item_id="invalid", expected_status="error")

    (sandbox / "fixture-inventory-block").touch()
    blocked = wait_state(process, path, "blocked inventory", lambda s:
                         s.get("merchant", {}).get("kind") == "inventory" and not s["waiting_for_input"])
    assert not any(o["purchasable"] for o in blocked["merchant"]["items"])
    send_command(process, bridge, "purchase_shop_item", item_id=waffle["id"], expected_status="error")
    send_command(process, bridge, "leave_shop", expected_status="error")
    (sandbox / "fixture-inventory-unblock").touch()
    wait_state(process, path, "unblocked inventory", lambda s: s.get("merchant", {}).get("leave_available"))
    (sandbox / "fixture-map-cover").touch()
    covered = wait_state(process, path, "map covering inventory", lambda s: s["scene"] == "map")
    assert not covered.get("merchant")
    for command in ("purchase_shop_item", "enter_merchant", "leave_shop", "proceed"):
        send_command(process, bridge, command, item_id=waffle["id"], expected_status="error")
    (sandbox / "fixture-map-uncover").touch()
    wait_state(process, path, "uncovered inventory", lambda s: s.get("merchant", {}).get("leave_available"))

    send_command(process, bridge, "leave_shop")
    closed = wait_state(process, path, "closed fake shop", lambda s: s.get("merchant", {}).get("enter_shop_available"))
    assert not closed["merchant"]["items"]
    send_command(process, bridge, "purchase_shop_item", item_id=waffle["id"], expected_status="error")
    send_command(process, bridge, "enter_merchant")
    wait_state(process, path, "reopened fake shop", lambda s: s.get("merchant", {}).get("leave_available"))
    time.sleep(3.5)  # A fresh fallback export must not change offers/RNG/player.
    polled = read_json(path)
    for key in ("merchant", "player", "deck", "potions"):
        assert polled[key] == opened[key], key
    (sandbox / "fixture-check-reads").touch()

    def checked():
        error = sandbox / "fixture-error.txt"
        if error.exists():
            raise RuntimeError(error.read_text())
        return (sandbox / "fixture-reads-checked").exists()

    wait_for(process, "native read stability", checked)
    expected_player = opened["player"]
    if case == "fake-merchant-buy":
        send_command(process, bridge, "purchase_shop_item", item_id=waffle["id"])
        bought = wait_state(process, path, "waffle purchase completed", lambda s:
                            s.get("player", {}).get("hp") == 39 and s["player"]["gold"] == 0
                            and not any(o.get("model_id") == "FAKE_LEES_WAFFLE" for o in s.get("merchant", {}).get("items", [])))
        # 10% of the starting 80 max HP = 8, not the genuine waffle's effect.
        assert opened["player"]["hp"] == 31 and bought["player"]["max_hp"] == 80
        assert len(bought["player"]["relics"]) == len(opened["player"]["relics"]) + 1
        assert sum(r["model_id"] == "FAKE_LEES_WAFFLE" for r in bought["player"]["relics"]) == 1
        assert not any(o["purchasable"] for o in bought["merchant"]["items"])
        send_command(process, bridge, "purchase_shop_item", item_id=waffle["id"], expected_status="error")
        remaining = next(o for o in bought["merchant"]["items"] if o.get("model_id"))
        assert not remaining["affordable"]
        send_command(process, bridge, "purchase_shop_item", item_id=remaining["id"], expected_status="error")
        (output / "fake-merchant-purchased.json").write_text(json.dumps(bought, indent=2))
        send_command(process, bridge, "leave_shop")
        wait_state(process, path, "closed purchased shop", lambda s: s.get("merchant", {}).get("enter_shop_available"))
        send_command(process, bridge, "enter_merchant")
        reopened = wait_state(process, path, "reopened purchased shop", lambda s: s.get("merchant", {}).get("leave_available"))
        assert reopened["merchant"]["items"] == bought["merchant"]["items"]
        (sandbox / "fixture-refill-gold").touch()
        funded = wait_state(process, path, "funded sold-out guard", lambda s: s.get("player", {}).get("gold") == 200)
        assert sum(o["purchasable"] for o in funded["merchant"]["items"]) == 5
        send_command(process, bridge, "purchase_shop_item", item_id=waffle["id"], expected_status="error")
        sold = next(o for o in funded["merchant"]["items"] if not o.get("model_id"))
        send_command(process, bridge, "purchase_shop_item", item_id=sold["id"], expected_status="error")
        time.sleep(3.5)
        expected_player = funded["player"]
        assert read_json(path)["player"] == expected_player
        assert expected_player["relics"] == bought["player"]["relics"] and expected_player["hp"] == 39

    send_command(process, bridge, "leave_shop")
    wait_state(process, path, "fake merchant exit", lambda s: s.get("merchant", {}).get("proceed_available"))
    send_command(process, bridge, "proceed")
    final = wait_state(process, path, "map after fake merchant", lambda s: s["scene"] == "map")
    assert not final.get("merchant") and final["player"] == expected_player and final["deck"] == initial["deck"]
    send_command(process, bridge, "purchase_shop_item", item_id=waffle["id"], expected_status="error")
    (output / "fake-merchant-final.json").write_text(json.dumps(final, indent=2))
