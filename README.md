# sts2-bridge

**Experimental v0.1.0.** A file-based Slay the Spire 2 state/action bridge for
external controllers. Exports structured observations and accepts semantic game
commands. Includes a Python CLI and an installable agent skill.

**Validated:** an Ironclad Ascension 1 clear through all three acts on native
Linux, game **v0.107.1**, using the shared CLI end-to-end. The run reached the
victory screen and unlocked Ascension 2; its chosen event branches needed no
manual workarounds.

Combat, card/potion choices, rewards, shops, treasure, rest sites, maps, and many
events are supported. Custom screens such as Crystal Sphere still need a human;
menus, victory/loss results, epochs, and run start/resume remain human-assisted
by design. Multiplayer and native Windows/macOS gameplay are unverified.
The protocol and game hooks may change.

## Install the mod

**Requirement:** Slay the Spire 2 installed.

1. Download the ZIP from the [latest bridge release](https://github.com/ethanfann/sts2-bridge/releases).
   Check its required game version and update Slay the Spire 2 through Steam if needed.
2. Close the game and back up any saves you care about. Use a separate test profile.
3. In Steam, select **Manage → Browse local files** to open the game directory.
   Extract the ZIP into `mods`, creating that directory if needed:

   ```text
   Slay the Spire 2/mods/sts2-bridge/
     sts2-bridge.dll
     sts2-bridge.json
     LICENSE
   ```

4. Enable mods in the game and restart if prompted. Start or continue a run by hand.
5. Check the game log for `STS2 Bridge writing state to ...` to find the data directory.

## Install the agent skill (includes the CLI)

**Requirements:** Python 3.8+ to run the CLI; Node.js/npm to run the skill installer.

```sh
npx skills add ethanfann/sts2-bridge --skill playing-sts2 --global
```

Choose your agent in the installer, or append its selector (for example,
`--agent amp`). `--global` makes the skill available across working directories;
omit it to install into the current agent project. Reload skills or restart the
agent after installation.

The [playing-sts2 skill](.agents/skills/playing-sts2/SKILL.md) ships its own
`scripts/sts2_bridge.py` and MIT license. The CLI implementation lives inside the
skill; root `bridge.py` is a checkout entry point to the same code.

The installed CLI's **`--help` is authoritative**. Resolve `<skill-dir>` from the
installer or the loaded skill's location:

```sh
python3 "<skill-dir>/scripts/sts2_bridge.py" --help
python3 "<skill-dir>/scripts/sts2_bridge.py" observe --help
python3 "<skill-dir>/scripts/sts2_bridge.py" act --help
python3 "<skill-dir>/scripts/sts2_bridge.py" act play_card --help
python3 "<skill-dir>/scripts/sts2_bridge.py" play-sequence --help
```

On Windows, use a Python 3.8+ interpreter such as `py -3` instead of `python3` if
needed. `--help` is read-only; an action without flags can still execute (for
example, `act end_turn`). Use help rather than probing commands.

## Working directories and live bridge files

| Directory | Contents and purpose |
| --- | --- |
| `<game>/mods/sts2-bridge/` | Installed DLL and JSON manifest; loaded at game startup. |
| `<skill-dir>/` | `SKILL.md`, `LICENSE`, and `scripts/sts2_bridge.py`. For Amp, a project install normally lives at `.agents/skills/playing-sts2/`, a global install at `~/.config/agents/skills/playing-sts2/`. Other agents may differ. |
| Agent working directory | Any chosen project/workspace. Use an absolute CLI path; changing this directory does not switch games or relocate runtime data. |
| `<bridge-data>/` | State, command transport, and recordings shared with the running game. Not the mods directory, repository, or save-profile directory. |
| Source checkout | `bridge.py`, build scripts, tests, and `dist/` outputs. |

The mod writes to Godot's `user://sts2-bridge`. On native Linux this is normally
`~/.local/share/SlayTheSpire2/sts2-bridge`; if `XDG_DATA_HOME` is configured for the
game, it lives under that data root instead. The **game log's absolute path is
authoritative**, especially on Windows, macOS, Proton, or when the game and agent
use different accounts/environments. Find `STS2 Bridge writing state to
.../state.json` and use that file's parent:

```sh
python3 "<skill-dir>/scripts/sts2_bridge.py" --bridge-dir "<bridge-data>" observe
```

`--bridge-dir` goes before the subcommand and should be repeated for every call.
Prefer an absolute path; a relative path resolves from the caller's working
directory. The option selects existing files, **not** where the mod writes them.
The client default is Linux's path; supply the logged path on other platforms.
The agent must have local filesystem access to the same files as the game. A
cloud agent cannot reach your desktop game by installing the skill alone.

```text
<bridge-data>/
  state.json           # Game-owned latest observation; persists after exit
  command.json         # Client-owned pending command; consumed by the game
  command-result.json  # Game-owned latest acknowledgement
  client.lock/         # Cooperative client lock while an action/sequence runs
  latest-session.json  # Pointer to the most recent recording session
  recordings/          # Persistent session timelines, states, and commands
```

Do not copy runtime files into the skill, mods folder, or repository. Saved
observations are not proof the game is still running. Keep only one command
writer active, and keep saves/recordings out of source control.

## Controller quickstart (protocol v1)

The examples below use `python3 bridge.py` from a source checkout. With an installed
skill, replace `bridge.py` with the absolute path to `scripts/sts2_bridge.py` above.
Run `--help` first for command syntax.

```sh
python3 bridge.py observe                        # Current decision; no full deck/piles/history
python3 bridge.py observe --view combat          # Includes piles and completed plays
python3 bridge.py observe --view deck
python3 bridge.py observe --view full            # Unfiltered snapshot
python3 bridge.py act play_card --card-id '<id>' --target-id '<enemy id>'
python3 bridge.py act select_choice --choice-id '<id>'
python3 bridge.py act end_turn
```

Use `--bridge-dir PATH` before the subcommand for another data directory. The
default path is native Linux's; on other systems use the path from the game log.
`observe --view choices` omits combat piles/hand/history while retaining active
selectors, rewards, shops, map, and player/relic context. Views name their omitted
sections and retain unknown fields and game tooltips; they do not summarize away
mechanics. Read the deck/piles when the decision needs them.

### One invocation for an ordered card sequence

```sh
python3 bridge.py play-sequence --if-state '<state_id from observe>' \
  '[{"card_id":"<bash id>","target_id":"<enemy id>"},{"card_id":"<strike id>","target_id":"<enemy id>"}]'
```

This sends **1–20 ordinary card plays, sequentially**, holding one cooperative
writer lock. Each step checks current playability/target eligibility, supplies an
`expected_state_id` guard, and waits for a new completed manual play in history
plus input readiness. It does not advance merely because a command was accepted.
It stops on a rejection, timeout, selector, room/turn change, unexpected play, or
hand change beyond removal of the played card. Drawing, exhausting another card,
or recovering a card therefore returns control to the agent for a fresh decision.
It never ends the turn, answers a selector, retries a command, or rolls back plays.

Output includes `status: completed|stopped`, `stop_reason`, each attempted play's
result and `completion_observed`, the `unsubmitted` tail, and a focused final
observation. A selector may interrupt an accepted card before its completion;
handle the selector rather than replaying that card. A lethal that changes rooms
also stops the sequence. Exit code is 0 for completion, 1 for a stop/error, and 2
for CLI argument errors. `--timeout` bounds each acknowledgement and completion
wait separately (10 seconds each by default). Do not resubmit a whole partial
sequence. This is an execution aid, not a tactical simulator or transaction.

`act --if-state '<id>' play_card ...` enables the same optional guard for a single
action. Without it, `act` submits an unguarded command. Single-action
output distinguishes `state_changed` from `effects_settled: null`; a fresh
observation alone is not proof of completion. Python callers can import
`read_state`, `act`, and `play_sequence` from `bridge.py` in a checkout.

### File protocol and execution guards

The bridge uses Godot's `user://sts2-bridge` directory, normally
`~/.local/share/SlayTheSpire2/sts2-bridge` on native Linux. Use the path printed in
the game log on other platforms. The game owns `state.json` and
`command-result.json`; one external writer owns `command.json`.

1. Read `state.json`. Check `scene`, `screen.supported`, `waiting_for_input`, and
   the relevant action eligibility flags. Unsupported screens require human input.
2. Use **instance IDs from the current observation**, not model names, tooltip IDs,
   deck indices, or IDs retained across a reload. Construct one command, for example:

   ```json
   {"command_id":"unique-per-submission","type":"play_card","card_id":"<hand card id>","target_id":"<enemy id>"}
   ```

3. Write a temporary file in the bridge directory, then atomically publish it as
   `command.json`. Never overwrite a pending command or run two command writers.
4. Wait for a result with the matching `command_id`; `status` is `ok` or `error`,
   with a `message`. Also wait for `command.json` to disappear before sending again.
5. Observe the resulting state and wait for the relevant action boundary. An `ok`
   result means the action was accepted, **not that all animations/effects settled**.
   A timeout is ambiguous; do not blindly retry. There is no durable exactly-once
   guarantee across reloads.

The mod advertises `command_guards: ["expected_state_id"]`. When supplied, that
field must match the published observation and its gameplay context must still
match the live game on the main thread immediately before execution. Guarded
commands from an earlier game process are rejected. Combat diagnostic labels
(including floating damage text) can update without changing `state_id`; other
gameplay-context changes invalidate it. This checks **observable** state, not
hidden RNG or a complete engine snapshot. Sequences require the guard capability.

The Python client atomically publishes complete commands without overwriting a
pending one and holds `client.lock` through result consumption. All writers must
cooperate: raw file writers and human gameplay can still interfere. If a client
is forcibly killed, the lock directory can remain; remove it only after confirming
no client is running, and inspect any pending command before sending another.
Timeouts leave pending commands intact and include the command ID for diagnosis.

| Commands | Additional fields |
| --- | --- |
| `play_card` | `card_id`, optional `target_id` |
| `end_turn`, `proceed`, `enter_merchant`, `leave_shop`, `open_treasure_chest` | None |
| `select_choice` | `choice_id` |
| `select_map_point` | `point_id` |
| `select_card` | `card_id` (toggle/select, according to the selector) |
| `confirm_card_selection` | None; only when confirmation is enabled |
| `select_card_reward_alternative` | `option_id` |
| `select_relic` | `relic_id` |
| `take_reward`, `skip_reward` | `reward_id` |
| `purchase_shop_item` | `item_id` |
| `select_rest_site_option` | `option_id` |
| `use_potion` | `potion_id`, optional `target_id` |
| `discard_potion` | `potion_id` |
| `mark` | `note` (recording annotation; no gameplay action) |

Try `python3 recording.py mark "controller connected"` for a harmless live command.
See the capability notes below for nested selectors and action-specific contracts.
Snapshots expose inspectable pile membership, not secret draw order, and relevant
game tooltips/calculated previews—not a complete effect simulator.

**Local trust boundary:** any process that can write into the bridge directory
can issue game commands. There is no network listener or authentication layer.
Do not synchronize the live command directory with an untrusted source. Recordings
are local and persistent; review them for paths, seeds, and notes before sharing.

## Combat context and previews

Snapshots include:

- `deck`: permanent deck instances; `hand` plus `piles.draw`, `discard`, `exhaust`,
  and `play`: current combat instances. `deck_card_id` links combat copies to
  their permanent cards. Generated cards can have no such link. IDs are opaque,
  stable across pile movement during a session, and not persistent across reloads.
- `piles.draw_order_known: false`: draw membership is sorted by model ID and
  opaque instance ID, **not draw sequence**. No hidden RNG state is exported.
- `cards_played`: ordered completed-play entries from the game's current combat
  history, including spent resources, target, replay index/count, auto-play flag,
  and current/previous-player-turn flags. Discarding a card is not a play. The
  `card.play_finished` timeline hook also records round and player turn. This hook
  is a game history milestone, not a guarantee all after-play effects have settled.
- Player/enemy `powers`: visible amounts and formatted descriptions. Player
  `relics`: descriptions, base variables, displayed counters, status and used-up
  flags. Counters are UI values and can temporarily show an activation animation;
  they are not arbitrary private relic state or generic trigger predictions.
- Enemy `intents`: announced type and description, plus game-calculated damage
  per hit, hit count and total for attacks. Damage is before absorbing block, not
  predicted HP loss. Hidden/non-attack intents have no invented damage value.
- Cards: model/type/target, upgrades, keywords, retain/exhaust flags, enchantment
  and affliction IDs, modified costs, base variables and `play_targets`. Each
  eligible hand-card target has variable previews calculated through the game's
  own hooks on **cloned** variables. A missing/null target means submit no target
  (including Self cards). `playable` checks resources and prevention hooks; use it
  only with `scene: combat` and `waiting_for_input: true`. In selectors the
  `playable` field means selectable, not playable in combat.

**These are observations and card-variable previews, not a turn simulator.**
Preview decimals may be rounded by actual resolution. They do not encompass all
after-play effects, generated cards, random outcomes, or effects of earlier
hypothetical actions. For example, Ornamental Fan's third attack grants 4
unpowered block after play; it is not part of that attack's card variables and
Dexterity/Frail do not modify it.

## Potion rewards and replacement

Owned `potions` expose `model_id`, formatted effects, tooltips, `base_values`,
zero-based `slot_index`, and `discard_available`. Their opaque `id` identifies
the actual potion instance for this session, not its name or position. Removing
another potion does not renumber IDs; refilling the same slot with the same type
does not revive an old ID. `player.potion_capacity` is the native belt capacity.

Potion reward rows include a nested `potion` preview with the same mechanics,
but no inventory ID or slot. Pass the outer row's `id` as `reward_id` to `take_reward`.
`selectable: false` plus `unavailable_reason: potion_belt_full` means the offer
remains available after freeing space; `potion_acquisition_blocked` means a
native game hook prevents acquisition. A rejected take does not consume the row.

For a full belt, the agent can choose either path:

- **Keep the belt:** collect any other wanted loot, then `skip_reward` with the
  last row's `reward_id`. This follows native Skip/Proceed. It rejects if other
  rows remain or skipping is disabled, rather than silently discarding other
  loot. `proceed` explicitly leaves **all** remaining rewards. For terminal combat
  rewards, Skip opens the map and native history is finalized on room departure.
- **Replace a potion:** `discard_potion` with the owned `potion_id`, wait for a
  snapshot showing the freed slot and selectable reward, then `take_reward`.
  Discard enqueues the native action, including discard hooks/history, without
  using the potion. An acknowledgement means queued, not completed; replacement
  is two explicit actions, not an atomic swap or automatic quality judgment.

Discard is supported on ordinary room/map/reward/shop screens and during idle
player combat input. It rejects stale IDs, queued/locked potions, dead/game-over
players, and covered card selectors. `use_potion` remains combat-only.

## Opened card-reward alternatives and relic previews

`card_selection.kind: card_reward` includes `alternatives`: the native buttons
currently offered alongside the cards. Each has an opaque `id`, native `kind`
(such as `Skip`, `REROLL`, or `SACRIFICE`), formatted `title`, `selectable`, and
`after_selected`. Send `select_card_reward_alternative` with `option_id` set to
the alternative's **id**, not its kind or array position.

The game owns the effects and completion rules:

- `EndSelectionAndDoNotCompleteReward`: Skip closes the card offer and returns
  to rewards. The card row remains available to reopen; other loot is untouched.
  After collecting other loot, `skip_reward` on the last row or `proceed` leaves
  it behind. For terminal combat rewards, native declined-card history is
  finalized when leaving the room.
- `EndSelectionAndCompleteReward`: alternatives such as Pael's Wing's Sacrifice
  run their effect and consume that card reward, without choosing a card.
- `DoNothing`: Reroll refreshes the cards and buttons while keeping selection
  open. Read a new snapshot before choosing again.

IDs identify the actual button instance for this session. Reopening or rerolling
replaces them; stale IDs, disabled/hidden buttons, covered screens, and completed
selections reject commands. An empty `alternatives` list does not authorize a
synthetic Skip. Other card selectors do not expose this field. `proceed` still
does not operate through a card-selection overlay.

Relic reward rows include the offered instance's formatted `description`,
`model_id`, `rarity`, `base_values`, and `hover_tips` **before pickup**. The row's
`id` remains the action ID for `take_reward`. Reads do not populate/reroll rewards,
award relics, or execute alternative effects.

## Combat-pile choices

The native `NCombatPileCardSelectScreen` handles **draw, discard, and exhaust**
choices through one bridge path, not card-specific handlers. When it is active,
the bridge exports `scene: card_selection` and `waiting_for_input: true`, even
though a card's play action may still be running while awaiting that choice.

`card_selection.kind: combat_pile` includes:

- `source_pile`, the game's prompt and configured `min_select` / `max_select`.
- `cards`: only the native grid's eligible cards, with the same instance IDs as
  the combat piles. Filters and live pile changes are respected. Draw choices
  are sorted independently of hidden draw order, including duplicate cards.
- `selected_card_ids`: the current selected set. The `playable` field
  means the card can be clicked: selected cards can still be toggled off at the
  limit; additional unselected cards cannot be selected beyond it.
- `require_manual_confirmation` and `confirm_available`: the latter follows the
  native Confirm button's enabled/visible state. These selectors have no cancel
  action. Confirming zero choices, where permitted, is a valid selection.

Use `select_card` with an offered `card_id` to toggle it. When
`require_manual_confirmation` is false, the native screen submits when enough
cards are selected. Otherwise send `confirm_card_selection` when
`confirm_available` is true; reaching the maximum alone does not submit the
choice. `proceed` is not a confirmation command.
As with other asynchronous commands, inspect the resulting state before acting
again. Combat actions cannot run through the selection overlay.

Ordinary pile-browsing screens and arbitrary custom selectors remain unsupported.

## In-hand combat choices

The native `NPlayerHand` selector exports `card_selection.kind: combat_hand`
(`combat_hand_upgrade` for Armaments-style upgrades), with `source_pile: hand`.
It uses the same prompt, bounds, selected IDs, and commands as pile choices.
Only eligible cards are offered, including selected cards moved into the native
selection/upgrade-preview container. Upgrade choices include `upgrade_preview`.

**Hand choices always require `confirm_card_selection`.** The native hand UI
does not auto-submit, even when its underlying preferences request it. Click
with `select_card`, inspect the selection, then confirm when `confirm_available`
is true. To replace a choice at the limit, toggle it off before picking another;
the bridge rejects extra choices rather than silently replacing the last one.

Ordinary hand cards export `playable: false` while selection is pending;
`card_selection.cards[].playable` still means selectable. Combat plays, end-turn,
and potion use/discard are blocked. A choice hidden by Peek or another screen
cannot be selected or confirmed through the bridge; human input must reveal it
first. Completing the choice returns control to the suspended native action.

## Deck selections and confirmation

`deck_transform`, `deck_upgrade`, `deck_enchant`, and `deck_card_select` export
the prompt, selection bounds, `selected_card_ids`, and `confirm_available`.
Every partial selection changes the snapshot, even before the minimum is met.
`select_card` toggles an eligible card; `cards[].playable` means clickable.

**Deck selections require explicit `confirm_card_selection`.** Reaching the
maximum opens the native preview; it does not apply the effect. Cards cannot
be clicked through that preview. A range selection can require two confirmations:
Continue from the grid, then Confirm in the preview. Inspect the resulting state
after each command, and confirm only while `confirm_available` is true.

`simple_card_select` uses the same fields but follows its native preferences:
it may auto-submit at the limit or require confirmation, including zero choices
when permitted. A selector covered by the map or hidden by Peek cannot receive
bridge selections or confirmations. `proceed` is not a confirmation command.

## Upgrade choice previews

On `card_selection.kind: deck_upgrade`, each eligible card includes an
`upgrade_preview` with its upgraded name, description, costs, upgrade level,
type/target, keywords, base variables, and relevant tooltips. The outer card
remains the original, with the action ID used by `select_card`.

Previews use the same instance-clone and internal-upgrade path as the native
**View Upgrades** checkbox. Both versions are available regardless of checkbox
state, including cards below the visible grid. Reads do not toggle the UI,
register preview cards in the run, or upgrade the actual deck. Preview objects
have no action IDs, playability flags, or combat-target predictions. These are
upgrade-screen previews, not simulations of upgrade-triggered relic effects.

On both `deck_upgrade` and `combat_hand_upgrade`, `select_card` selects the card;
`confirm_card_selection` commits the choice when available. Neither path
automates the checkbox. Other selectors and ordinary deck snapshots omit
`upgrade_preview`.

## Relevant mechanics in the observed state

`hover_tips` accompanies event choices, cards (deck, combat piles, and selectors),
owned relics, offered relics in selection/treasure screens, owned potions, and
merchant items.
It exports the **installed game's own tooltip list**, with resolved descriptions
and original rich-text markup.
For example, Neow's Torment includes Neow's Fury's 1-energy cost, 10 damage,
up-to-2-card discard recovery, and Exhaust explanation. Silken Tress includes
Glam (Replay once per combat) and Replay (play an additional time).

- Each tooltip has its native `id`, `kind`, `title`, and `description`.
- `kind: card` also has informational `card` metadata: model ID, cost, star cost,
  type/target, rarity, and upgrade level. These are references, **not selectable
  or playable instances**. Neither the tooltip ID nor its model ID is an action
  handle. Use the owning choice/card/relic's ordinary action ID.
- Event relic offers also include `relic_model_id` for unambiguous association.
- An unrecognized tooltip type becomes `kind: unsupported` with
  `unsupported_type`, rather than disappearing or gaining invented mechanics.
- An empty list means the game supplies no additional tips here, not that the
  effect is trivial or fully modeled. Only supplied references are exported;
  the bridge does not enumerate hidden offers or recursively expand the catalog.

Potion reward rows include a nested mechanics preview. Ancient dialogue is
observable in `screen.visible_controls`; absent event descriptions are empty.

Merchant items include native formatted descriptions, `model_id`, `base_values`,
and relevant tooltips. Card offers also include a `card` snapshot with energy/star
costs, keywords, upgrade level, and other ordinary card metadata. The outer
`cost` remains the **gold price**; `card.energy_cost` is the play cost. Numeric
variables retain icon-only effects such as Bloodletting's `Energy: 2`.
Use the outer `shop_*` ID with `purchase_shop_item`, not the nested card ID.
Shop cards are not playable or part of the deck until purchased. Base values
are model parameters, not predictions of every relic interaction.

Rest-site descriptions use the option's native formatter to resolve heal amounts,
Smith counts/plurals, and relic-added text. This preserves the game's wording:
Regal Pillow lists base healing plus its bonus separately, not as a summed total.
Observations do not regenerate options, purchase items, or execute rest effects.

Treasure offers include only visible, enabled, interactive holders while relic
selection is active. Unused multiplayer slots are neither read nor selectable.
`open_treasure_chest` uses the native click handler and rejects repeat opening;
`select_relic` uses the offered relic's outer `id`. `proceed` follows the native
Skip/Proceed handler when enabled. Offers stay empty before opening, during
animations, after collection, and for genuinely empty chests; check the action
availability flags rather than interpreting an empty list as a completed chest.

## Map destinations

`map.points` comes from the native map screen's point nodes, including the
starting ancient, boss, and optional second boss, rather than just the regular
grid. Child IDs therefore include special destinations in the same graph.
`travelable` follows the native node's state and enabled/visible flags, with
travel disabled during room completion, travel animations, or map drawing.
The bridge does not enable debug travel or reconstruct adjacency rules itself.

`select_map_point` accepts only an exact exported `point_id` that is currently
selectable on the active map. It uses the native selection/vote action, not a
direct call to the downstream travel method. Wait for the resulting room state;
a command acknowledgement is not proof that the transition has completed.
Second-boss and multiplayer transitions are unverified.

## Session recordings

Recording starts automatically when the mod loads. Add a note with the CLI:

```sh
python3 "<skill-dir>/scripts/sts2_bridge.py" act mark --note "Finished Act 1"
```

From a source checkout, inspect saved recordings with:

```sh
python3 recording.py status
```

`status` summarizes saved observations, unsupported screens, error counts, and
notes; it is **not** a liveness check. A successful `mark` confirms that the
running mod saved the note. Use `--bridge-dir PATH` before the subcommand to
select another data directory.
Do not run another command writer concurrently. A timed-out marker may still be
pending; it is not silently removed or resent.

Each launch creates `recordings/<UTC-time>-<id>/session.json` and
`timeline-0001.jsonl` (additional segments at 16 MiB). The ordered timeline contains
full changed snapshots, hook events, bridge commands/results, notes, and export
errors. Snapshots include seed, event ID, active screen text/controls, and combat
turn/phase context. Hook events include manual card plays and event choices.
Snapshots are debounced, not frame-by-frame; this is diagnostic evidence, **not a
deterministic replay or a complete capture of every click**. `observed_state_id`
means the last exported observation, not that an asynchronous action is finished.

Unsupported screens remain observable and playable by hand. Combat context is
documented above. Recordings stay local and persist across launches. Remove
session folders manually when no longer needed. If recording storage fails,
gameplay continues, the game log reports the failure, and `mark` returns an error.

## Build from source

**Requirements:** .NET 9 SDK and the installed game.

On Linux:

```sh
./build-and-deploy.sh build
./build-and-deploy.sh install  # Close the game first
```

Add `--game-dir PATH` for a different Steam library. On Windows, use
`.\build-and-deploy.ps1 build` and `.\build-and-deploy.ps1 install`, with
`-GameDir PATH` for a different library. Builds write the DLL and manifest to
`dist/local/`; installation copies them into the game's `mods/sts2-bridge/`.

Tests require Python 3 and, for game fixtures, Linux with bubblewrap (`bwrap`):

```sh
python3 -m unittest discover -s tests -p 'test_*.py' -v
./build-and-deploy.sh smoke
./build-and-deploy.sh events
./build-and-deploy.sh combat
```

Game tests use disposable offline save data. Logs and recordings are retained in
`dist/smoke/run-*/`.

`python3 release.py --tag v0.1.0` builds against
[Book.StS2.RefLib 0.107.1](https://www.nuget.org/packages/Book.StS2.RefLib/0.107.1)
and writes the DLL, manifest, ZIP, license, and checksums to `dist/release/`.
Reference assemblies are compile-only and excluded from the package.

## License

Bridge code is [MIT licensed](LICENSE). Game content and third-party dependencies
remain under their owners' terms.

The repository's `icon.svg` and raster `mod_image.png` use the default Godot
project-icon styling and Godot Engine logo, copyright © 2017 Andrea Calabró,
licensed under [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/)
([upstream notice](https://github.com/godotengine/godot/blob/master/misc/logo/LICENSE.txt)).
These repository-only images are excluded from the release package.
