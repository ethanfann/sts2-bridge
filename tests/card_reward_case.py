"""Real native card alternatives and pre-pickup relic context, offline only."""

import json
import time
from bridge_test import read_json, send_command, wait_for, wait_state


def exercise_card_rewards(process, sandbox, path, output):
    bridge = path.parent

    def signal(name):
        (sandbox / f"fixture-card-{name}").touch()

    def audit(name):
        result = wait_for(process, f"{name} native audit", lambda:
                          read_json(sandbox / f"fixture-card-{name}.json"))
        (output / f"card-{name}-audit.json").write_text(json.dumps(result, indent=2))
        return result

    def rows(count):
        return wait_state(process, path, f"{count} reward rows", lambda s:
                          s["scene"] == "rewards" and len(s["rewards"]["rewards"]) == count)

    def shop():
        return wait_state(process, path, "returned to shop", lambda s: s["scene"] == "merchant_room")

    def selection():
        state = wait_state(process, path, "card offer", lambda s:
                           s["screen"]["type"] == "NCardRewardSelectionScreen"
                           and all(a["selectable"] for a in s["card_selection"]["alternatives"]))
        assert state["card_selection"]["prompt"] == "Choose a Card", state["card_selection"]["prompt"]
        return state

    def open_card(state):
        row = next(r for r in state["rewards"]["rewards"] if r["reward_type"] == "Card")
        send_command(process, bridge, "take_reward", reward_id=row["id"])
        return selection()

    def alternative(state, kind):
        return next(a for a in state["card_selection"]["alternatives"] if a["kind"] == kind)

    def choose(option, status="ok"):
        send_command(process, bridge, "select_card_reward_alternative", expected_status=status, option_id=option["id"])

    def pick(state):
        send_command(process, bridge, "select_card", card_id=state["card_selection"]["cards"][0]["id"])

    initial = rows(5)
    relics = [r for r in initial["rewards"]["rewards"] if r["reward_type"] == "Relic"]
    letter = next(r for r in relics if r["model_id"] == "LETTER_OPENER")
    scales = next(r for r in relics if r["model_id"] == "BRONZE_SCALES")
    assert letter["base_values"] == {"Cards": 3, "Damage": 5}
    assert "3" in letter["description"] and "5" in letter["description"] and "Skills" in letter["description"]
    assert letter["hover_tips"] == [] and letter["rarity"] == "Uncommon"
    assert scales["base_values"] == {"ThornsPower": 7} and "7" in scales["description"]
    assert any(t["title"] == "Thorns" for t in scales["hover_tips"])
    assert all(r["description"] != r["title"] and "{" not in r["description"] for r in relics)
    time.sleep(3.5)
    polled = read_json(path)
    assert polled["rewards"] == initial["rewards"] and polled["player"] == initial["player"]
    signal("reads")
    native = audit("reads")
    assert any(r["model_id"] == native["model_id"] and r["description"] == native["description"] for r in relics)

    opened = open_card(initial)
    assert [c["model_id"] for c in opened["card_selection"]["cards"]] == ["STRIKE_IRONCLAD", "HEADBUTT", "BASH"]
    skip = alternative(opened, "Skip")
    assert skip["title"] == "Skip" and skip["after_selected"] == "EndSelectionAndDoNotCompleteReward"
    assert len(opened["card_selection"]["alternatives"]) == 1
    send_command(process, bridge, "select_card_reward_alternative", expected_status="error")
    choose({"id": "Skip"}, "error")  # Native kind is not an action ID.
    send_command(process, bridge, "proceed", expected_status="error")
    signal("disable")
    wait_state(process, path, "disabled alternative", lambda s:
               not s["card_selection"]["alternatives"][0]["selectable"])
    choose(skip, "error")
    signal("hide")
    wait_state(process, path, "hidden alternative", lambda s: s["card_selection"]["alternatives"] == [])
    choose(skip, "error")
    signal("cover")
    covered = wait_state(process, path, "covered offer", lambda s: s["screen"]["type"] == "NSimpleCardSelectScreen")
    assert "alternatives" not in covered["card_selection"]
    choose(skip, "error")
    signal("uncover")
    restored = selection()
    assert restored["card_selection"] == opened["card_selection"]
    choose(skip)
    skipped = rows(5)
    assert skipped["rewards"] == initial["rewards"] and skipped["player"] == initial["player"]
    choose(skip, "error")
    signal("skip-check")
    assert audit("skip") == {"cards": 0, "gold": initial["player"]["gold"],
                             "deck": initial["player"]["deck_count"], "relics": len(initial["player"]["relics"])}
    reopened = open_card(skipped)
    assert reopened["card_selection"]["cards"] == opened["card_selection"]["cards"]
    assert alternative(reopened, "Skip")["id"] != skip["id"]
    choose(skip, "error")
    pick(reopened)
    state = rows(4)
    assert state["player"]["deck_count"] == initial["player"]["deck_count"] + 1
    for model in ("LETTER_OPENER", "BRONZE_SCALES", None):
        row = next(r for r in state["rewards"]["rewards"] if
                   (r.get("model_id") == model if model else r["reward_type"] == "Gold"))
        count = len(state["rewards"]["rewards"])
        send_command(process, bridge, "take_reward", reward_id=row["id"])
        state = rows(count - 1)
    for offer in (letter, scales):
        owned = next(r for r in state["player"]["relics"] if r["model_id"] == offer["model_id"])
        assert owned["description"] == offer["description"] and owned["base_values"] == offer["base_values"]
    assert state["player"]["gold"] == initial["player"]["gold"] + 17
    send_command(process, bridge, "proceed")  # Leave only the random relic.
    shop()
    assert audit("first") == {"picked": 1, "declined": 2}

    signal("required")
    required = open_card(rows(1))
    assert required["card_selection"]["alternatives"] == []
    choose(skip, "error")
    send_command(process, bridge, "proceed", expected_status="error")
    pick(required)
    shop()

    signal("reroll")
    before = open_card(rows(1))
    reroll = alternative(before, "REROLL")
    old_skip = alternative(before, "Skip")
    assert reroll["after_selected"] == "DoNothing"
    choose(reroll)
    after = wait_state(process, path, "rerolled cards and buttons", lambda s:
                       s.get("card_selection", {}).get("kind") == "card_reward"
                       and len(s["card_selection"]["alternatives"]) == 1
                       and s["card_selection"]["cards"] != before["card_selection"]["cards"])
    assert not ({c["id"] for c in before["card_selection"]["cards"]}
                & {c["id"] for c in after["card_selection"]["cards"]})
    assert after["player"] == before["player"] and alternative(after, "Skip")["id"] != old_skip["id"]
    choose(reroll, "error")
    choose(old_skip, "error")
    send_command(process, bridge, "select_card", expected_status="error", card_id=before["card_selection"]["cards"][0]["id"])
    choose(alternative(after, "Skip"))
    pick(open_card(rows(1)))
    shop()

    signal("sacrifice")
    state = rows(3)
    before_deck, before_gold = state["player"]["deck_count"], state["player"]["gold"]
    offered = open_card(state)
    assert {a["kind"] for a in offered["card_selection"]["alternatives"]} == {"Skip", "SACRIFICE"}
    sacrifice = alternative(offered, "SACRIFICE")
    assert sacrifice["after_selected"] == "EndSelectionAndCompleteReward"
    choose(sacrifice)
    state = rows(2)
    assert state["player"]["deck_count"] == before_deck and state["player"]["gold"] == before_gold
    assert next(r for r in state["player"]["relics"] if r["model_id"] == "PAELS_WING")["counter"] == 1
    second = open_card(state)
    choose(sacrifice, "error")  # Same alternative kind on a different reward.
    pick(second)
    state = rows(1)
    send_command(process, bridge, "take_reward", reward_id=state["rewards"]["rewards"][0]["id"])
    finished = shop()
    assert finished["player"]["gold"] == before_gold + 23 and finished["player"]["deck_count"] == before_deck + 1
    assert audit("sacrifice") == {"sacrifices": 1}

    signal("combat")
    state = wait_state(process, path, "terminal combat", lambda s:
                       s["scene"] == "combat" and s["waiting_for_input"]
                       and any(c["model_id"] == "BLUDGEON" for c in s["hand"]))
    finisher = next(c for c in state["hand"] if c["model_id"] == "BLUDGEON")
    send_command(process, bridge, "play_card", card_id=finisher["id"], target_id=state["enemies"][0]["id"])
    state = wait_state(process, path, "terminal rewards", lambda s: s["scene"] == "rewards")
    count = len(state["rewards"]["rewards"])
    offered = open_card(state)
    choose(alternative(offered, "Skip"))
    state = rows(count)
    while len(state["rewards"]["rewards"]) > 1:
        row = next(r for r in state["rewards"]["rewards"] if r["reward_type"] != "Card")
        send_command(process, bridge, "take_reward", reward_id=row["id"])
        state = rows(len(state["rewards"]["rewards"]) - 1)
    send_command(process, bridge, "skip_reward", reward_id=state["rewards"]["rewards"][0]["id"])
    final = wait_state(process, path, "map after card skip", lambda s: s["scene"] == "map")
    assert final["deck"] == offered["deck"]
    (output / "card-terminal-map.json").write_text(json.dumps(final, indent=2))
    signal("terminal-map-check")
    assert audit("terminal-map") == {"new_choices": 0}
    destination = next(p for p in final["map"]["points"] if p["travelable"])
    send_command(process, bridge, "select_map_point", point_id=destination["id"])
    wait_state(process, path, "room departure", lambda s: s["run"]["total_floor"] > final["run"]["total_floor"])
    signal("terminal-check")
    assert audit("terminal") == {"picked": 0, "declined": 3, "sacrifices": 1,
                                 "models": sorted(c["model_id"] for c in offered["card_selection"]["cards"])}
