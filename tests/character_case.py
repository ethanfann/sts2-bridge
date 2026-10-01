"""Character mechanics checked against the installed DLL, not mocked models."""

import json
from bridge_test import read_json, send_command, wait_for, wait_state
from bridge import observation


def exercise_character(process, sandbox, path, case, output):
    bridge = path.parent

    def settled(name, predicate=lambda s: True):
        state = wait_state(process, path, name, lambda s:
                           s["scene"] == "combat" and s["waiting_for_input"] and predicate(s))
        (output / f"{name}.json").write_text(json.dumps(state, indent=2))
        return state

    def stage(name):
        (sandbox / f"fixture-character-{name}").touch()

        def done():
            error = sandbox / "fixture-error.txt"
            if error.exists():
                raise RuntimeError(error.read_text())
            return (sandbox / f"fixture-character-{name}-done").exists()

        wait_for(process, name, done)

    def card(state, model, upgrade=0):
        return next(c for c in state["hand"] if c["model_id"] == model and c["upgrade_level"] == upgrade)

    def play(state, model, upgrade=0):
        selected = card(state, model, upgrade)
        assert selected["playable"] and selected["play_targets"], selected
        target = selected["play_targets"][0].get("target_id")
        send_command(process, bridge, "play_card", expected_state_id=state["state_id"],
                     card_id=selected["id"], **({"target_id": target} if target else {}))
        result = settled(f"after-{model.lower()}-{len(state['cards_played'])}", lambda s:
                         len(s["cards_played"]) == len(state["cards_played"]) + 1)
        assert result["cards_played"][-1]["card_id"] == selected["id"]
        return result

    def orb_values(state):
        return [(o["model_id"], o["passive"], o["evoke"]) for o in state["player"]["orbs"]]

    def osty(state):
        pets = state["player"]["pets"]
        assert len(pets) == 1 and pets[0]["model_id"] == "OSTY", pets
        assert all(e["model_id"] != "OSTY" for e in state["enemies"])
        return pets[0]

    state = settled("character-initial", lambda s: s["player"]["energy"] == 10
                    and len(s["hand"]) == (2 if case == "character-necrobinder" else 4))
    assert state["player"]["character"] == case.removeprefix("character-").upper()
    assert state["player"]["orb_slots"] == (3 if case == "character-defect" else 0)
    if case != "character-necrobinder":
        assert state["player"]["pets"] == []
    if case != "character-defect":
        assert state["player"]["orbs"] == []
    # Focused CLI observations must not filter away the newly exposed context.
    assert observation(state, view="combat")["state"]["player"] == state["player"]
    stage("audit")

    if case == "character-regent":
        assert read_json(sandbox / "fixture-ready.json")["starting_stars"] == 3
        assert state["player"]["stars"] == 1
        falling = card(state, "FALLING_STAR")
        assert falling["energy_cost"] == 0 and falling["star_cost"] == 2
        assert not falling["playable"] and falling["play_targets"] == []
        send_command(process, bridge, "play_card", expected_status="error",
                     card_id=falling["id"], target_id=state["enemies"][0]["id"])
        state = play(state, "VENERATE")
        assert (state["player"]["stars"], state["player"]["energy"]) == (3, 9)
        state = play(state, "FALLING_STAR")
        assert (state["player"]["stars"], state["player"]["energy"]) == (1, 9)
        assert state["enemies"][0]["hp"] == 292
        assert state["cards_played"][-1]["stars_spent"] == 2
        assert state["cards_played"][-1]["energy_spent"] == 0
        state = play(state, "REFINE_BLADE")
        blade = card(state, "SOVEREIGN_BLADE")
        assert blade["base_values"]["Damage"] == 19  # Base 10 + Forge 9.
        assert blade["retain_this_turn"] and "deck_card_id" not in blade
        state = play(state, "REFINE_BLADE", upgrade=1)
        stronger = card(state, "SOVEREIGN_BLADE")
        assert stronger["id"] == blade["id"]
        assert stronger["base_values"]["Damage"] == 32  # Add upgraded Forge 13.
        assert stronger["play_targets"][0]["values"]["Damage"] == 48  # Vulnerable.
        state = play(state, "SOVEREIGN_BLADE")
        assert state["enemies"][0]["hp"] == 244
        send_command(process, bridge, "end_turn")
        state = settled("regent-turn-two", lambda s: s["combat"]["player_turn"] == 2)
        assert state["player"]["stars"] == 1, "Stars persist between turns"

    elif case == "character-necrobinder":
        pet = osty(state)
        assert (pet["hp"], pet["max_hp"], pet["is_alive"]) == (1, 1, True)
        assert any(p["model_id"] == "STRENGTH_POWER" and p["amount"] == 2 for p in pet["powers"])
        hp = state["player"]["hp"]
        state = play(state, "BODYGUARD")
        assert (osty(state)["hp"], osty(state)["max_hp"]) == (6, 6)
        unleash = card(state, "UNLEASH")
        assert unleash["play_targets"][0]["target_id"] == state["enemies"][0]["id"]
        assert unleash["play_targets"][0]["values"]["CalculatedDamage"] == 14  # 6 + Osty HP 6 + Strength 2.
        state = play(state, "UNLEASH")
        assert state["enemies"][0]["hp"] == 286
        send_command(process, bridge, "end_turn")
        state = settled("osty-absorbs-attack", lambda s: s["combat"]["player_turn"] == 2)
        assert state["player"]["hp"] == hp
        assert (osty(state)["hp"], osty(state)["max_hp"]) == (3, 7)  # Hit 4, then summon 1.
        stage("kill-osty")
        state = settled("osty-dead", lambda s: any(c["model_id"] == "BODYGUARD" for c in s["hand"])
                        and s["player"]["pets"][0]["hp"] == 0)
        assert osty(state)["is_alive"] is False
        state = play(state, "BODYGUARD")
        assert (osty(state)["hp"], osty(state)["max_hp"], osty(state)["is_alive"]) == (5, 5, True)

    else:
        assert orb_values(state) == [("LIGHTNING_ORB", 3, 8), ("FROST_ORB", 2, 5), ("DARK_ORB", 6, 12)]
        assert all(o["name"] and o["hover_tips"] for o in state["player"]["orbs"])
        assert all("{" not in t.get("description", "") for o in state["player"]["orbs"] for t in o["hover_tips"])
        state = play(state, "DEFRAGMENT", upgrade=1)
        assert orb_values(state) == [("LIGHTNING_ORB", 5, 10), ("FROST_ORB", 4, 7), ("DARK_ORB", 8, 12)]
        assert any(p["model_id"] == "FOCUS_POWER" and p["amount"] == 2 for p in state["player"]["powers"])
        state = play(state, "ZAP")
        assert state["enemies"][0]["hp"] == 290, "Full queue evokes the front Lightning"
        assert orb_values(state) == [("FROST_ORB", 4, 7), ("DARK_ORB", 8, 12), ("LIGHTNING_ORB", 5, 10)]
        state = play(state, "DUALCAST")
        assert state["player"]["block"] == 14, "Evoke Frost twice, remove it only once"
        assert orb_values(state) == [("DARK_ORB", 8, 12), ("LIGHTNING_ORB", 5, 10)]
        state = play(state, "CAPACITOR")
        assert state["player"]["orb_slots"] == 5 and len(state["player"]["orbs"]) == 2
        stage("negative-focus")
        state = settled("negative-focus", lambda s: len(s["player"]["orbs"]) == 3
                        and s["player"]["orbs"][0]["passive"] == 1)
        assert orb_values(state) == [("DARK_ORB", 1, 12), ("LIGHTNING_ORB", 0, 3), ("PLASMA_ORB", 1, 2)]
        send_command(process, bridge, "end_turn")
        state = settled("defect-turn-two", lambda s: s["combat"]["player_turn"] == 2)
        assert orb_values(state) == [("DARK_ORB", 1, 13), ("LIGHTNING_ORB", 0, 3), ("PLASMA_ORB", 1, 2)]
        assert state["player"]["energy"] == 4, "Plasma ignores Focus and gives turn-start energy"

    stage("reset")
    reset = settled("character-reset", lambda s: s["combat"]["player_turn"] == 1 and not s["cards_played"])
    if case == "character-defect":
        assert reset["player"]["orb_slots"] == 3
        assert orb_values(reset) == [("LIGHTNING_ORB", 3, 8)]
    elif case == "character-necrobinder":
        assert (osty(reset)["hp"], osty(reset)["max_hp"]) == (1, 1)
        assert not any(p["model_id"] == "STRENGTH_POWER" for p in osty(reset)["powers"])
    else:
        assert reset["player"]["stars"] == 3
        cards = reset["hand"] + [c for pile in ("draw", "discard", "exhaust", "play") for c in reset["piles"][pile]]
        assert not any(c["model_id"] == "SOVEREIGN_BLADE" for c in cards)
