"""Native Sphere payments, reveals, privacy, guards, rewards, and final exit."""

import json
import time
from bridge_test import read_json, send_command, wait_for, wait_state


def exercise_crystal_sphere(process, sandbox, path, case, output):
    bridge = path.parent
    initial = wait_state(process, path, "Sphere event choices", lambda s:
                         s.get("run", {}).get("event_id") == "CRYSTAL_SPHERE" and len(s.get("choices", [])) == 2)
    gold_branch = case == "crystal-sphere-gold"
    count = 3 if gold_branch else 6
    payment = initial["choices"][0 if gold_branch else 1]
    send_command(process, bridge, "select_crystal_sphere_tool", tool_id="invalid", expected_status="error")
    send_command(process, bridge, "select_choice", choice_id=payment["id"])

    def settled(remaining):
        return wait_state(process, path, f"Sphere ready with {remaining} divinations", lambda s:
                          s.get("crystal_sphere", {}).get("divinations_remaining") == remaining
                          and not s["crystal_sphere"]["busy"] and s["waiting_for_input"])

    opened = settled(count)
    native = wait_for(process, "Sphere ground truth", lambda: read_json(sandbox / "fixture-sphere.json"))
    (output / "sphere-native.json").write_text(json.dumps(native, indent=2))
    assert opened["scene"] == "crystal_sphere" and opened["screen"]["type"] == "NCrystalSphereScreen"
    assert opened["screen"]["supported"] and not opened["choices"] and not opened.get("proceed_context")
    assert opened["player"]["gold"] == 200 - (read_json(sandbox / "fixture-ready.json")["cost"] if gold_branch else 0)
    assert sum(c["model_id"] == "DEBT" for c in opened["deck"]) == (0 if gold_branch else 1)
    sphere = opened["crystal_sphere"]
    assert (sphere["width"], sphere["height"]) == (11, 11)
    assert sphere["selected_tool"] == "Big" and not sphere["revealed_items"]
    assert len(sphere["cells"]) == len({c["id"] for c in sphere["cells"]}) == 121
    assert {(c["x"], c["y"]) for c in sphere["cells"]} == {(x, y) for x in range(11) for y in range(11)}
    assert all(c["selectable"] == c["hidden"] for c in sphere["cells"])
    tools = {t["kind"]: t for t in sphere["tools"]}
    assert set(tools) == {"Small", "Big"} and all(t["label"] and t["selectable"] for t in tools.values())
    center = next(c for c in sphere["cells"] if (c["x"], c["y"]) == (5, 5))
    cleared = next(c for c in sphere["cells"] if not c["hidden"])
    for command, fields in (
        ("proceed", {}), ("select_choice", {"choice_id": payment["id"]}),
        ("select_crystal_sphere_tool", {}), ("select_crystal_sphere_tool", {"tool_id": "invalid"}),
        ("reveal_crystal_sphere_cell", {}), ("reveal_crystal_sphere_cell", {"cell_id": "invalid"}),
        ("reveal_crystal_sphere_cell", {"cell_id": cleared["id"]}),
    ):
        send_command(process, bridge, command, expected_status="error", **fields)

    def signal(step):
        (sandbox / f"fixture-sphere-{step}").touch()

    signal("disable")
    wait_state(process, path, "disabled Sphere controls", lambda s:
               s.get("crystal_sphere") and not next(t for t in s["crystal_sphere"]["tools"] if t["kind"] == "Small")["selectable"])
    send_command(process, bridge, "select_crystal_sphere_tool", tool_id=tools["Small"]["id"], expected_status="error")
    send_command(process, bridge, "reveal_crystal_sphere_cell", cell_id=center["id"], expected_status="error")
    signal("enable")
    wait_state(process, path, "enabled Sphere controls", lambda s:
               s.get("crystal_sphere") and all(t["selectable"] for t in s["crystal_sphere"]["tools"]))
    signal("busy")
    busy = wait_state(process, path, "pending native click", lambda s: s.get("crystal_sphere", {}).get("busy"))
    assert not busy["waiting_for_input"] and not any(c["selectable"] for c in busy["crystal_sphere"]["cells"])
    for command, fields in (("select_crystal_sphere_tool", {"tool_id": tools["Small"]["id"]}),
                            ("reveal_crystal_sphere_cell", {"cell_id": center["id"]}), ("proceed", {})):
        send_command(process, bridge, command, expected_status="error", **fields)
    signal("idle")
    settled(count)
    signal("cover")
    covered = wait_state(process, path, "map over Sphere", lambda s: s["scene"] == "map")
    assert not covered.get("crystal_sphere")
    send_command(process, bridge, "select_crystal_sphere_tool", tool_id=tools["Small"]["id"], expected_status="error")
    send_command(process, bridge, "reveal_crystal_sphere_cell", cell_id=center["id"], expected_status="error")
    send_command(process, bridge, "proceed", expected_status="error")
    signal("uncover")
    settled(count)
    time.sleep(3.5)  # Include fresh fallback exports, not just cached file reads.
    assert read_json(path)["crystal_sphere"] == sphere
    assert read_json(path)["player"] == opened["player"] and read_json(path)["deck"] == opened["deck"]
    signal("check")

    def checked():
        error = sandbox / "fixture-error.txt"
        if error.exists():
            raise RuntimeError(error.read_text())
        return (sandbox / "fixture-sphere-checked").exists()

    wait_for(process, "native read/privacy checks", checked)
    (output / "sphere-initial.json").write_text(json.dumps(opened, indent=2))
    curse = next(i for i in native["items"] if i["kind"] == "Curse")
    send_command(process, bridge, "select_crystal_sphere_tool", tool_id=tools["Small"]["id"])
    state = wait_state(process, path, "Small selected", lambda s:
                       s.get("crystal_sphere", {}).get("selected_tool") == "Small")
    stale = send_command(process, bridge, "reveal_crystal_sphere_cell", cell_id=center["id"],
                         expected_state_id=opened["state_id"], expected_status="error")
    assert "stale" in stale["message"]

    def reveal(state, x, y, tool):
        before = state["crystal_sphere"]
        target = next(c for c in before["cells"] if (c["x"], c["y"]) == (x, y))
        assert target["selectable"]
        send_command(process, bridge, "reveal_crystal_sphere_cell", cell_id=target["id"],
                     expected_state_id=state["state_id"])
        # Independent expected geometry: Small clears one, Big a clipped square.
        radius = 0 if tool == "Small" else 1
        expected_hidden = {(c["x"], c["y"]) for c in before["cells"] if c["hidden"]
                           and not (abs(c["x"] - x) <= radius and abs(c["y"] - y) <= radius)}
        remaining = before["divinations_remaining"] - 1
        if remaining == 0:
            return None, expected_hidden
        after = settled(remaining)
        actual = after["crystal_sphere"]
        assert {c["id"] for c in actual["cells"]} == {c["id"] for c in before["cells"]}
        assert {(c["x"], c["y"]) for c in actual["cells"] if c["hidden"]} == expected_hidden
        expected_items = [i for i in native["items"] if not any(
            (xx, yy) in expected_hidden for xx in range(i["x"], i["x"] + i["width"])
            for yy in range(i["y"], i["y"] + i["height"]))]
        assert [{key: i[key] for key in ("kind", "x", "y", "width", "height")}
                for i in actual["revealed_items"]] == expected_items
        send_command(process, bridge, "reveal_crystal_sphere_cell", cell_id=target["id"], expected_status="error")
        return after, expected_hidden

    state, _ = reveal(state, curse["x"], curse["y"], "Small")
    assert not state["crystal_sphere"]["revealed_items"], "One quarter of a curse must not reveal its identity or bounds"
    assert not any(c["model_id"] == "DOUBT" for c in state["deck"])
    (output / "sphere-partial.json").write_text(json.dumps(state, indent=2))
    send_command(process, bridge, "select_crystal_sphere_tool", tool_id=tools["Big"]["id"])
    state = wait_state(process, path, "Big selected", lambda s:
                       s.get("crystal_sphere", {}).get("selected_tool") == "Big")
    state, hidden = reveal(state, curse["x"] + 1, curse["y"] + 1, "Big")
    assert sum(c["model_id"] == "DOUBT" for c in state["deck"]) == 1
    (output / "sphere-revealed.json").write_text(json.dumps(state, indent=2))
    if not gold_branch:
        # Test boundary clipping with an asymmetric edge coordinate.
        x, y = next((c["x"], c["y"]) for c in state["crystal_sphere"]["cells"] if c["x"] == 0 and c["selectable"])
        state, hidden = reveal(state, x, y, "Big")
    while state is not None:
        # Ensure a native reward exists; fixture-only layout is not gameplay advice.
        target = next((i for i in native["items"] if i["kind"] == "Gold" and (i["x"], i["y"]) in hidden), None)
        x, y = (target["x"], target["y"]) if target else next(iter(sorted(hidden)))
        state, hidden = reveal(state, x, y, "Big")

    rewards = wait_state(process, path, "Sphere native rewards", lambda s: s["scene"] == "rewards")
    assert not rewards.get("crystal_sphere") and not rewards["choices"]
    assert rewards["rewards"]["rewards"]
    send_command(process, bridge, "reveal_crystal_sphere_cell", cell_id=center["id"], expected_status="error")
    (output / "sphere-rewards.json").write_text(json.dumps(rewards, indent=2))
    gold_before = rewards["player"]["gold"]
    expected_gold = sum(30 if i["width"] == 2 else 10 for i in native["items"] if i["kind"] == "Gold"
                        and all((xx, i["y"]) not in hidden for xx in range(i["x"], i["x"] + i["width"])))
    assert expected_gold > 0
    current = rewards
    while current["scene"] == "rewards":
        rows = current["rewards"]["rewards"]
        reward = next((r for r in rows if r["reward_type"] == "Gold"), None)
        if reward is None:
            break
        send_command(process, bridge, "take_reward", reward_id=reward["id"])
        current = wait_state(process, path, "gold row consumed", lambda s:
                             s["scene"] != "rewards" or len(s["rewards"]["rewards"]) < len(rows))
    assert current["player"]["gold"] == gold_before + expected_gold
    # Other native reward/selector mechanics have their own fixtures; leave any
    # remaining offers using the reward screen's native Proceed button.
    if current["scene"] == "rewards":
        wait_state(process, path, "rewards Proceed", lambda s: s.get("rewards", {}).get("proceed_enabled"))
        send_command(process, bridge, "proceed")
    final = settled(0)
    assert final["proceed_context"]["kind"] == "crystal_sphere" and final["crystal_sphere"]["proceed_available"]
    assert not any(c["selectable"] for c in final["crystal_sphere"]["cells"])
    assert not any(t["selectable"] for t in final["crystal_sphere"]["tools"])
    assert {(c["x"], c["y"]) for c in final["crystal_sphere"]["cells"] if c["hidden"]} == hidden
    send_command(process, bridge, "select_crystal_sphere_tool", tool_id=tools["Small"]["id"], expected_status="error")
    send_command(process, bridge, "reveal_crystal_sphere_cell", cell_id=center["id"], expected_status="error")
    (output / "sphere-finished.json").write_text(json.dumps(final, indent=2))
    send_command(process, bridge, "proceed")
    mapped = wait_state(process, path, "map after Sphere", lambda s: s["scene"] == "map")
    assert not mapped.get("crystal_sphere") and mapped["player"] == final["player"]
    send_command(process, bridge, "reveal_crystal_sphere_cell", cell_id=center["id"], expected_status="error")
