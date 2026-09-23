"""Native map graph, selection legality, and ordinary/boss room entry."""

import json
import time
from bridge_test import read_json, send_command, wait_for, wait_state


def exercise_map(process, sandbox, path, case, output):
    bridge = path.parent
    expected = read_json(sandbox / "fixture-ready.json")
    destination = expected["destination"]
    initial = wait_state(process, path, "unfinished rest", lambda s:
                         bool(s.get("rest_site", {}).get("options")))
    assert not initial.get("map")
    send_command(process, bridge, "select_map_point", point_id=destination, expected_status="error")
    (sandbox / "fixture-map-view").touch()
    disabled = wait_state(process, path, "view-only map", lambda s: bool(s.get("map")))
    assert not disabled["map"]["is_travel_enabled"]
    assert not any(p["travelable"] for p in disabled["map"]["points"])
    send_command(process, bridge, "select_map_point", point_id=destination, expected_status="error")
    (output / "map-disabled.json").write_text(json.dumps(disabled, indent=2))
    (sandbox / "fixture-map-close").touch()
    rest = wait_state(process, path, "rest after closing map", lambda s:
                      bool(s.get("rest_site", {}).get("options")) and not s.get("map"))
    option = next(o for o in rest["rest_site"]["options"] if o["title"] == "Rest")
    send_command(process, bridge, "select_rest_site_option", option_id=option["id"])
    wait_state(process, path, "completed rest", lambda s: s.get("rest_site", {}).get("proceed_available"))
    send_command(process, bridge, "proceed")
    ready = wait_state(process, path, "travel-enabled map", lambda s:
                       s.get("map", {}).get("is_travel_enabled")
                       and any(p["travelable"] for p in s["map"]["points"]))
    graph = ready["map"]
    points = {p["id"]: p for p in graph["points"]}
    assert len(points) == len(graph["points"]), "No duplicate special points"
    assert set(points) == set(expected["points"])
    assert graph["current_point_id"] == expected["source"]
    assert points[expected["source"]]["current"] and points[expected["source"]]["visited"]
    assert points[expected["boss"]]["room_kind"] == "boss"
    assert points[expected["start"]]["row"] == 0
    assert all(child in points for p in points.values() for child in p["children"])
    assert destination in points[expected["source"]]["children"]
    travelable = {p["id"] for p in points.values() if p["travelable"]}
    assert destination in travelable
    if case == "map-boss":
        assert travelable == {expected["boss"]}
    else:
        assert expected["boss"] not in travelable
        assert points[destination]["room_kind"] == "monster"
    (sandbox / "fixture-map-check").touch()
    native = wait_for(process, "native map buttons", lambda: read_json(sandbox / "fixture-map-native.json"))
    assert set(native["points"]) == set(points)
    assert set(native["travelable"]) == travelable
    (output / "map-native.json").write_text(json.dumps(native, indent=2))

    # These IDs distinguish nonexistent, current, nonadjacent, and noncanonical
    # targets from the valid destination. Rejecting them must not start travel.
    nonadjacent = next(p["id"] for p in points.values() if not p["travelable"] and not p["current"])
    for point_id in ("point_99_99", "invalid", expected["source"], nonadjacent,
                     destination.replace("point_", "point_+", 1)):
        send_command(process, bridge, "select_map_point", point_id=point_id, expected_status="error")
    time.sleep(3.5)  # Include a fresh fallback export, not just the command ack.
    polled = read_json(path)
    for key in ("map", "player", "deck", "run"):
        assert polled[key] == ready[key], key
    assert not (sandbox / "fixture-map-vote.json").exists()
    (output / "map-ready.json").write_text(json.dumps(ready, indent=2))
    send_command(process, bridge, "select_map_point", point_id=destination)
    combat = wait_state(process, path, "selected room combat", lambda s:
                        s["scene"] == "combat" and s["waiting_for_input"] and bool(s.get("enemies")))
    assert combat["run"]["base_room_type"] == ("Boss" if case == "map-boss" else "Monster")
    assert combat["combat"]["player_turn"] == 1
    assert not combat.get("map")
    vote = read_json(sandbox / "fixture-map-vote.json")
    assert vote == {"count": 1, "destination": destination}
    send_command(process, bridge, "select_map_point", point_id=destination, expected_status="error")
    (output / "map-vote.json").write_text(json.dumps(vote, indent=2))
    (output / "map-combat.json").write_text(json.dumps(combat, indent=2))
