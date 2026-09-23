---
name: playing-sts2
description: "Reads Slay the Spire 2 state and submits actions through the STS2 Bridge CLI. Use when asked to play, inspect, or test STS2 through the bridge mod, including combat sequences, card choices, events, shops, and map travel."
license: MIT
compatibility: "Requires Python 3.8+ and filesystem access to the running game's STS2 Bridge data directory. No Python packages, source checkout, MCP server, or model credentials required."
---

# Playing STS2

Use the bundled CLI to observe and control the user's Slay the Spire 2 run.
This skill describes the interface, not a tactical policy or card tier list.
Do not issue gameplay actions when the user only asked to inspect the run.

## Learn the installed CLI

**The installed CLI's `--help` is the source of truth for syntax, options, and
command semantics.** Resolve `scripts/sts2_bridge.py` relative to this `SKILL.md`,
not relative to the agent's working directory. Replace `<skill-dir>` below with
that absolute directory. Start with:

```sh
python3 "<skill-dir>/scripts/sts2_bridge.py" --help
```

Then read the relevant help, including action-specific arguments:

```sh
python3 "<skill-dir>/scripts/sts2_bridge.py" observe --help
python3 "<skill-dir>/scripts/sts2_bridge.py" act --help
python3 "<skill-dir>/scripts/sts2_bridge.py" act play_card --help
python3 "<skill-dir>/scripts/sts2_bridge.py" play-sequence --help
```

Use a Python 3.8+ interpreter (`py -3` on Windows if appropriate). Help is
read-only. Do not probe actions by omitting arguments: `act end_turn`, for
example, executes. Discover the current action list through `act --help` rather
than assuming this document is an exhaustive command reference.

## Keep the directories separate

| Location | Purpose |
| --- | --- |
| `<game>/mods/sts2-bridge/` | Contains `sts2-bridge.dll` **and** `sts2-bridge.json`. Installed with the game closed, then enabled in-game. Not a state directory. |
| This skill's directory | Contains this guide, `LICENSE`, and the self-contained CLI under `scripts/`. Installed by `npx skills`; no repository checkout needed. |
| Agent working directory | Any workspace the user chooses. It does not select the game or relocate bridge data. Use the absolute CLI path from here. |
| Bridge data directory | Live `state.json`, `command.json`, `command-result.json`, `client.lock`, and `recordings/`. Both the game and client must access the same directory. |

The mod writes to Godot's `user://sts2-bridge`. On native Linux the usual path is
`~/.local/share/SlayTheSpire2/sts2-bridge` (or under `XDG_DATA_HOME` when configured).
**The game log is authoritative:** `STS2 Bridge writing state to .../state.json`.
Use that file's parent directory. The CLI default is Linux's; on Windows, macOS,
Proton, or a different account, use the path from that game's log explicitly.
Do not guess a save-profile, Steam, or repository path.

Put `--bridge-dir` **before** the subcommand and reuse it for every call:

```sh
python3 "<skill-dir>/scripts/sts2_bridge.py" --bridge-dir "<absolute-data-dir>" observe
```

This selects existing data; it does not configure the mod's output directory.
Relative data paths resolve from the caller's working directory, so prefer an
absolute path. Run on the game machine with access to its files; a cloud agent
does not gain access to the user's game just by installing this skill. There is
no network listener or MCP server to start.

## Observe, decide, act, verify

1. Confirm the user has enabled the mod and started/resumed the intended run.
   Menus, epochs/unlocks, and unsupported screens require human input. Do not edit
   saves or bypass them. Read first; check the run/character, `scene`,
   `screen.supported`, `waiting_for_input`, and relevant eligibility flags.
2. Request the view needed for the decision. `observe --help` describes omissions.
   Combat needs hand, enemies, relics/powers, piles, and completed plays; deck
   construction may need the permanent deck. Read `full` when context is missing.
   Tooltips/calculated values are evidence, not a complete effect simulator;
   unknown draw order is not permission to inspect hidden game data.
3. Read instance/choice/target IDs from current JSON. Never derive them from model
   names, descriptions, list positions, or an earlier run. Prefer guarded actions
   using the observed `state_id`; discover the flag placement in `act --help`.
   `command_guards` advertises DLL capabilities; missing support requires a
   compatible mod, not stripping the guard from a failed sequence.
4. Use `play-sequence` for a decided same-turn card chain. Let it stop at selectors,
   changed hands, or other decision boundaries. Read `steps`, `stop_reason`,
   `unsubmitted`, and the returned observation. An accepted card interrupted by a
   selector is already in progress: resolve the selector, not replay the card.
5. Verify the expected effect and next input boundary. A single action's `ok`
   means accepted, not completed; `state_changed` alone is not proof either.
   Observations persist after exit and unchanged timestamps are not proof the
   game is dead. When a liveness check is needed, `act mark --help` documents a
   recording marker that does not change gameplay.

## Stop safely

- One command writer at a time. Human input and raw file writers can bypass the
  cooperative lock; coordinate before taking control. Use the CLI, not ad-hoc
  writes to `command.json`.
- A timeout can mean the command is still executing. Inspect the result and state;
  never blindly resend a command or a partially completed sequence. No rollback
  or exactly-once guarantee exists across reloads.
- Do not overwrite pending commands or remove a live client's `client.lock`.
  After a forced client exit, confirm no writer remains and inspect the pending
  command before cleaning up a stale lock. Do not delete recordings or saves.
- Report unsupported screens and missing context; request human help rather than
  guessing IDs or falling back to mouse/keyboard automation unasked.
- Records can contain seeds, paths, and user notes. Do not publish them without
  the user's permission. Installing this skill does not authorize gameplay,
  changes to the mod installation, or uploads.
