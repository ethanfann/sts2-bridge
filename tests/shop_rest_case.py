"""Real merchant/rest observations, read stability, purchases, and healing."""

import json
import re
import time
from bridge_test import read_json, send_command, wait_for, wait_state


def plain(text):
    return re.sub(r"\[[^\]]*\]", "", text).lower()


def unchanged(path, state, context):
    time.sleep(3.5)  # Include the fallback export, not just a cached file read.
    polled = read_json(path)
    for key in (context, "deck", "player", "potions"):
        assert polled[key] == state[key], key


def exercise_shop_rest(process, sandbox, path, output):
    bridge = path.parent
    wait_state(process, path, "merchant room", lambda s: s.get("merchant", {}).get("enter_shop_available"))
    send_command(process, bridge, "enter_merchant")
    state = wait_state(process, path, "merchant offers", lambda s: len(s.get("merchant", {}).get("items", [])) == 14)
    (output / "shop-context.json").write_text(json.dumps(state, indent=2))
    items = state["merchant"]["items"]
    by_model = {item.get("model_id"): item for item in items}
    assert len({item["id"] for item in items}) == 14
    for item in items:
        assert item["description"] and "{" not in item["description"], item
        assert item["cost"] > 0
        assert item["affordable"] == (item["cost"] <= state["player"]["gold"])
        assert item["purchasable"] == item["affordable"]
        for tip in item["hover_tips"]:
            assert tip["description"] and "{" not in tip["description"]
        if item["kind"] == "card":
            card = item["card"]
            assert card["model_id"] == item["model_id"] and card["name"] == item["title"]
            for key in ("description", "base_values", "hover_tips"):
                assert card[key] == item[key]
            assert not card["playable"] and not card["play_targets"]
            assert card["id"] != item["id"]
    headbutt = by_model["HEADBUTT"]
    assert headbutt["card"]["upgrade_level"] == 1 and headbutt["base_values"]["Damage"] == 17
    assert "17" in plain(headbutt["description"])
    blood = by_model["BLOODLETTING"]
    assert blood["card"]["energy_cost"] == 0 and blood["card"]["cost"] == "0"
    assert blood["base_values"] == {"HpLoss": 3, "Energy": 2}
    for model, keyword in (("DOMINATE", "Exhaust"), ("MIND_BLAST", "Innate")):
        assert keyword in by_model[model]["card"]["keywords"]
        assert keyword.lower() in plain(by_model[model]["description"])
        assert any(t["title"] == keyword for t in by_model[model]["hover_tips"])
    assert by_model["KUNAI"]["base_values"] == {"Cards": 3, "DexterityPower": 1}
    assert "dexterity" in plain(by_model["KUNAI"]["description"])
    assert any(t["title"] == "Dexterity" for t in by_model["KUNAI"]["hover_tips"])
    assert by_model["PENDULUM"]["base_values"] == {"Cards": 1, "Turns": 3}
    assert by_model["MYSTIC_LIGHTER"]["base_values"]["Damage"] == 9
    assert by_model["FLEX_POTION"]["base_values"] == {"StrengthPower": 5}
    assert "5" in plain(by_model["FLEX_POTION"]["description"])
    assert by_model["LUCKY_TONIC"]["base_values"] == {"BufferPower": 1}
    assert "skill" in plain(by_model["SKILL_POTION"]["description"])
    assert any(item.get("on_sale") for item in items)
    assert any(not item["affordable"] for item in items)
    unchanged(path, state, "merchant")
    (sandbox / "fixture-check-shop").touch()
    wait_for(process, "unchanged native shop RNG/card registry", lambda: (sandbox / "fixture-shop-checked").exists())

    # Purchase the modified instance using the unchanged outer shop action ID.
    assert headbutt["purchasable"]
    send_command(process, bridge, "purchase_shop_item", item_id=headbutt["id"])
    bought = wait_state(process, path, "purchased Headbutt", lambda s:
                        s.get("player", {}).get("deck_count") == state["player"]["deck_count"] + 1)
    assert bought["player"]["gold"] == state["player"]["gold"] - headbutt["cost"]
    obtained = next(c for c in bought["deck"] if c["model_id"] == "HEADBUTT")
    assert obtained["id"] == headbutt["card"]["id"] and obtained["base_values"]["Damage"] == 17
    assert not any(i.get("model_id") == "HEADBUTT" and i["purchasable"] for i in bought["merchant"]["items"])
    (output / "shop-purchased.json").write_text(json.dumps(bought, indent=2))
    send_command(process, bridge, "leave_shop")
    wait_state(process, path, "closed merchant", lambda s: s.get("merchant", {}).get("kind") == "room")
    send_command(process, bridge, "proceed")
    wait_state(process, path, "map after ordinary merchant", lambda s: s["scene"] == "map")
    (sandbox / "fixture-rest").touch()

    rest = wait_state(process, path, "rest site options", lambda s: bool(s.get("rest_site", {}).get("options")))
    options = {o["title"]: o for o in rest["rest_site"]["options"]}
    assert rest["player"]["hp"] == 31 and rest["player"]["max_hp"] == 80
    assert "24" in plain(options["Rest"]["description"])
    assert "a card" in plain(options["Smith"]["description"])
    assert all("{" not in o["description"] for o in options.values())
    (output / "rest-base.json").write_text(json.dumps(rest, indent=2))
    unchanged(path, rest, "rest_site")
    (sandbox / "fixture-rest-modified").touch()
    modified = wait_state(process, path, "modified rest site", lambda s:
                          any("2 cards" in plain(o["description"]) for o in s.get("rest_site", {}).get("options", [])))
    options = {o["title"]: o for o in modified["rest_site"]["options"]}
    text = plain(options["Rest"]["description"])
    # The native UI lists base healing and the relic bonus separately.
    # The actual heal below independently verifies their combined effect.
    assert re.findall(r"\d+", text) == ["30", "24", "15"] and "\n" in text, text
    assert "+15 hp from regal pillow" in text
    assert all("{" not in o["description"] for o in options.values())
    assert modified["player"]["hp"] == 31
    (output / "rest-modified.json").write_text(json.dumps(modified, indent=2))
    unchanged(path, modified, "rest_site")
    send_command(process, bridge, "select_rest_site_option", option_id=options["Rest"]["id"])
    healed = wait_state(process, path, "completed rest", lambda s:
                        s.get("rest_site", {}).get("proceed_available") and s["player"]["hp"] == 70)
    assert healed["deck"] == modified["deck"] and healed["player"]["gold"] == modified["player"]["gold"]
    (output / "rest-healed.json").write_text(json.dumps(healed, indent=2))
