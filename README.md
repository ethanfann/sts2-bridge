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
The [roadmap](ROADMAP.md) separates completed bridge work from future decision
policies, Jev, retrieval, and simulation.

Bridge code is [MIT licensed](LICENSE). Game content and third-party dependencies
remain under their owners' terms; the MIT license does not grant rights to them.

The repository's inherited `icon.svg` and `mod_image.png` are default Godot project
icons using the Godot Engine logo, copyright © 2017 Andrea Calabró, licensed under
[CC BY 4.0](https://creativecommons.org/licenses/by/4.0/)
([upstream notice](https://github.com/godotengine/godot/blob/master/misc/logo/LICENSE.txt)).
They retain the default project-icon styling; `mod_image.png` is a raster version.
Neither image is included in the DLL-only release. They are not STS2 Bridge branding
or an endorsement by Godot.

## Install the mod

**Requirement:** Slay the Spire 2 v0.107.1.

1. Close the game and back up any saves you care about. Use a separate test profile.
2. From a [tagged release](https://github.com/ethanfann/sts2-bridge/releases), extract
   `sts2-bridge-v0.1.0.zip` into the game's `mods` directory. Steam's **Manage → Browse
   local files** opens the game install directory. Create `mods` if absent:

   ```text
   Slay the Spire 2/mods/sts2-bridge/
     sts2-bridge.dll
     sts2-bridge.json
     LICENSE
   ```

   Or download the DLL and JSON separately and place them in that same folder.
   **The JSON manifest is required; a DLL by itself is not the full installation.**
3. Enable mods in the game and restart if prompted. Start or continue a run by hand.
4. Check the game log for `STS2 Bridge writing state to ...` to find the data directory.

Later game patches require fresh runtime validation.

**Migrating from FirstMod:** move `mods/FirstMod` outside the mods directory before
installing. Do not load both mods. The mod ID, assembly, and data directory have
changed; old saves may require the old mod or a new test profile. Existing
`first-mod-bridge` recordings are preserved, not migrated. Point `recording.py
--bridge-dir` at the old directory to inspect them.

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

These are separate locations; the agent does **not** need to work inside Steam:

| Directory | Contents and purpose |
| --- | --- |
| `<game>/mods/sts2-bridge/` | Installed DLL and JSON manifest; loaded at game startup. |
| `<skill-dir>/` | `SKILL.md`, `LICENSE`, and `scripts/sts2_bridge.py`. For Amp, a project install normally lives at `.agents/skills/playing-sts2/`, a global install at `~/.config/agents/skills/playing-sts2/`. Other agents may differ. |
| Agent working directory | Any chosen project/workspace. Use an absolute CLI path; changing this directory does not switch games or relocate runtime data. |
| `<bridge-data>/` | State, command transport, and recordings shared with the running game. Not the mods directory, repository, or save-profile directory. |
| Source checkout (developers only) | `bridge.py`, build scripts, tests, and `dist/` outputs. A checkout may still be named `first-mod`; runtime paths do not depend on its name. |

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
Run `--help` first; the same client drives the sandbox tests and installed skill.

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
action. Without it, `act` retains the original command behavior. Single-action
output distinguishes `state_changed` from `effects_settled: null`; a fresh
observation alone is not proof of completion. Python callers can import
`read_state`, `act`, and `play_sequence` from `bridge.py` in a checkout. MCP is
deferred; the CLI and skill are the supported agent interface for now.

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

New DLLs advertise `command_guards: ["expected_state_id"]`. When supplied, that
field must match the published observation and its gameplay context must still
match the live game on the main thread immediately before execution. Guarded
commands from an earlier game process are rejected. Combat diagnostic labels
(including floating damage text) can update without changing `state_id`; other
gameplay-context changes invalidate it. This checks **observable** state, not
hidden RNG or a complete engine snapshot. Sequences refuse older DLLs without the
guard capability; ordinary unguarded commands remain compatible.

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

## Native Linux build

Install a .NET 9 **SDK** (the runtime alone cannot compile the mod). With mise:

```sh
mise install dotnet@9
mise exec dotnet@9 -- ./build-and-deploy.sh build
```

If the SDK is already on `PATH`, run `./build-and-deploy.sh build` directly.
The default action is also `build`; it never installs or starts the game.

The script reads `sts2.dll` and `0Harmony.dll` directly from the installed game's
`data_sts2_linuxbsd_x86_64` directory. Old repository-root DLLs are not used on
Linux. The Godot SDK supplies the C# bindings and source generator; no Godot
editor or PCK export is needed. The package contains only `sts2-bridge.dll` and
`sts2-bridge.json` in `dist/local/`.

The default game directory is
`~/.local/share/Steam/steamapps/common/Slay the Spire 2`. For another library,
append `--game-dir "/path/to/Slay the Spire 2"` to any command. Direct MSBuild
users can pass `-p:GameDataDir="/path/to/data_sts2_linuxbsd_x86_64"`.

## Windows build

With a .NET 9 SDK and the game installed, in PowerShell:

```powershell
.\build-and-deploy.ps1 build
.\build-and-deploy.ps1 install  # Game must be closed
# For a different Steam library, add -GameDir 'D:\SteamLibrary\steamapps\common\Slay the Spire 2'
```

The `.cmd` wrapper forwards the same arguments. Build and install are separate;
no Godot editor, PCK export, automatic game launch, or repository-root game DLL
copies are needed. This script has not yet been exercised on native Windows.

## Release builds and GitHub Actions

Both local compilation/upload and hosted CI are possible. Alchyr's
[mod template](https://github.com/Alchyr/ModTemplate-StS2) builds against an installed
game; [BaseLib](https://github.com/Alchyr/BaseLib-StS2/releases) distributes mod
assets on GitHub. This project's CI instead compiles against pinned
[Book.StS2.RefLib 0.107.1](https://www.nuget.org/packages/Book.StS2.RefLib/0.107.1).
Its publisher states Mega Crit permits reference use. It contains signature-only
`sts2`, Harmony, and Godot assemblies; only compile assets are enabled, not runtime
assets or its access-check-generating build targets. No Steam login is needed.

Run the same packaging step locally with Python 3 and a .NET 9 SDK on `PATH`:

```sh
python3 release.py --tag v0.1.0
# Or: mise exec dotnet@9 -- python3 release.py --tag v0.1.0
```

This validates that the supplied tag, project version, and manifest version agree,
rebuilds against the references, and creates `dist/release/` with the DLL, manifest,
versioned ZIP, MIT `LICENSE`, and `SHA256SUMS`. It neither creates a tag nor installs/uploads files.
The ZIP contains `sts2-bridge/sts2-bridge.dll`, its JSON manifest, and the license.
Never upload the whole build tree: it can contain dependencies and test fixtures.

The workflow runs unit/packaging tests and builds artifacts on pushes and PRs.
A pushed `v*` tag additionally creates a **draft prerelease**, for a maintainer to
review and publish. PR builds have no release-write permissions. There are no
self-hosted runners, game downloads, Workshop uploads, or custom secrets.
Reference compilation currently emits a Godot-generated `Main` name-conflict
warning (CS0436); do not confuse a successful build with game compatibility.

Before publishing source/history, review it for private data and third-party
content. The inherited Godot icons are attributed above; the bridge's selected
source license is MIT. Preserve its notice with binary distributions too.
For each release, update versions, run installed-game fixtures, test the actual
packaged DLL using `tests/smoke.py --package-dir dist/release`, then commit/tag the
reviewed revision. Review the draft and its compatibility notes before publishing.
CI cannot run the game from reference stubs. A game update needs fresh runtime
tests even when compilation still passes.

## Repeatable smoke test

Requires Python 3 and bubblewrap (`bwrap`), in addition to the installed game:

```sh
./build-and-deploy.sh smoke
```

This tests the existing package; rebuild first after changing C# or the manifest.
It runs the real game headlessly twice: once to generate fresh settings, then
with mod loading enabled in that disposable account. It asserts:

- The DLL initializes and all Harmony patches apply.
- Startup reaches the main menu and exports a protocol-v1 `state.json`.
- An unsupported `smoke_probe` command receives the matching error result and
  its command file is consumed.
- The marker CLI reaches the game, and the session contains ordered snapshots,
  command results, markers, and a clean exit entry.

The test mounts game files read-only, exposes no real home directory or Steam
sockets, disables networking and Steam initialization, and uses temporary save
data. No game install, real save, or Steam Cloud data is changed. Other installed
mods are excluded. There is no unsandboxed fallback.

Before patching, the mod loads `libgcc_s.so.1` with global symbol visibility on
Linux for the installed Harmony/MonoMod native helper. Neither Steam launch
options nor test-only `LD_PRELOAD` are required. The headless game also emits
FMOD and dummy-renderer/exit warnings, including without the mod; those are
retained in the logs. Managed errors and bridge startup/export/command failures
fail the test.

Logs, state, command results, and recordings are retained under
`dist/smoke/run-*/`; temporary save data is deleted.

## Repeatable mystery-room tests

```sh
mise exec dotnet@9 -- ./build-and-deploy.sh events
python3 -m unittest discover -s tests -p 'test_*.py' -v
```

`events` tests the existing package. It builds a **separate test-only DLL** and
runs eighteen paths in fresh, seeded, unsaved Ironclad runs inside the same offline
sandbox. It uses the game's debug room entry followed by ordinary bridge actions;
it does not use AutoSlay or replace gameplay resolution:

- Neow → forced Scroll Boxes / Neow's Torment / Silken Tress offers → verify
  Fury, Exhaust, Glam, and Replay explanations → choose Torment → verify the
  obtained card and owned relic → map. Repeated reads must not change previews
  or player/deck state. Only the test fixture forces these offers.
- Morphic Grove → Loner → +5 max HP → map.
- Wellspring → Bottle → potion reward → finished event → map.
- Wellspring → Bathe → deck removal → curse → finished event → map.
- Battleworn Dummy → three end turns → finished parent event → map.
- Round Tea Party → Pick a Fight → Continue → lose 11 HP, gain one relic → map.
  Checks resolved effect text and an empty continuation description, not a raw
  localization diagnostic.
- Architect → second-visit Threaten → Continue → Proceed choice. Checks empty
  choice descriptions and native dialogue transitions; does not dismiss results.
- Crystal Sphere → custom minigame → explicit `unsupported` state and safe command
  rejection. **Minigame automation is not implemented**; manual play still works.
- Aroma of Chaos → upgrade choices → compare native View Upgrades previews →
  upgrade Pommel Strike → finished event → map. The fixture adds a large deck,
  modified/duplicate cards, and non-upgradable cards to test preview coverage.
- The Trial → two-card transform → range removal, multi-upgrade, enchantment,
  and simple selectors. Checks partial-selection updates, toggles, bounds,
  preview/confirmation boundaries, map blocking, and exact deck effects.
- Merchant → resolved card/relic/potion effects → purchase a modified Headbutt →
  map → rest site. Checks numeric energy gain, keywords, prices, repeated-read stability,
  unchanged player RNG/card registration, singular/plural Smith text, and Regal
  Pillow's extra heal text against an actual rest (31 → 70 HP).
- Fake Merchant → six native fake relic offers → buy Lee's Waffle → close/reopen
  → map, plus a separate no-purchase path. Checks actual prices and fake effects,
  exact payment and healing (31 → 39 HP), one award, stable offers/RNG, and rejection
  of stale, unaffordable, closed, blocked, and map-covered shop actions. Uses the
  same `merchant` snapshot and commands as an ordinary shop; `run.event_id` stays
  `FAKE_MERCHANT`. The Foul Potion combat branch is not covered by these tests.
- Treasure → open → take Blood Vial → map, and a separate Skip path. Checks
  native descriptions, hidden initialized/uninitialized reward holders, stable
  reads, exactly one award, and rejection of repeat opening/selection.
- Silver Crucible → genuinely empty treasure chest → map. Verifies that no
  phantom relic appears and neither relics nor gold are awarded.
- Rest → map → ordinary monster, and final campfire → map → Act 1 boss. Checks
  the complete graph against the native map, including start/boss points, native
  button eligibility, exactly one normal map vote, and actual combat entry.
  Closed/view-only maps and invalid, current, nonadjacent, and stale destinations
  must reject travel without changing the run.

The fixture DLL refuses to run outside the offline `/test/` user-data directory.
The normal installer never installs it. Each fixture exports the installed game's
event catalog and retains its session for diagnosis. This is representative
transition coverage, not exhaustive coverage of every event or character.

Run one case (after building fixtures), or verify recording failure isolation:

```sh
python3 tests/smoke.py \
  --game-dir "$HOME/.local/share/Steam/steamapps/common/Slay the Spire 2" \
  --package-dir dist/local --fixture-dir dist/fixtures --case morphic-loner
# Add --recording-failure to block recording storage in the sandbox.
# The event must still complete and mark must report that it was not saved.
```

## Combat context and previews

```sh
mise exec dotnet@9 -- ./build-and-deploy.sh build
mise exec dotnet@9 -- ./build-and-deploy.sh combat
```

The combat fixture uses the same offline sandbox. It checks upgraded/duplicate
cards, generated cards, draw/exhaust/reshuffle identity, actual plays versus
end-turn discards, history reset between combats, resource eligibility, visible
powers, relic counters, and calculated intents. It compares card previews with
real damage/block under Strength, Weak, Vulnerable, Dexterity and Frail, and checks
Ornamental Fan before/after its third-attack trigger. Synthetic multi-hit and
hidden intents test export shape; those synthetic moves are never executed.
The fixture then wins through an ordinary card play, collects other loot before
the last card reward, and verifies that the completed rewards screen still
exports `scene: rewards`, `waiting_for_input: true`, and an empty reward list with
`proceed_enabled: true`. The ordinary `proceed` command must reach the map; it
must not operate on a covered or already-dismissed rewards screen.

Snapshots now include:

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
  only with `scene: combat` and `waiting_for_input: true`. In selectors the legacy
  `playable` field means selectable, not playable in combat.

**These are observations and card-variable previews, not a turn simulator.**
Preview decimals may be rounded by actual resolution. They do not encompass all
after-play effects, generated cards, random outcomes, or effects of earlier
hypothetical actions. For example, Ornamental Fan's third attack grants 4
unpowered block after play; it is not part of that attack's card variables and
Dexterity/Frail do not modify it. A future planner needs verified trigger rules
or isolated engine rollouts, explicit incomplete-coverage reporting, and must
avoid double-counting relic modifiers already included in card previews.

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
The offline `potion-rewards` case (included in `./build-and-deploy.sh combat`)
checks both paths, duplicate/same-slot replacement, native skip restrictions,
unchanged RNG on reads, belt expansion, discard versus use effects, and terminal
reward departure/history. Multiplayer remains unverified.

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

Relic reward rows now include the offered instance's formatted `description`,
`model_id`, `rarity`, `base_values`, and `hover_tips` **before pickup**. The row's
`id` remains the action ID for `take_reward`. Reads do not populate/reroll rewards,
award relics, or execute alternative effects.

The offline `card-rewards` case (included in `./build-and-deploy.sh combat`)
checks skip/reopen, required offers, native reroll, Pael's Wing sacrifice,
multiple card rewards, stale/disabled/hidden/covered buttons, ordinary card
selection, terminal map departure/history, and pre-pickup relic mechanics
(including a modified instance and a randomly populated relic). It verifies
unchanged player RNG, inventory, and history during repeated preview reads.
An offer containing Headbutt, before any combat, verifies that reward prompts
come from the active screen rather than an offered card's on-play selection text.
This is singleplayer coverage; multiplayer remains unverified.

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
- `selected_card_ids`: the current selected set. The legacy `playable` field
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

`./build-and-deploy.sh combat` also runs a separate pile-selection fixture:
Hemokinesis → Fury → recover/replay Hemokinesis for lethal; optional zero/partial
choices, toggles and limits; Hologram's mandatory discard choice; Secret Technique
and Secret Weapon's filtered draw choices; and a native exhaust-pile selector
whose selected card is removed while open. It checks actual card movement,
identity, completion, invalid commands, and draw-order privacy. The exhaust case
tests the native UI contract directly, not a particular card's effect.

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

The `combat-hand-selection` fixture runs Thinking Ahead (including redrawing the
top-decked card), True Grit+, Survivor, and Armaments. It also checks multi-card
bounds, toggles, zero/partial confirmation, filtered-out and stale IDs, map/peek
blocking, and native deselection when a selected card no longer passes its filter.
It runs under `./build-and-deploy.sh combat`, without accessing real saves.

## Deck selections and confirmation

`deck_transform`, `deck_upgrade`, `deck_enchant`, and `deck_card_select` export
the prompt, selection bounds, `selected_card_ids`, and `confirm_available`.
Every partial selection changes the snapshot, even before the minimum is met.
`select_card` toggles an eligible card; `cards[].playable` means clickable.

**Deck selections require explicit `confirm_card_selection`.** Reaching the
maximum opens the native preview; it does not apply the effect. Cards cannot
be clicked through that preview. A range selection can require two confirmations:
Continue from the grid, then Confirm in the preview. Inspect the resulting state
after each command, and confirm only while `confirm_available` is true. The
bridge no longer forces completion after a click on an upgrade/removal screen.

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

The `events` suite checks damage/draw/cost changes, True Grit's random-to-chosen
exhaust text, instance modifiers, repeated-read stability, native checkbox
parity, and exactly one real upgrade after selecting and confirming the original
card ID.

## Relevant mechanics in the observed state

`hover_tips` accompanies event choices, cards (deck, combat piles, and selectors),
owned relics, offered relics in selection/treasure screens, owned potions, and
merchant items.
It exports the **installed game's own tooltip list**, with resolved descriptions
and original rich-text markup. No wiki, tier list, or decision runtime is needed.
For example, Neow's Torment now includes Neow's Fury's 1-energy cost, 10 damage,
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

This is a baseline lookup attached to relevant observations, **not a complete
mechanics database or effect simulator**. Potion reward rows include a nested
mechanics preview; other unopened reward types do not yet include this field.
Missing game tooltips still need recording and
targeted fixes. Ancient dialogue remains observable in `screen.visible_controls`;
an absent event description is empty instead of a nonexistent localization key.

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
The offline cases verify ordinary travel and the Act 1 boss; second-boss and
multiplayer transitions are not yet playtested.

## Install for normal play

Close the game, then explicitly install the package you built:

```sh
./build-and-deploy.sh install
```

This copies only the two package files into `<game>/mods/sts2-bridge/`. Other mods
are left alone. Installation refuses while `mods/FirstMod` exists, to prevent
loading two bridge implementations. Enable mods in the game and restart if prompted.

During normal play, bridge files live in Godot's `user://sts2-bridge`
directory (normally `~/.local/share/SlayTheSpire2/sts2-bridge` on Linux):
`state.json`, `command.json`, `command-result.json`, and `latest-session.json`. Write
commands to a temporary file and rename it to `command.json`, using a unique
`command_id`; wait for the matching result before submitting another command.

## Record a playtest

Recording starts automatically when the mod loads; play normally. No AI runtime
or separate capture process is needed. From this repository:

```sh
python3 recording.py status
python3 recording.py mark "Mystery room: had to select this card manually"
```

`status` summarizes saved observations, unsupported screens, error counts, and
notes; it is **not** a liveness check. A successful `mark` confirms that the
running mod saved the note. Use `--bridge-dir PATH` before the subcommand to
inspect another data directory, including a retained `dist/smoke/run-…` folder.
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
documented above; full action-outcome prediction is not implemented. Recordings stay local
and persist across launches; there is no automatic deletion. Remove old session
folders when no longer needed. If recording storage fails, gameplay continues,
the game log reports the failure, and `mark` returns an error. The old `trace.log`
is no longer written.
