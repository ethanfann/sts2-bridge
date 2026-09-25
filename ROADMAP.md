# Milestones and swappable rulesets

Recorded 2026-09-21; updated 2026-09-25. **Phase 0 achieved and exceeded:**
the shared CLI has completed an Ironclad A1 clear through all three acts on
native Linux v0.107.1, reaching victory and unlocking A2. Every chosen event
branch worked without a manual workaround.
**Current release: experimental v0.1.1 with Crystal Sphere and manual run uploads.**

This is a staged plan, not a request to implement every layer now. It supersedes
the milestone ordering in the earlier [architecture proposal](sts2-monorepo-architecture.md).
The [README](README.md) describes implemented behavior and test commands; planned
capabilities below must not be mistaken for features that already exist.

## Starting point

- STS2 Bridge loads in the native Linux game and records bridge observations,
  commands/results, and selected game events locally.
- Exported context includes the permanent deck, combat piles, completed plays,
  visible powers/relics, announced intents, and game-calculated card previews.
  Startup, representative events, and combat context have sandbox fixtures.
- Relevant mechanics start with game-supplied `hover_tips` on event choices,
  cards, relics, and owned potions. Referenced cards and keyword/enchantment
  explanations are included without exposing hidden offers. Neow has a forced
  offer fixture; shop context and potion reward previews also have fixtures.
  This is not a complete mechanics catalog.
- These previews are not a simulator. After-play triggers and hypothetical
  sequences require separate verification. Observed state IDs and completed-play
  hooks do not prove every asynchronous effect has settled.
- Repeatable unattended full-run reliability is not established. Menus,
  run start/results, and unsupported custom screens still require human input.
  Crystal Sphere now has semantic controls, with the visual-clue limitation below.
  Recordings are diagnostic evidence, not complete deterministic replays.
- Jev, swappable runtime rulesets, capability scoring, tier catalogs, retrieval,
  and simulation are not implemented. STS2MCP remains a separate comparison,
  not a migration requirement.

### Implemented bridge fix — Crystal Sphere

The 2026-09-23 Ironclad A10 run on v0.107.1 required manual completion of
`NCrystalSphereScreen` in Act 2. Ethan completed the minigame and returned control
at its supported rewards screen. That gap led to the v0.1.1 adapter;
[investigation and proposed approach](https://ampcode.com/threads/T-01a0cfa3-00c4-743d-af15-2fa552cbf20a).

- Exports stable tool/cell IDs, grid coordinates, remaining divinations, input
  eligibility, and fully uncovered items after their reveal animation.
- Selects tools and reveals cells through native callbacks, preserving payments,
  rewards, busy-state gating, stale-state guards, and active-screen precedence.
  Handles both the rewards screen and the minigame's final Proceed back to the map.
- Offline fixtures cover both payment branches, partial/full reveals, invalid and
  repeated actions, exhausted divinations, covering overlays, and hidden-information
  isolation. They use disposable saves, not the live A10 run.
- **Remaining limitation:** partial artwork is not classified or exported as an
  image. The agent therefore lacks some visual clues a human can use. Covered and
  partially uncovered item identities and locations must remain private.

`codex.py` also supports optional **manually invoked** Spire Codex uploads. It
does not watch run history, install a timer, or upload when a run ends.

## Phases add capabilities; rulesets choose how to play

A **phase** is a development milestone with an observable completion test.
A **ruleset** is a versioned play policy: objectives, priorities, tie-breaking,
fallbacks, and which decision aids it may use. It is not a new bridge or mod.

A **decision backend** executes that policy: initially a human or the assisting
agent, later deterministic code, Jev, or a combination. Switching backends is
separate from changing the ruleset. Record both to avoid conflating their effects.

Strong play is a goal, not a claim of state-of-the-art performance. Keep simple
baselines available, and retain complexity only when measured results justify it.

## Phase 0 — Simple play through Act 1

**Goal:** complete Act 1 as Ironclad, Ascension 0, using ordinary good decisions
and the current bridge. No new AI runtime is a prerequisite.

- Pick broadly useful cards; make sensible reward, route, shop, rest, and potion
  choices. These can be direct human/agent judgments rather than encoded scores.
- In combat, use visible effects and calculated previews, take available lethal,
  avoid preventable death, and spend resources sensibly. Do not turn "block when
  threatened" into a rigid rule that ignores killing the attacker.
- Fill observed mechanics gaps with version-correct game descriptions, including
  referenced cards, enchantments, and keywords (for example Neow's Fury and Glam).
  Keep factual rules separate from tier opinions and tactical advice; a complete
  knowledge/retrieval service is not a prerequisite for fixing a missing lookup.
- Execute one action at a time, inspect the resulting state, and pause on unclear
  effects or unsupported interactions. Do not blindly retry ambiguous commands.
- Record the intended choice and actual result where possible. Classify problems
  as missing context, action/transition failure, or decision error. Turn repeatable
  bridge failures into isolated fixtures instead of rediscovering them in runs.
- Turn decision mistakes into regression cases too: preserve the original state,
  choice, and observed consequence; test a proposed correction and a counterexample
  where applying it would be harmful. Do not promote an untested postrun lesson
  directly into the active ruleset.

**Exit evidence:** a recorded Act 1 clear, with bridge-controlled versus manual
steps identified and known blockers documented. An assisted clear is a gameplay
milestone, not evidence of unattended reliability; one clear is not a win rate.

**Not required:** tier-list scraping, archetype classification, Jev integration,
vector search, simulation, or a repository restructure.

## Next bridge milestone — an ergonomic controller client

`bridge.py` now provides dependency-free `observe`, `act`, and `play-sequence`
commands plus importable client functions. Recording markers and fixture actions
share its no-overwrite, correlated, single-writer transport. Sequences use native
completed-play history, input readiness, and an optional DLL-side observed-state
guard; they stop at selectors, changed hands, scene/turn transitions, or failures.
They are not atomic mod transactions. The installable `playing-sts2` skill bundles
the shared CLI and uses its `--help` as the command reference. The skill and CLI
are sufficient for now; MCP is deferred until there is a concrete need.

- Exercise the shared client for observations, atomic command submission,
  single-writer ownership, result correlation, and bounded waits in live runs.
  Keep policy separate. Do not treat a changed state ID or command acknowledgement
  as proof of settled effects; record false stops and unsupported continuations.
- Exercise skill installation without a source checkout and from unrelated
  working directories. Keep the installed skill self-contained and distinguish
  its scripts from the mod install and runtime state/command directories.
- Provide focused, deterministic views of combat, choices, deck, and mechanics,
  with access to the raw snapshot. Preserve IDs, action eligibility, source state,
  and unsupported/unknown fields; avoid an LLM summary hiding material mechanics.
- Return command results with relevant resulting observations. A bounded action
  sequence can stop on rejection, a selector, scene transition, or unexpected
  state; do not silently continue past reveals that require a fresh decision.

**Exit evidence:** the same client drives offline fixtures and a supervised live
segment. Tests cover stale acknowledgements, pending-command contention, timeouts,
reloads, and interrupted sequences without duplicate actions. The adapter does
not need a new decision runtime or a replacement bridge protocol to begin.

## Phase 1 — Recognize useful combinations when building the deck

**Goal:** reliably recognize a documented set of supported interactions and pick
cards/relics that improve the current deck, without forcing an archetype.

- Recompute a capability profile from the actual deck after additions, removals,
  upgrades, transformations, and reloads. Keep it separate from temporary combat
  state. Cards may serve multiple roles.
- Track damage, block, draw, energy, scaling, and relevant status effects; keep
  counts, magnitudes, costs, conditions, and reliability distinct. Attack card
  type is not the same thing as damage capability.
- Add a small reviewed, version-pinned tier catalog as baseline guidance. Store
  source, rationale, upgrade assumptions, and patch compatibility. An overall
  card tier is not a per-role magnitude or a number to sum into deck strength.
- Represent enablers and payoffs: reliable exhaust makes an exhaust payoff useful;
  an existing payoff makes a missing enabler valuable. Account for redundant
  copies, setup time, affordability, and the cost of diluting the deck.
- Compare the marginal benefit of each choice, including skip where legal.
  Keep immediate survival distinct from eventual combo strength. A partial combo
  is an opportunity, not a commitment; no universal block/attack/draw ratio.
- Give relic decisions their actual trigger conditions, benefits, and downsides.
  Annotate unsupported effects rather than treating them as zero benefit.
- Add small condition-triggered playbooks for supported situations: encounter
  mechanics, near-term threats, or a specific deck interaction. Each has an
  explicit trigger, scope, exceptions, provenance, and regression evidence.
  Retrieve only relevant advice; avoid universal "spend all energy" or "always
  protect HP" rules. Playbooks are optional strategy inputs, not game facts.

**Exit evidence:** fixed decision cases cover an active combo, a missing enabler,
an unsupported combo, an unaffordable high-tier card, redundant payoff, and skip.
Compare tier-only and synergy-aware choices under the same conditions. Demonstrate
the supported behavior and report run results without claiming universal combo
recognition or improved win rate from a handful of examples. Playbook tests must
exercise both activation and non-activation, including their exceptions.

## Phase 2 — Repeatable runs with interchangeable policies

**Goal:** run the ordinary Ascension 0 loop repeatedly with selectable rulesets;
target a full-run clear while measuring how often the controller needs help.

- Build the smallest external controller needed to read observations, construct
  currently valid candidates, choose, submit once, and observe the result.
  Policy selection must not require changing or rebuilding the game bridge.
- Introduce Jev for narrow judgments where useful. Keep arithmetic, eligibility,
  command handling, and game side effects in code. Freeze model/instruction
  versions for comparisons where the provider permits it.
- Assemble fresh, bounded decision packets from current observations, relevant
  mechanics, and explicitly enabled strategy aids. Do not append an ever-growing
  run transcript. Keep a compact, revisable run summary of the current engine,
  weakness, next known threat, and resource intentions; attach turn/combat/run
  scope to notes and expire or reconcile them when observations change.
- Verify decision-boundary readiness and recovery from rejects, timeouts,
  scene changes, and nested selectors. Command acknowledgement alone is not
  proof that all effects have resolved.
- Keep menus, victory/loss results, epochs, run start/resume, and unsupported
  screens human-assisted for beta. Results dismissal is intentionally outside
  the release scope. Report interventions rather than calling a run fully
  autonomous. Expand room coverage using fixtures as observed failures warrant.

**Exit evidence:** multiple seeded attempts, a recorded full-run clear as the
gameplay target, and explicit intervention/error counts. Switching the drafting
ruleset preserves the same combat policy and bridge behavior in comparison tests.
Unknown or stale choices pause safely; no duplicate submission after a timeout.

## Phase 3 — Bounded tactical lookahead

**Goal:** compare short action sequences by their predicted consequences, not
enumerate every combination or simulate the entire run.

- Start in shadow mode: predict a supported action, let it happen in the real
  game, and compare the prediction with the observed result. Expand mechanics
  coverage only after mismatches are understood.
- Prefer verified game calculations or isolated engine execution where feasible.
  Safe combat cloning has not been established; never mutate the live run to
  explore hypothetical actions or assume frozen previews compose correctly.
- Start with roughly 2–3 card plays and a hard node/time budget. Resolve relevant
  triggers and nested selections; include end turn and the modeled enemy response
  when comparing survival. Make the evaluation horizon explicit.
- Prioritize promising actions and use a position evaluator at the search
  boundary, instead of expanding every branch equally. Begin with inspectable
  heuristics; compare more selective search only after simulator correctness.
- Distinguish chosen cards from random outcomes. Branch or sample chance events
  using only player-visible information, never hidden draw order or RNG state.
  Replan after a reveal rather than commit to a sequence based on unseen cards.
  Hypothetical decisions must depend on observations available at that point,
  not on the hidden state sampled inside a simulation. Track poor-outcome risk
  as well as average value; model confidence is not a survival probability.
- Preserve exact, estimated, unsupported, and budget-truncated outcomes as
  different states. Keep simulation uncertainty separate from Jev confidence.
- Rank a bounded shortlist, execute only its first action, observe, and replan.
  Correctly model relic triggers without double-counting existing modifiers.

**Exit evidence:** independent fixtures expose ordering effects, trigger timing,
retrieval choices, randomness, and loops. Predictions match actual resolution for
the declared supported mechanics; compute remains bounded, and unknown branches
are not mislabeled safe. Compare against the no-lookahead combat baseline.

### First simulation implementation spike — divine-sts2

Start with [divine-sts2](https://github.com/favet/divine-sts2) before writing a
parallel implementation of game mechanics. Source inspection found resident
snapshot restoration with hash checks and a reset-plus-action-replay fallback in
[PersistentNativeCombatEnvironment](https://github.com/favet/divine-sts2/blob/main/src/Sts2.NativeSim.Core/PersistentNativeCombatEnvironment.cs).
This makes it a candidate, not a proven drop-in simulator: arbitrary import of
our live bridge state, complete restoration, and game parity remain unverified.

The spike should answer one question: **can an isolated worker branch and restore
a representative combat while matching the installed game's resolution?**

1. Pin the upstream revision; check its license, Linux/.NET requirements, and
   compatibility with our installed v0.107.1 assemblies. Audit gameplay-affecting
   patches and omissions. Do not upgrade the live game to make the spike pass.
2. Build/run only in a disposable offline environment using locally owned game
   assemblies, without normal saves, Steam IPC, or changes to the installed mod.
   No Jev integration, bridge replacement, or general search framework yet.
3. Reproduce a small deterministic combat from our existing fixtures. Compare
   settled HP, block, energy, powers, pile membership, and relic effects; include
   modifier-sensitive damage/block and Ornamental Fan's third-attack trigger.
   Map card identities between processes rather than compare opaque IDs directly.
4. Branch A, restore the root, branch B, restore, and repeat A. Verify both root
   state and subsequent effects/draws; a matching observation hash alone does not
   prove hidden state or continuations were restored. Check branch isolation,
   RNG restoration, and explicit failure on divergence.
5. Separately document the initialization contract: fixture reset, replay from a
   checkpoint, or genuine current-combat import. Inventory required state beyond
   our observation export; never fill unknown internals with silent defaults.
   Keep sampled hidden state out of policy-visible inputs.
6. Measure restore/step latency and supported coverage. Return an adopt/adapt/defer
   decision with exact revisions, commands, parity results, and blockers. If the
   fixture works but live-state import does not, claim only a fixture backend.

Success permits a narrow adapter and shadow predictions for the verified subset;
it does not establish full-game equivalence. This is the preferred starting point
when simulation work begins, not an additional gate before the Phase 0 playtest.

## Phase 4 — Retrieve comparable historical decisions

**Goal:** use prior play as evidence for choices, especially rewards and relics,
without assuming that a winner's every choice was correct.

- Index decision points: information available before the choice, offered options,
  selected action, and later outcomes. Do not embed only whole-run summaries.
- Verify data completeness and reuse permissions first. Run histories can support
  some between-room decisions; combat imitation needs sufficiently complete
  pre-action states and action sequences, not just final decks or event journals.
- Filter by game compatibility, character, decision type, difficulty, and mode.
  Combine numeric/card-set similarity with semantic retrieval of effects and
  interactions. Embeddings are one tool, not a requirement for every field.
- Return matching features, important differences, source, support counts, and
  outcomes. Compare successful examples with relevant failures and cases where
  the alternatives were actually offered. Popularity is not causal evidence.
- Keep raw examples, compact encounter summaries, and triggered playbooks distinct;
  compare their usefulness rather than assume vector similarity is best. Treat
  AI-generated trajectories as policy observations, not expert or optimal labels.
- Prevent hindsight and evaluation leakage: future decks/draws are not inputs;
  held-out evaluation runs and their continuations cannot enter retrieval.

**Exit evidence:** audit retrieved examples for relevance and information leakage;
compare retrieval on/off with the same policy/backend and report coverage and
performance. Do not depend on a third-party feed until availability is verified.

This phase can begin with reward choices before simulation is mature; it is not
a prerequisite for Phase 0 or a strictly sequential dependency on Phase 3.

## Phase 5 — Combine the useful pieces and climb Ascension

**Goal:** progressively stronger, measured play using the components that earned
their cost, rather than assuming the most complex configuration is best.

- Combine tactical search, capability/synergy assessment, and retrieval. Extend
  strategic decisions to route risk, elites, shops, upgrades, and potion timing.
- Allocate more search to difficult decisions; improve chance-outcome evaluation
  and longer-horizon position scoring. Retain uncertainty and human fallback.
- If supported by enough verified trajectories, consider a learned action
  predictor to prioritize branches or a position evaluator to stop search early.
  Fine-tuning an LLM is optional research, not a prerequisite. Jev currently has
  no customer fine-tuning; a learned component would be separate.
- Raise Ascension and expand characters deliberately, keeping benchmarks separated
  by difficulty, character, and game version. Revalidate after balance patches.

**Exit evidence:** held-out run batches at each target difficulty, failure review,
and one-component-at-a-time comparisons. Describe the strongest measured setup;
do not claim state-of-the-art play without a credible external comparison.

## Ruleset contract and initial experiment matrix

This is a design constraint for the future controller, not an implemented API or
a reason to build a plugin framework now. For Phase 0, record the policy manually.

- **Shared input:** observed game state and currently valid decision candidates.
  Capability summaries, tier knowledge, playbooks, strategic notes, retrieved
  examples, and simulations are explicit optional inputs, enabled only for the
  policy components allowed to use them. A combat playbook must not silently
  change a tier-only drafting policy.
- **Output:** one candidate or an explicit request to pause, with a short reason
  and the evidence used. A policy never executes game actions directly.
- **Shared execution:** eligibility checks, one command writer, command/result
  correlation, readiness checks, recording, and human handoff are independent
  of strategy. Even a deliberately simplistic ruleset cannot bypass them.
- **Versioned identity:** freeze policy settings and instruction text, backend,
  tier/annotation/playbook/retrieval versions, and search budget for a run. Log
  overrides and mid-run changes; do not silently fall back to a stronger strategy.
  Proposed lessons stay outside frozen evaluation stores until reviewed and tested.
- **Independent choices:** drafting/relic policy, combat policy, and route/shop
  policy can vary independently. Keep unchanged components fixed in experiments.

| Proposed ruleset | Card/relic choices | Combat | Decision aids |
| --- | --- | --- | --- |
| `simple-baseline` | Broadly useful picks by human/agent judgment | Immediate tactical judgment | Current observations/previews |
| `tier-only` | Highest static tier; no combo or deck-fit adjustments | Same simple combat baseline | Pinned tier catalog |
| `synergy-aware` | Baseline tiers adjusted for supported interactions and needs | Same simple combat baseline | Capability profile and effect annotations |
| `lookahead` | Same synergy-aware drafting | Bounded action search | Verified simulation coverage |
| `history-assisted` | Historical comparisons added to a named baseline | Hold the chosen combat baseline fixed | Compatible decision retrieval |
| `combined` | Best measured composition | Best measured composition | Only aids shown useful in evaluation |

For the first `tier-only` card-reward experiment: choose the highest-ranked legal
card offered (S before A, and so on); break ties by offered order; do not skip
while any rated card is available. Unrated cards are not eligible for this ranking;
pause if all are unrated. Record the omissions. Add a separate tier catalog before
extending this behavior to relics. This intentionally ignores combo value and
deck dilution: it represents a real, testable play style, not the recommended
universal strategy. Do not secretly add synergy bonuses to improve its results.

## Evidence required across phases

- Record game/build, mod revision or build hash, character, Ascension, seed,
  ruleset/backend identity, content versions, and relevant computation limits.
  Some of this metadata needs future controller support; existing recordings
  should not be assumed to contain it all.
- Report Act 1/full-run clears with attempt counts, furthest progress, deaths,
  unsupported screens, manual interventions, rejected/ambiguous actions,
  prediction errors where applicable, and decision latency/cost.
- Keep assisted and unassisted segments/results distinct. A game loss is not a
  bridge failure; a bridge stall must not be hidden as poor strategy.
- Use paired seed suites, fixed fixtures, and held-out seeds for comparisons.
  Different actions can consume RNG differently, so the same seed does not
  guarantee identical later encounters or draws. These recordings alone cannot
  replay arbitrary counterfactual actions.
- Compare one changed component at a time, retain losses, and report uncertainty
  with small samples. Evaluate survival as well as speed; distinguish known
  mechanics from approximations and unsupported cases.

## Research leads, not dependencies

Checked during the 2026-09-21 discussion; recheck compatibility and availability
before using them. No bulk dataset ingestion is part of the current milestone.

- [TypeSafe's Jev guidance](https://docs.typesafe.ai/concepts/how-to-build-with-system-one):
  narrow structured judgments, with deterministic work and control flow in code.
- [AgenticSTS paper](https://arxiv.org/html/2607.02255v1) and
  [implementation](https://github.com/AlayaLab/AgenticSTS): fresh decision-specific
  context, triggered skills, and frozen evaluation stores are useful references.
  A0 Silent results were 4/10 wins with prompts alone versus 6/10 with skills;
  adding episodes also gave 6/10. These small-sample differences are not
  statistically decisive; transfer to another model sometimes hurt performance.
  Borrow the structure, not blanket strategy prose or claims of a proven champion.
  Its postrun judged-decision checks are not substitutes for outcome validation.
- [AgenticSTS trajectories](https://huggingface.co/datasets/AlayaLab/AgenticSTS-trajectories):
  the release describes 298 completed games plus 14 decision-capped records in its
  analysis superset, with some missing session logs. Available logs include
  decisions, states, and action results; most runs use older v0.103.x builds.
  These are AI-generated, primarily Silent data. Author trajectories are CC-BY-4.0;
  embedded competitor documents retain separate licenses. Audit the chosen subset
  and observation completeness before reuse; no corpus was ingested here.
- [Gumbel planning](https://openreview.net/forum?id=bERaNdoegnO): policy-guided
  allocation of small search budgets has evidence in other games, not STS2.
  Its learned-policy setting is not a guarantee for a Jev-ranked shortlist.
- [POMCP](https://papers.nips.cc/paper/4031-monte-carlo-planning-in-large-pomdps):
  sampling belief states and observation histories is a reference for hidden-draw
  planning. It requires a suitable generative simulator and belief model; neither
  this algorithm nor Gumbel planning is an immediate implementation requirement.
- [Spire Codex run export](https://spire-codex.com/api/exports/runs?limit=1):
  a one-record gzip JSONL download worked. The inspected record contains run/floor
  history, not per-action combat states. This is the working route; the published
  developer page advertised a different URL that returned 404.
- [Codex replay sample](https://github.com/ptrlrd/spire-codex/blob/main/backend/tests/fixtures/sample-replay.jsonl):
  separate combat event journals merit an audit, but neither complete state
  reconstruction nor a usable bulk replay corpus has been established.
- [Ironclad tier-list example](https://nat1gaming.com/sts2/tier-list/ironclad-card-tier-list):
  rankings include useful conditional explanations. Patch notes and older card
  descriptions on the page conflict, so do not import it as mechanical truth.

## Immediate next step

Prepare the experimental `sts2-bridge` v0.1.0 release: source/license review,
documented protocol and limits, repeatable DLL-only packaging, and tag-triggered
draft GitHub releases. No Workshop publication is planned. Next, improve controller
ergonomics with the shared client/CLI/MCP boundary above while continuing
fixture-driven reliability work. Do not delay bridge use for Phase 1–5 infrastructure.
When focused simulation work begins, use the divine-sts2 spike above.
