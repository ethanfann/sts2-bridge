# STS2 game rules

Scope: standard singleplayer, **game v0.107.1**, reviewed 2026-10-10. These are
mechanics, not a play policy. Other builds, custom modifiers, and multiplayer
need rechecking; do not substitute STS1 rules.

Read `game.version` in the selected bridge directory's `latest-session.json`
when available. This is recording metadata, not a liveness check. If the build
is missing or different, state that limitation and verify decision-relevant
rules against that build's game text or implementation.

## Ascension modifiers are cumulative

At Ascension N, every level from 1 through N is active. A0 has none of these
modifiers; A10 includes all ten, not just the second boss. [1, 2]

| Level | Modifier | Effect |
| --- | --- | --- |
| A1 | Swarming Elites | More elite rooms appear on the map. |
| A2 | Weary Traveler | Ancients restore 80% rather than 100% of missing HP. Includes Neow. |
| A3 | Poverty | Enemies and treasure chests drop 25% less gold. |
| A4 | Tight Belt | Start with one fewer potion slot. |
| A5 | Ascender's Bane | Start with Ascender's Bane in the deck. |
| A6 | Inflation | Merchant card removal starts at 100 rather than 75 gold; each subsequent removal adds 50 rather than 25. Other effects can change the price. |
| A7 | Scarcity | Rare and upgraded cards appear less often. |
| A8 | Tough Enemies | Enemies have more HP. |
| A9 | Deadly Enemies | Enemies deal more damage. |
| A10 | Double Boss | Fight two bosses at the end of Act 3. |

**Do not apply these modifiers twice.** Observed enemy HP/intents, shop prices,
potion capacity, and offered rewards already reflect the current game. Use the
actual values, not a multiplier applied again to the snapshot. A8/A9 are not a
universal percentage by which to scale every enemy. [1, 2]

## Ancient healing, not boss healing

Healing happens when a fresh **Ancient event starts**, not on boss defeat,
reward collection, or opening the next act's map. Survive remaining effects,
including Death Blow, first; reobserve HP after entering the Ancient. [3]

- A0–1: restore **100% of missing HP**.
- A2–10: restore **80% of missing HP, rounded down**.
- For integer current HP `h` and max HP `m`, with `0 < h <= m`, the A2+ result is
  `h + (4 * (m - h)) // 5`. This is not 80% of max HP.
- Neow initializes HP to zero before healing, so A2+ starts at 80% of max HP,
  rounded down, before the chosen boon's effects. This is not combat revival.
- There is no Ancient heal between A10's two Act 3 bosses. Other explicit
  healing effects are separate; check the actual route and effects. [1, 2]

## Resource lifetimes and resolution

These are defaults; apply the actual card, power, relic, and event exceptions.

- **HP, gold, permanent deck, relics, and unused potions** persist between rooms
  and acts unless an effect changes them. `CombatOnly` restricts potion use,
  not how long an unused potion lasts. [4]
- **Energy** normally resets to the current maximum at turn start. **Block**
  normally clears at the start of that creature's turn, not at End Turn.
  **Stars and orbs** persist between turns, but new combat state is initialized
  each fight. Reward/map snapshots can retain old combat values. [4]
- **Hand cleanup** normally discards unplayed cards; retain, ethereal, and other
  effects change this. Combat discard/exhaust is not permanent deck removal.
  Consult exported keywords and pile membership. [4]
- **Frost passive block** triggers at player turn end before ordinary enemy
  attacks. Use current orb values, queue order, and trigger modifiers. It is
  unpowered: Dexterity/Frail do not modify it. Other end-turn damage may have
  different timing; identify the trigger rather than assuming this order. [5]

## Sources and verification scope

1. Installed v0.107.1 `sts2.dll`: `AscensionLevel`, `AscensionManager.HasLevel`
   and `ApplyEffectsTo`, `AscensionHelper.PovertyAscensionGoldMultiplier`,
   `MerchantCardRemovalEntry`, and `RunManager.GenerateRooms`.
2. [STS2 Ascension reference](https://slaythespire.wiki.gg/wiki/Slay_the_Spire_2:Ascension)
   and [Weary Traveler](https://spire-codex.com/ascensions/level_02): secondary
   descriptions for the full table; later page updates do not extend build support.
3. Installed v0.107.1: `AncientEventModel.BeforeEventStarted`, `CreatureCmd.Heal`,
   `Creature.HealInternal` / `SetCurrentHpInternal`: healing trigger and rounding.
4. Installed v0.107.1: `CombatManager`, `PlayerCombatState`; bridge character
   fixtures and recorded runs cover resource persistence/reset.
5. Installed v0.107.1: `CombatManager.DoTurnEnd`, `OrbQueue.BeforeTurnEnd`,
   and `FrostOrb`: orb timing and unpowered block.
