# STS2 Agent Monorepo Architecture

## Purpose

Define the recommended monorepo architecture for building a **Slay the Spire 2 AI agent** using the easiest reliable path:

- **STS2 mod bridge** for exact game-state export and semantic action execution
- **TypeScript agent runtime** for planning, orchestration, logging, and memory
- **single monorepo** for shared schemas, tooling, and docs

This architecture intentionally avoids screenshot/OCR-first state extraction as the primary interface.

---

## Core recommendation

Use a **monorepo** with two main apps:

1. **`apps/sts2-bridge-mod`**
   - a C# / Godot .NET STS2 mod
   - exports exact game state to JSON or local IPC
   - receives semantic commands from the external agent
   - validates and executes those commands through game hooks

2. **`apps/agent-runtime`**
   - a TypeScript/Node runtime
   - reads exported game state
   - calls cloud/local models for reasoning
   - chooses semantic actions
   - writes commands back to the bridge
   - persists runs, steps, actions, and later memory

This gives you the best split:

```text
STS2 Game <-> Bridge Mod <-> Agent Runtime
```

---

## Why this architecture

### Why use a mod bridge
A mod bridge is better than OCR for STS2 because it can expose exact values for:

- player HP / max HP
- energy
- block
- gold
- hand cards
- draw / discard / exhaust piles
- enemy HP
- enemy intents
- relics
- buffs / debuffs
- rewards
- map choices
- shop items
- event options

And it can execute semantic actions like:

- `play_card(card_id, target_id?)`
- `end_turn()`
- `pick_reward(index)`
- `choose_map_node(index)`

instead of guessing at pixels.

### Why keep the agent runtime in TypeScript
The agent/runtime side benefits from:
- better ergonomics for you
- shared typing
- easier integration with qmd later
- straightforward cloud API clients
- clean monorepo tooling

### Why not separate repos
A monorepo makes shared development much easier:
- one place for docs
- one place for JSON schemas
- one place for integration scripts
- simpler local development
- simpler CI later

---

## High-level architecture

```text
+-----------------------------------------------------------------------------------+
|                               STS2 AGENT MONOREPO                                 |
+-----------------------------------------------------------------------------------+

   apps/sts2-bridge-mod                                  apps/agent-runtime
+----------------------------+                       +------------------------------+
| STS2 Mod / Bridge          |                       | Agent Runtime                |
|----------------------------|                       |------------------------------|
| - hook game state          |   state JSON / IPC    | - read game state            |
| - export stable state      +---------------------> | - choose next action         |
| - read command queue       |                       | - call reasoning model       |
| - validate actions         |   command JSON / IPC  | - persist run/step logs      |
| - execute semantic action  | <---------------------+ - later memory/qmd           |
+-------------+--------------+                       +---------------+--------------+
              |                                                      |
              v                                                      v
        Slay the Spire 2                                        SQLite / files

```

---

## Monorepo structure

```text
sts2-agent/
  README.md
  package.json
  bun.lockb / package-lock.json
  turbo.json
  tsconfig.base.json
  .gitignore

  apps/
    agent-runtime/
      package.json
      src/
        main.ts
        cli.ts
        runtime/
        planner/
        storage/
        bridge/
        logs/
      tsconfig.json

    bridge-client/
      package.json
      src/
        read-state.ts
        write-command.ts
        schemas.ts
      tsconfig.json

    sts2-bridge-mod/
      README.md
      sts2-bridge-mod.csproj
      src/
        ModEntry.cs
        Bridge/
          StateExporter.cs
          CommandReader.cs
          ActionExecutor.cs
          StableStateDetector.cs
        Hooks/
        Models/
        Utils/

  packages/
    schemas/
      package.json
      src/
        state.ts
        actions.ts
        protocol.ts

    shared-config/
      package.json
      src/

    docs-config/
      package.json
      src/

  scripts/
    dev-agent.sh
    dev-agent.ps1
    copy-mod-artifacts.sh
    copy-mod-artifacts.ps1
    run-integration-smoke.sh

  docs/
    architecture/
      monorepo-architecture.md
      bridge-protocol.md
      runtime-loop.md
      state-schema.md
      action-schema.md
    research/
      links.md
```

---

## Responsibilities by app/package

## `apps/sts2-bridge-mod`

### Responsibility
The bridge mod is the only code that talks directly to STS2 internals.

### It should:
- initialize on game load
- detect relevant game scenes and state changes
- export stable state snapshots
- read external commands
- validate commands against current state
- execute commands through game methods/hooks
- report results and errors

### It should not:
- call cloud APIs
- contain high-level strategy logic
- contain long-term memory logic
- become a general-purpose agent runtime

### Output responsibilities
- write current state to JSON file or local IPC
- write action execution result
- optionally write debug logs

---

## `apps/agent-runtime`

### Responsibility
Own the external agent loop.

### It should:
- read bridge state
- decide when the game is waiting for input
- prepare model/planner input
- choose the next action
- write the command back to the bridge
- log runs, steps, responses, and outcomes
- pause safely on invalid states or repeated failures

### It should not:
- know internal game implementation details
- perform UI clicking directly
- bypass the bridge mod

---

## `apps/bridge-client`

### Responsibility
A tiny TypeScript library/app for talking to the bridge.

This can later be merged into `agent-runtime`, but keeping it separate early can help with integration testing.

### It should:
- load latest state
- validate schemas
- write commands atomically
- read command results
- expose typed helpers to the runtime

---

## `packages/schemas`

### Responsibility
Shared protocol definitions.

### It should define:
- game state schema
- action schema
- command/result schema
- error schema

### It should be the single source of truth for:
- agent-runtime types
- bridge-client types
- protocol documentation

---

## Bridge protocol design

Start simple.

## Transport v1
Use **filesystem-based JSON**.

### Why
- simplest to debug
- easiest to inspect manually
- no socket server required for first version
- robust enough for turn-based gameplay

### Suggested files
```text
runtime/
  state.json
  command.json
  result.json
  logs/
```

### Rules
- bridge writes `state.json` atomically
- runtime writes `command.json` atomically
- bridge writes `result.json` after execution
- every payload includes IDs and timestamps
- stale commands are rejected

### Later upgrade path
If needed, move to:
- named pipe
- local TCP
- WebSocket

Do not start there.

---

## State export design

The bridge should export **stable state snapshots**.

### Stable means
The game is waiting for meaningful player input, not in the middle of:
- animations
- action queue resolution
- transitions
- reward reveal animations
- map movement animations

This follows the same broad idea used by Slay the Spire 1 CommunicationMod:
export when the game is stable enough for an external controller to act.

---

## State schema v1

```json
{
  "protocol_version": 1,
  "state_id": "state_000123",
  "timestamp": "2026-03-15T18:20:00Z",
  "scene": "combat",
  "waiting_for_input": true,
  "run": {
    "floor": 12,
    "act": 1,
    "turn": 3
  },
  "player": {
    "hp": 54,
    "max_hp": 70,
    "block": 8,
    "energy": 2,
    "max_energy": 3,
    "gold": 99
  },
  "enemies": [
    {
      "id": "enemy_0",
      "name": "Jaw Worm",
      "hp": 38,
      "max_hp": 46,
      "block": 0,
      "intent": {
        "type": "attack",
        "value": 12
      },
      "powers": []
    }
  ],
  "hand": [
    {
      "id": "card_0",
      "name": "Strike",
      "cost": 1,
      "upgraded": false,
      "playable": true
    }
  ],
  "draw_pile": 9,
  "discard_pile": 2,
  "exhaust_pile": 0,
  "relics": [],
  "potions": [],
  "legal_actions": []
}
```

---

## Action protocol design

The runtime should send **semantic commands**, not raw pixels or input events.

### Command examples

```json
{
  "protocol_version": 1,
  "command_id": "cmd_000001",
  "state_id": "state_000123",
  "timestamp": "2026-03-15T18:20:01Z",
  "action": {
    "type": "play_card",
    "card_id": "card_0",
    "target_id": "enemy_0"
  }
}
```

```json
{
  "protocol_version": 1,
  "command_id": "cmd_000002",
  "state_id": "state_000124",
  "timestamp": "2026-03-15T18:20:03Z",
  "action": {
    "type": "end_turn"
  }
}
```

### Action types to support first
- `end_turn`
- `play_card`
- `choose_reward`
- `skip_reward`

### Action types to support next
- `choose_map_node`
- `choose_event_option`
- `buy_shop_item`
- `leave_shop`
- `campfire_action`

---

## Validation rules

The bridge mod should validate all commands before execution.

### It should reject:
- stale `state_id`
- invalid card IDs
- invalid targets
- actions not legal in the current scene
- actions during unstable game state
- duplicate command IDs

### It should return:
- `accepted`
- `executed`
- `rejected`
- `failed`

with a machine-readable reason.

---

## Runtime loop

```text
[wait for state]
      |
      v
[validate state schema]
      |
      v
[if waiting_for_input == false]
      |
      +--> sleep / wait
      |
      v
[build planner input]
      |
      v
[call model / planner]
      |
      v
[validate planner action]
      |
      v
[write command.json]
      |
      v
[wait for result.json or new state]
      |
      v
[log step + continue]
```

---

## Why this is easier than OCR

### Mod bridge route
- exact state
- no pixel guessing
- semantic actions
- simpler planner input
- better long-term reliability

### OCR route
- ambiguous state extraction
- icon parsing pain
- cursor/input synchronization problems
- lots of brittle glue

For STS2, the mod bridge is the right primary architecture.

---

## Recommended milestones

## Milestone 1 — monorepo scaffold
Deliverables:
- monorepo root
- TypeScript workspace
- C# mod project in `apps/sts2-bridge-mod`
- shared schema package
- docs folder

Acceptance:
- `agent-runtime` builds
- `schemas` package builds
- mod project builds

## Milestone 2 — bridge mod bootstrap
Deliverables:
- mod loads into STS2
- startup log written
- no state export yet

Acceptance:
- can confirm mod initialization in-game

## Milestone 3 — state snapshot export
Deliverables:
- one-shot JSON state export
- schema-aligned output
- debug trigger or combat-start export

Acceptance:
- runtime can read and validate exported JSON

## Milestone 4 — stable state export loop
Deliverables:
- bridge writes `state.json` at stable decision points
- runtime polls/reads state

Acceptance:
- state updates during combat and reward flow

## Milestone 5 — command ingress
Deliverables:
- runtime writes command file
- bridge reads and validates command
- bridge writes result file

Acceptance:
- `end_turn` works end-to-end

## Milestone 6 — card/action execution
Deliverables:
- `play_card`
- optional target selection
- result reporting

Acceptance:
- a targeted or untargeted card can be played by command

## Milestone 7 — expand scenes
Deliverables:
- rewards
- shops
- events
- map
- campfire

Acceptance:
- full-run control becomes possible

---

## Development workflow

## Local workflow
1. build or copy the mod into STS2 `mods`
2. run STS2
3. run `agent-runtime` in watch/dev mode
4. inspect `runtime/state.json`, `runtime/command.json`, `runtime/result.json`
5. iterate on protocol and hooks

## Why this workflow is good
- inspectable
- reproducible
- no hidden transport layer
- simple enough for fast iteration

---

## Recommended documentation set

Store these in `docs/architecture/`:

- `monorepo-architecture.md`
- `bridge-protocol.md`
- `state-schema.md`
- `action-schema.md`
- `runtime-loop.md`

Store research links in `docs/research/links.md`.

---

## Relevant links

### STS2 modding foundations
- BaseLib-StS2  
  https://github.com/Alchyr/BaseLib-StS2

- ModTemplate-StS2  
  https://github.com/Alchyr/ModTemplate-StS2

- STS2FirstMod  
  https://github.com/jiegec/STS2FirstMod

### Godot modding references
- Godot Mod Loader wiki  
  https://wiki.godotmodding.com/

- Script extensions guide  
  https://wiki.godotmodding.com/guides/modding/script_extensions/

- Mod loader API / script hooks reference  
  https://wiki.godotmodding.com/api/mod_loader_mod/

### Prior art from Slay the Spire 1
- CommunicationMod  
  https://github.com/ForgottenArbiter/CommunicationMod

- spirecomm  
  https://github.com/ForgottenArbiter/spirecomm

### STS2 community example
- BoberInSpire discussion / JSON export overlay example  
  https://www.reddit.com/r/slaythespire/comments/1rtjafp/slay_the_spire_overlay_opensource_fun_project/

---

## Recommended next step

Implement the monorepo scaffold and get to **Milestone 2** as fast as possible:

- monorepo exists
- schemas package exists
- mod loads in STS2
- startup log proves the bridge is alive

Once that works, move immediately to **one-shot state export** before doing any action hooks.

---

## Summary

The recommended architecture is:

```text
Monorepo
  ├─ STS2 bridge mod (C# / Godot .NET)
  ├─ Agent runtime (TypeScript / Node)
  ├─ Shared protocol schemas
  └─ Shared docs and scripts
```

The bridge mod owns:
- game hooks
- exact state export
- semantic action execution

The runtime owns:
- orchestration
- model calls
- logging
- later memory and learning

This is the cleanest and most maintainable path for an STS2 agent.
