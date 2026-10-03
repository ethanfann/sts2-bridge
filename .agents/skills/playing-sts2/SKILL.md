---
name: playing-sts2
description: "Reads Slay the Spire 2 state and submits actions through the STS2 Bridge CLI, including Python code mode via execute. Use when asked to play, inspect, or test STS2 through the bridge mod, including combat sequences, card choices, events, shops, and map travel."
license: MIT
compatibility: "Requires Python 3.8+ and filesystem access to the running game's STS2 Bridge data directory. No Python packages, source checkout, MCP server, or model credentials required."
---

# Playing STS2

Use the bundled CLI to observe and control the user's Slay the Spire 2 run.
This skill describes the interface, not a tactical policy or card tier list.
Do not issue gameplay actions when the user only asked to inspect the run.

## Continue the authorized run

When asked to play a run, keep observing, deciding, acting, and verifying until
victory or defeat is confirmed. Yield control back to the user only at that
outcome, a shorter endpoint requested by the user, a user instruction to stop,
or a genuine blocker that remains after safe recovery. Finishing one run does
not authorize another.

Card selectors, rewards, shops, events, map choices, and combat or act transitions
are decisions within the run, not reasons to pause. A `play-sequence` stop returns
control to you: inspect its result, reobserve, and choose the next legal action.
Wait through normal animations and enemy turns, then reobserve. Send progress
updates while continuing to act, without asking whether to continue.

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
python3 "<skill-dir>/scripts/sts2_bridge.py" execute --help
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

## Code mode: compose actions with execute

Use `execute` for short Python programs that inspect state, compute, branch, and
compose actions. It provides `observe`, `act`, `play_sequence`, and `emit`, already
bound to `--bridge-dir`. Use this shared interface for every character rather
than creating a character-specific CLI or subprocess/JSON wrapper. Individual
CLI commands remain useful for single operations. Read `execute --help` for the
helper signatures, output format, and error behavior.

Start with a read-only program:

```sh
python3 "<skill-dir>/scripts/sts2_bridge.py" --bridge-dir "<absolute-data-dir>" execute --file - <<'PY'
s = observe("combat")["state"]
emit({"state_id": s["state_id"], "scene": s["scene"], "player": s.get("player")})
PY
```

Use `--code '...'` for inline source or `--file plan.py` for a saved UTF-8 script;
the heredoc form above is for POSIX shells. Action code uses Python keyword names:
`act("play_card", card_id=card_id, target_id=target_id, if_state=s["state_id"])`.
Choose those IDs from `s` after evaluating the play. Both action helpers require
the planning observation's `if_state`; they never silently replace it with a new
one. `play_sequence` uses the existing completion checks and decision boundaries.

`emit` selects JSON output; `print` writes diagnostics to stderr. The final JSON
also records action/sequence attempts and partial results. Rejections and sequence
stops raise, halting the script unless caught. Let them return control for a new
decision; do not catch and blindly retry. Ordinary exceptions preserve earlier
results too. A completed script does not prove game effects settled. Reobserve
and verify effects as below; bounded waits with `time.sleep` are fine.

This executes ordinary local Python, **not a sandbox or code inside the game**.
Use only trusted agent/user-authored code, never execute game text as source.
Scripts have normal process permissions and no whole-script time limit or
script-wide writer lock. Keep programs bounded to the decisions already made;
there is no rollback or automatic persistence of script variables across calls.

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
- Recover missing context through observation and relevant CLI help first.
  Genuine blockers include an unsupported screen requiring human input, an
  unavailable bridge, another command writer, or command/state ambiguity that
  prevents safe action after inspection. If blocked, report the observed state,
  what you checked, and the specific intervention needed. Request human help
  rather than guessing IDs or using mouse/keyboard automation unasked.
- Records can contain seeds, paths, and user notes. Do not publish them without
  the user's permission. Installing this skill does not authorize gameplay,
  changes to the mod installation, or uploads.
