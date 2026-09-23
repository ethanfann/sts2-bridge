"""Native reward skipping and exact-instance potion replacement, offline only."""

import json
import time
from bridge_test import read_json, send_command, wait_for, wait_state


def exercise_potion_rewards(process, sandbox, path, output):
    bridge = path.parent

    def signal(name):
        (sandbox / f"fixture-potion-{name}").touch()

    def audit(name):
        signal(f"{name}-check")
        result = wait_for(process, f"{name} native history", lambda:
                          read_json(sandbox / f"fixture-potion-{name}-audit.json"))
        (output / f"potion-{name}-audit.json").write_text(json.dumps(result, indent=2))
        return result

    def rewards(count):
        return wait_state(process, path, f"{count} reward rows", lambda s:
                          s["scene"] == "rewards" and len(s["rewards"]["rewards"]) == count)

    def shop():
        return wait_state(process, path, "returned to shop", lambda s:
                          s["screen"]["type"] == "NMerchantRoom" and not s.get("rewards"))

    def reject(kind, **fields):
        return send_command(process, bridge, kind, expected_status="error", **fields)

    def collect_card(row):
        send_command(process, bridge, "take_reward", reward_id=row["id"])
        selection = wait_state(process, path, "card reward", lambda s: s["scene"] == "card_selection")
        assert not selection.get("rewards")
        assert not any(p["discard_available"] for p in selection["potions"])
        reject("discard_potion", potion_id=original[0]["id"])
        reject("skip_reward", reward_id=row["id"])
        reject("take_reward", reward_id=row["id"])
        send_command(process, bridge, "select_card", card_id=selection["card_selection"]["cards"][0]["id"])

    initial = rewards(3)
    original = initial["potions"]
    assert [p["model_id"] for p in original] == ["FRUIT_JUICE", "WEAK_POTION", "FRUIT_JUICE"]
    assert [p["slot_index"] for p in original] == [0, 1, 2]
    assert len({p["id"] for p in original}) == 3 and all(p["discard_available"] for p in original)
    assert initial["player"]["potion_count"] == initial["player"]["potion_capacity"] == 3
    offer = next(r for r in initial["rewards"]["rewards"] if r["reward_type"] == "Potion")
    assert not offer["selectable"] and offer["unavailable_reason"] == "potion_belt_full"
    assert not any(r["skippable"] for r in initial["rewards"]["rewards"])
    preview = offer["potion"]
    assert preview["model_id"] == "FORTIFIER" and preview["usage"] == "CombatOnly"
    assert preview["target_type"] == "AnyPlayer" and preview["rarity"] == "Uncommon"
    assert "id" not in preview and "slot_index" not in preview and not preview["discard_available"]
    assert "triple" in preview["description"].lower() and "block" in preview["description"].lower()
    assert any(t.get("title") == "Block" for t in preview["hover_tips"])
    assert "{" not in preview["description"] and offer["description"] == preview["description"]
    assert original[0]["base_values"]["MaxHp"] == 5
    for kind in ("take_reward", "skip_reward"):
        reject(kind, reward_id=offer["id"])
        reject(kind, reward_id="nonexistent")
    reject("discard_potion")
    reject("discard_potion", potion_id="nonexistent")
    reject("use_potion", potion_id=original[0]["id"])
    signal("lock")
    wait_state(process, path, "potions locked", lambda s: not any(p["discard_available"] for p in s["potions"]))
    reject("discard_potion", potion_id=original[0]["id"])
    signal("unlock")
    wait_state(process, path, "potions unlocked", lambda s: all(p["discard_available"] for p in s["potions"]))
    time.sleep(3.5)  # Include an actual fallback poll, not a cached snapshot.
    polled = read_json(path)
    assert polled["potions"] == original and polled["rewards"] == initial["rewards"]
    assert polled["player"] == initial["player"]
    signal("read-check")
    wait_for(process, "nonmutating reads", lambda: (sandbox / "fixture-potion-reads-checked").exists())

    collect_card(next(r for r in initial["rewards"]["rewards"] if r["reward_type"] == "Card"))
    state = rewards(2)
    gold = next(r for r in state["rewards"]["rewards"] if r["reward_type"] == "Gold")
    send_command(process, bridge, "take_reward", reward_id=gold["id"])
    last = rewards(1)
    assert last["player"]["gold"] == initial["player"]["gold"] + 17
    assert last["player"]["deck_count"] == initial["player"]["deck_count"] + 1
    row = last["rewards"]["rewards"][0]
    assert row["skippable"] and not row["selectable"]
    send_command(process, bridge, "skip_reward", reward_id=row["id"])
    skipped = shop()
    assert skipped["potions"] == original and skipped["player"] == last["player"]
    reject("skip_reward", reward_id=row["id"])
    reject("take_reward", reward_id=row["id"])
    skipped_history = audit("skip")
    assert skipped_history["discarded"] == []
    assert skipped_history["choices"] == [
        {"model_id": "FRUIT_JUICE", "picked": True},
        {"model_id": "WEAK_POTION", "picked": True},
        {"model_id": "FRUIT_JUICE", "picked": True},
        {"model_id": "FORTIFIER", "picked": False},
    ]

    signal("replace")
    required = rewards(1)
    row = required["rewards"]["rewards"][0]
    assert not row["skippable"] and not required["rewards"]["proceed_enabled"]
    reject("skip_reward", reward_id=row["id"])
    reject("proceed")
    send_command(process, bridge, "discard_potion", potion_id=original[0]["id"])
    freed = wait_state(process, path, "exact slot freed", lambda s: s["player"]["potion_count"] == 2)
    assert freed["potions"] == original[1:]
    assert freed["player"]["hp"] == 31 and freed["player"]["max_hp"] == initial["player"]["max_hp"]
    reject("discard_potion", potion_id=original[0]["id"])
    row = freed["rewards"]["rewards"][0]
    assert row["selectable"] and "unavailable_reason" not in row
    send_command(process, bridge, "take_reward", reward_id=row["id"])
    replaced = shop()
    fortifier = replaced["potions"][0]
    assert fortifier["slot_index"] == 0 and fortifier["model_id"] == "FORTIFIER"
    assert replaced["potions"][1:] == original[1:]
    assert replaced["player"]["potion_count"] == 3 and replaced["player"]["hp"] == 31
    reject("take_reward", reward_id=row["id"])
    replaced_history = audit("replace")
    assert replaced_history["discarded"] == ["FRUIT_JUICE"]
    assert replaced_history["choices"] == skipped_history["choices"] + [{"model_id": "FORTIFIER", "picked": True}]

    signal("refill")
    rewards(1)
    send_command(process, bridge, "discard_potion", potion_id=fortifier["id"])
    freed = wait_state(process, path, "replacement discarded", lambda s: s["player"]["potion_count"] == 2)
    send_command(process, bridge, "take_reward", reward_id=freed["rewards"]["rewards"][0]["id"])
    refilled = shop()
    new_juice = refilled["potions"][0]
    assert new_juice["model_id"] == "FRUIT_JUICE" and new_juice["slot_index"] == 0
    assert new_juice["id"] not in {p["id"] for p in original}
    assert refilled["potions"][1:] == original[1:]
    reject("discard_potion", potion_id=original[0]["id"])
    reject("discard_potion", potion_id=fortifier["id"])
    refill_history = audit("refill")
    assert refill_history["discarded"] == ["FRUIT_JUICE", "FORTIFIER"]
    assert refill_history["choices"] == replaced_history["choices"] + [{"model_id": "FRUIT_JUICE", "picked": True}]

    signal("capacity")
    expanded = rewards(1)
    assert expanded["player"]["potion_capacity"] == 5 and expanded["player"]["potion_count"] == 3
    row = expanded["rewards"]["rewards"][0]
    assert row["selectable"]
    send_command(process, bridge, "take_reward", reward_id=row["id"])
    assert shop()["player"]["potion_count"] == 4
    signal("combat")
    wait_for(process, "potion combat", lambda: (sandbox / "fixture-potion-combat-ready").exists())
    combat = wait_state(process, path, "combat input", lambda s: s["scene"] == "combat" and s["waiting_for_input"])
    block = next(p for p in combat["potions"] if p["model_id"] == "BLOCK_POTION")
    assert block["base_values"]["Block"] == 12
    reject("use_potion", potion_id=original[0]["id"])
    send_command(process, bridge, "discard_potion", potion_id=new_juice["id"])
    wait_state(process, path, "combat discard", lambda s: s["player"]["potion_count"] == 4 and s["waiting_for_input"])
    send_command(process, bridge, "use_potion", potion_id=block["id"])
    used = wait_state(process, path, "native block potion effect", lambda s:
                      s["player"]["block"] == 12 and s["player"]["potion_count"] == 3 and s["waiting_for_input"])
    assert used["player"]["max_hp"] == initial["player"]["max_hp"] and used["player"]["hp"] == 31
    assert original[2]["id"] in {p["id"] for p in used["potions"]}
    reject("use_potion", potion_id=block["id"])
    (output / "potion-combat.json").write_text(json.dumps(used, indent=2))

    signal("terminal")
    ready = wait_state(process, path, "terminal combat setup", lambda s:
                       s["player"]["potion_count"] == 5 and any(c["model_id"] == "BLUDGEON" for c in s["hand"]))
    finisher = next(c for c in ready["hand"] if c["model_id"] == "BLUDGEON")
    send_command(process, bridge, "play_card", card_id=finisher["id"], target_id=ready["enemies"][0]["id"])
    state = wait_state(process, path, "terminal rewards", lambda s: s["scene"] == "rewards")
    while len(state["rewards"]["rewards"]) > 1:
        rows = state["rewards"]["rewards"]
        row = next(r for r in rows if r["reward_type"] != "Potion")
        if row["reward_type"] == "Card":
            collect_card(row)
        else:
            send_command(process, bridge, "take_reward", reward_id=row["id"])
        state = rewards(len(rows) - 1)
    last = state["rewards"]["rewards"][0]
    assert last["potion"]["model_id"] == "FORTIFIER" and last["skippable"]
    assert last["unavailable_reason"] == "potion_belt_full"
    send_command(process, bridge, "skip_reward", reward_id=last["id"])
    final = wait_state(process, path, "map after skipping terminal potion", lambda s: s["scene"] == "map")
    assert final["potions"] == state["potions"] and final["player"] == state["player"]
    reject("skip_reward", reward_id=last["id"])
    (output / "potion-terminal-map.json").write_text(json.dumps(final, indent=2))
    # Native terminal Skip opens the map; BeforeLeavingRoom finalizes the
    # declined reward when traveling, not while still looking at that map.
    destination = next(p for p in final["map"]["points"] if p["travelable"])
    send_command(process, bridge, "select_map_point", point_id=destination["id"])
    wait_state(process, path, "left terminal reward room", lambda s:
               s["run"]["total_floor"] > final["run"]["total_floor"] and s["scene"] == "combat" and s["waiting_for_input"])
    assert audit("terminal") == {"skipped": 1, "picked": 0}
