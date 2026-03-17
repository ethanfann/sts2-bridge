# STS2 Hook Research

Goal: identify the best bridge points in `sts2.dll` for state export and semantic action execution.

Method used here:
- metadata inspection of `sts2.dll` with `dnfile`
- focus on `v0.99.1`
- emphasis on stable bridge hooks, not UI clicking first

## Big finding

There is already an internal auto-player namespace in the game:

- `MegaCrit.Sts2.Core.AutoSlay.AutoSlayer`
- room handlers for combat, event, rest site, shop, treasure
- screen handlers for map, rewards, card reward, etc.

That is strong evidence the game already has internal abstractions for autonomous play. We should treat `AutoSlay` as prior art, but prefer a simpler external bridge built on domain/state/action hooks.

## Best state entry points

### Run lifecycle
- `MegaCrit.Sts2.Core.Runs.RunManager.get_Instance`
- `MegaCrit.Sts2.Core.Runs.RunManager.get_State`
- `MegaCrit.Sts2.Core.Runs.RunManager.add_RunStarted`
- `MegaCrit.Sts2.Core.Runs.RunManager.add_RoomEntered`
- `MegaCrit.Sts2.Core.Runs.RunManager.add_RoomExited`
- `MegaCrit.Sts2.Core.Runs.RunManager.add_ActEntered`

Why:
- clean top-level run state
- room/act transitions are easy snapshot boundaries

### Combat lifecycle
- `MegaCrit.Sts2.Core.Combat.CombatManager.get_Instance`
- `MegaCrit.Sts2.Core.Combat.CombatManager.add_CombatSetUp`
- `MegaCrit.Sts2.Core.Combat.CombatManager.add_CombatEnded`
- `MegaCrit.Sts2.Core.Combat.CombatManager.add_TurnStarted`
- `MegaCrit.Sts2.Core.Combat.CombatManager.add_TurnEnded`
- `MegaCrit.Sts2.Core.Combat.CombatManager.add_PlayerEndedTurn`
- `MegaCrit.Sts2.Core.Combat.CombatManager.add_PlayerUnendedTurn`
- `MegaCrit.Sts2.Core.Combat.CombatManager.add_PlayerActionsDisabledChanged`
- `MegaCrit.Sts2.Core.Combat.CombatManager.WaitUntilQueueIsEmptyOrWaitingOnNonPlayerDrivenAction`

Why:
- this is the safest place to determine phase and command readiness

### Combat data
- `MegaCrit.Sts2.Core.Combat.CombatState.get_Players`
- `MegaCrit.Sts2.Core.Combat.CombatState.get_Enemies`
- `MegaCrit.Sts2.Core.Combat.CombatState.get_Creatures`
- `MegaCrit.Sts2.Core.Combat.CombatState.get_HittableEnemies`
- `MegaCrit.Sts2.Core.Combat.CombatState.get_RoundNumber`
- `MegaCrit.Sts2.Core.Combat.CombatState.get_CurrentSide`

### Player data
- `MegaCrit.Sts2.Core.Entities.Players.Player.get_Gold`
- `MegaCrit.Sts2.Core.Entities.Players.Player.get_Relics`
- `MegaCrit.Sts2.Core.Entities.Players.Player.get_Potions`
- `MegaCrit.Sts2.Core.Entities.Players.Player.get_Deck`
- `MegaCrit.Sts2.Core.Entities.Players.Player.get_PlayerCombatState`
- `MegaCrit.Sts2.Core.Entities.Players.Player.get_Creature`

### Combat player data
- `MegaCrit.Sts2.Core.Entities.Players.PlayerCombatState.get_Hand`
- `MegaCrit.Sts2.Core.Entities.Players.PlayerCombatState.get_DrawPile`
- `MegaCrit.Sts2.Core.Entities.Players.PlayerCombatState.get_DiscardPile`
- `MegaCrit.Sts2.Core.Entities.Players.PlayerCombatState.get_ExhaustPile`
- `MegaCrit.Sts2.Core.Entities.Players.PlayerCombatState.get_PlayPile`
- `MegaCrit.Sts2.Core.Entities.Players.PlayerCombatState.get_Energy`
- `MegaCrit.Sts2.Core.Entities.Players.PlayerCombatState.get_MaxEnergy`
- `MegaCrit.Sts2.Core.Entities.Players.PlayerCombatState.HasCardsToPlay`

### Creature data
- `MegaCrit.Sts2.Core.Entities.Creatures.Creature.get_CurrentHp`
- `MegaCrit.Sts2.Core.Entities.Creatures.Creature.get_MaxHp`
- `MegaCrit.Sts2.Core.Entities.Creatures.Creature.get_Block`
- `MegaCrit.Sts2.Core.Entities.Creatures.Creature.get_Name`
- `MegaCrit.Sts2.Core.Entities.Creatures.Creature.get_IsEnemy`
- `MegaCrit.Sts2.Core.Entities.Creatures.Creature.get_IsHittable`
- `MegaCrit.Sts2.Core.Entities.Creatures.Creature.get_Powers`

### State-change notifiers
- `MegaCrit.Sts2.Core.Combat.CombatStateTracker.add_CombatStateChanged`
- `MegaCrit.Sts2.Core.Multiplayer.Game.PeerInput.ScreenStateTracker.GetCurrentScreen`

Use `CombatStateTracker` for diff-triggered export, but debounce it.

## Best action hooks

## 1. Combat card play

Best hooks:
- `MegaCrit.Sts2.Core.Models.CardModel.CanPlay()`
- `MegaCrit.Sts2.Core.Models.CardModel.IsValidTarget(target)`
- `MegaCrit.Sts2.Core.Models.CardModel.TryManualPlay(target)`
- `MegaCrit.Sts2.Core.Models.CardModel.EnqueueManualPlay(target)`

Related action type:
- `MegaCrit.Sts2.Core.GameActions.PlayCardAction`
  - ctor `(cardModel, target)`
  - ctor `(player, netCombatCard, cardModelId, targetId)`

Recommendation:
- prefer `TryManualPlay(target)` first
- use `EnqueueManualPlay(target)` if explicit queue behavior is needed
- avoid constructing `PlayCardAction` directly unless necessary

## 2. End turn

Related action type:
- `MegaCrit.Sts2.Core.GameActions.EndPlayerTurnAction`
  - ctor `(player, combatRound)`

Best execution path:
- enqueue through `ActionQueueSynchronizer`
- do not simulate UI button press first

## 3. Map choice

Related action types:
- `MegaCrit.Sts2.Core.GameActions.MoveToMapCoordAction`
  - ctor `(player, destination)`
- `MegaCrit.Sts2.Core.GameActions.VoteForMapCoordAction`
  - ctor `(player, source, destination)`

Related domain/UI methods:
- `MegaCrit.Sts2.Core.Map.MapCoord` ctor `(col, row)`
- `MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen.TravelToMapCoord(coord)`
- `MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen.OnMapPointSelectedLocally(point)`

Recommendation:
- prefer `MoveToMapCoordAction` / `VoteForMapCoordAction`
- avoid direct UI map selection if possible

## 4. Reward choice

Best hooks:
- `MegaCrit.Sts2.Core.Rewards.Reward.OnSelect()`
- `MegaCrit.Sts2.Core.Rewards.Reward.OnSkipped()`

Related UI methods:
- `MegaCrit.Sts2.Core.Nodes.Screens.NRewardsScreen.RewardCollectedFrom(button)`
- `MegaCrit.Sts2.Core.Nodes.Screens.NRewardsScreen.RewardSkippedFrom(button)`
- `MegaCrit.Sts2.Core.Nodes.Screens.NRewardsScreen.OnProceedButtonPressed(_)`

Recommendation:
- prefer `Reward` domain methods if reward objects are easy to resolve
- keep screen button methods as fallback only

## 5. Potion use

Best hook:
- `MegaCrit.Sts2.Core.Models.PotionModel.EnqueueManualUse(target)`

Related action type:
- `MegaCrit.Sts2.Core.GameActions.UsePotionAction`
  - ctor `(potion, target, isCombatInProgress)`
  - ctor `(player, potionIndex, targetId, targetPlayerId, isCombatInProgress)`

## 6. Queue/synchronization layer

Best low-level action ingress:
- `MegaCrit.Sts2.Core.GameActions.Multiplayer.ActionQueueSynchronizer.RequestEnqueue(action)`
- `MegaCrit.Sts2.Core.GameActions.Multiplayer.ActionQueueSynchronizer.EnqueueAction(action, actionOwnerId)`
- `MegaCrit.Sts2.Core.GameActions.Multiplayer.ActionQueueSynchronizer.RequestEnqueueHookAction(action)`
- `MegaCrit.Sts2.Core.GameActions.Multiplayer.ActionQueueSynchronizer.EnqueueHookAction(gameAction)`
- `MegaCrit.Sts2.Core.GameActions.Multiplayer.ActionQueueSynchronizer.ResumeActionAfterPlayerChoice(id)`

Supporting execution/state:
- `MegaCrit.Sts2.Core.GameActions.ActionExecutor.get_IsRunning`
- `MegaCrit.Sts2.Core.GameActions.ActionExecutor.get_CurrentlyRunningAction`
- `MegaCrit.Sts2.Core.GameActions.ActionExecutor.ExecuteActions`
- `MegaCrit.Sts2.Core.GameActions.ActionExecutor.Pause`
- `MegaCrit.Sts2.Core.GameActions.ActionExecutor.Unpause`

Recommendation:
- queue semantic actions through the synchronizer
- keep all bridge command execution on the main thread

## Waiting-for-input heuristic

Safest combat `waiting_for_input` test:

- `CombatManager.IsInProgress`
- `CombatManager.IsPlayPhase`
- `!CombatManager.PlayerActionsDisabled`
- queue drained via `WaitUntilQueueIsEmptyOrWaitingOnNonPlayerDrivenAction`
- `ActionExecutor.CurrentlyRunningAction == null` or not running

Outside combat:
- derive screen state from `ScreenStateTracker.GetCurrentScreen()`
- then resolve active room/screen objects for rewards, map, event, shop, rest site

## Bridge recommendation by milestone

### Milestone 3 — one-shot export
- subscribe to `RunManager` room/act events and `CombatManager` setup/end events
- export one JSON snapshot on those boundaries

### Milestone 4 — stable export loop
- add `CombatStateTracker.CombatStateChanged`
- debounce and only emit when snapshot hash or phase changes

### Milestone 5 — `end_turn`
- validate `waiting_for_input`
- enqueue `EndPlayerTurnAction`

### Milestone 6 — `play_card`
- resolve card by stable bridge ID
- map ID to current `CardModel`
- validate `CanPlay()` and target validity
- call `TryManualPlay(target)`

### Milestone 7 — rewards/map/events
- rewards: `Reward.OnSelect()` / `OnSkipped()`
- map: `MoveToMapCoordAction`
- events: inspect `EventModel.CurrentOptions`, then likely route through event domain methods or `NEventRoom.OptionButtonClicked(option, index)` as a fallback

## Biggest risks

- event storms from `CombatStateChanged`
- stale object refs across room changes
- action queue race conditions
- direct UI calls breaking across patches
- multiplayer/net identity assumptions if we bypass model/manual APIs

## Practical next implementation path

1. build `BridgeRuntime`
2. wire `RunManager` + `CombatManager` subscriptions
3. export one-shot combat snapshot JSON
4. add `waiting_for_input`
5. implement `end_turn`
6. implement `play_card`

## Extra useful discovery

Net card identity support exists:
- `MegaCrit.Sts2.Core.GameActions.Multiplayer.NetCombatCardDb.GetCardId`
- `MegaCrit.Sts2.Core.GameActions.Multiplayer.NetCombatCardDb.GetCard`
- `MegaCrit.Sts2.Core.GameActions.Multiplayer.NetCombatCardDb.TryGetCardId`
- `MegaCrit.Sts2.Core.GameActions.Multiplayer.NetCombatCardDb.TryGetCard`

That looks like the cleanest way to assign stable combat card IDs for external commands.
