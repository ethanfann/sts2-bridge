using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2Bridge.Bridge;

internal static class StateExporter
{
    private static string? _publishedStateId;
    private static string? _publishedGuardJson;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    public static string StateFilePath => Path.Combine(StateDirectoryPath, "state.json");

    internal static string StateDirectoryPath => ProjectSettings.GlobalizePath("user://sts2-bridge");

    public static StableBridgeSnapshot BuildStableSnapshot()
    {
        RunManager? runManager = RunManager.Instance;
        CombatManager? combatManager = CombatManager.Instance;
        RunState? runState = BridgeIntrospection.GetRunState(runManager);
        CombatState? combatState = BridgeIntrospection.GetCombatState(runState);
        Player? player = BridgeIntrospection.GetPrimaryPlayer(combatState, runState);
        PlayerCombatState? playerCombatState = player?.PlayerCombatState;
        ScreenSnapshot screen = ScreenDiagnostics.Capture();
        CardSelectionContextSnapshot? cardSelection = BridgeIntrospection.BuildCardSelectionContext();
        RelicSelectionContextSnapshot? relicSelection = cardSelection is null ? BridgeIntrospection.BuildRelicSelectionContext() : null;
        MapContextSnapshot? map = BridgeIntrospection.BuildMapContext(runState);
        MerchantContextSnapshot? merchant = cardSelection is null && relicSelection is null && map is null ? BridgeIntrospection.BuildMerchantContext() : null;
        RewardContextSnapshot? rewards = cardSelection is null && relicSelection is null && map is null ? BridgeIntrospection.BuildRewardContext() : null;
        TreasureContextSnapshot? treasure = cardSelection is null && relicSelection is null && merchant is null && rewards is null && map is null ? BridgeIntrospection.BuildTreasureContext() : null;
        RestSiteContextSnapshot? restSite = cardSelection is null && relicSelection is null && merchant is null && rewards is null && treasure is null && map is null ? BridgeIntrospection.BuildRestSiteContext(runState) : null;
        bool shouldExportEventChoices = cardSelection is null && relicSelection is null && merchant is null && rewards is null && treasure is null && restSite is null && map is null;
        List<ChoiceSnapshot> choices = shouldExportEventChoices ? BridgeIntrospection.BuildChoiceSnapshots(runState) : [];
        ChoiceContextSnapshot? choiceContext = shouldExportEventChoices ? BridgeIntrospection.BuildChoiceContext(runState) : null;
        ProceedContextSnapshot? proceedContext = BridgeIntrospection.BuildProceedContext(runState, choices.Count, cardSelection);
        List<PotionSnapshot> potions = BridgeIntrospection.BuildPotionSnapshots(player);

        return new StableBridgeSnapshot
        {
            ProtocolVersion = 1,
            Screen = screen,
            Combat = combatState is null ? null : new CombatSnapshot(
                combatState.RoundNumber, combatState.CurrentSide.ToString(),
                playerCombatState?.TurnNumber, playerCombatState?.Phase.ToString(),
                combatManager?.PlayerActionsDisabled ?? true,
                runManager?.ActionExecutor?.CurrentlyRunningAction?.GetType().Name),
            Scene = !screen.Supported && runState is not null ? "unsupported" : DetermineScene(runManager, combatManager, runState, combatState, cardSelection, relicSelection, merchant, rewards, treasure, restSite, map),
            WaitingForInput = screen.Supported && DetermineWaitingForInput(runManager, combatManager, playerCombatState, choices.Count, cardSelection, relicSelection, merchant, rewards, treasure, restSite, proceedContext, map),
            Run = BuildRunSnapshot(runState),
            Player = BuildPlayerSnapshot(player),
            Enemies = BuildEnemySnapshots(combatState),
            Hand = CardStateExporter.BuildPile(playerCombatState?.Hand),
            Deck = CardStateExporter.BuildPile(player?.Deck),
            Piles = CardStateExporter.BuildPiles(playerCombatState),
            CardsPlayed = CardStateExporter.BuildPlayedCards(combatState, player),
            Potions = potions,
            CardSelection = cardSelection,
            RelicSelection = relicSelection,
            Merchant = merchant,
            Rewards = rewards,
            Treasure = treasure,
            RestSite = restSite,
            Map = map,
            ChoiceContext = choiceContext,
            ProceedContext = proceedContext,
            Choices = choices,
            DrawPileCount = GetPileCount(playerCombatState?.DrawPile),
            DiscardPileCount = GetPileCount(playerCombatState?.DiscardPile),
            ExhaustPileCount = GetPileCount(playerCombatState?.ExhaustPile),
            PlayPileCount = GetPileCount(playerCombatState?.PlayPile),
        };
    }

    public static string BuildStableStateJson(StableBridgeSnapshot snapshot)
    {
        return JsonSerializer.Serialize(snapshot, JsonOptions);
    }

    public static string BuildStateJson(StableBridgeSnapshot stableSnapshot)
    {
        string guardJson = BuildGuardJson(stableSnapshot);
        BridgeSnapshot snapshot = new()
        {
            ProtocolVersion = stableSnapshot.ProtocolVersion,
            StateId = guardJson == _publishedGuardJson && _publishedStateId is not null
                ? _publishedStateId : $"state_{Guid.NewGuid():N}",
            Timestamp = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            Scene = stableSnapshot.Scene,
            WaitingForInput = stableSnapshot.WaitingForInput,
            Screen = stableSnapshot.Screen,
            Combat = stableSnapshot.Combat,
            Run = stableSnapshot.Run,
            Player = stableSnapshot.Player,
            Enemies = stableSnapshot.Enemies,
            Hand = stableSnapshot.Hand,
            Deck = stableSnapshot.Deck,
            Piles = stableSnapshot.Piles,
            CardsPlayed = stableSnapshot.CardsPlayed,
            Potions = stableSnapshot.Potions,
            CardSelection = stableSnapshot.CardSelection,
            RelicSelection = stableSnapshot.RelicSelection,
            Merchant = stableSnapshot.Merchant,
            Rewards = stableSnapshot.Rewards,
            Treasure = stableSnapshot.Treasure,
            RestSite = stableSnapshot.RestSite,
            Map = stableSnapshot.Map,
            ChoiceContext = stableSnapshot.ChoiceContext,
            ProceedContext = stableSnapshot.ProceedContext,
            Choices = stableSnapshot.Choices,
            DrawPileCount = stableSnapshot.DrawPileCount,
            DiscardPileCount = stableSnapshot.DiscardPileCount,
            ExhaustPileCount = stableSnapshot.ExhaustPileCount,
            PlayPileCount = stableSnapshot.PlayPileCount,
        };

        return JsonSerializer.Serialize(snapshot, JsonOptions);
    }

    private static string BuildGuardJson(StableBridgeSnapshot snapshot)
    {
        // Combat diagnostics include floating damage text and animated labels.
        // Gameplay context is already exported separately; those labels must
        // not invalidate an otherwise identical card-play decision.
        if (snapshot.Scene == "combat" && snapshot.Screen.Type == "NCombatRoom")
        {
            snapshot = snapshot with { Screen = snapshot.Screen with { VisibleControls = [], Truncated = false } };
        }
        return BuildStableStateJson(snapshot);
    }

    public static bool MatchesCurrentObservation(string stateId) =>
        stateId == _publishedStateId
        && _publishedGuardJson == BuildGuardJson(BuildStableSnapshot());

    public static void WriteStateJson(string stateJson, StableBridgeSnapshot snapshot)
    {
        Directory.CreateDirectory(StateDirectoryPath);

        string tempFilePath = Path.Combine(StateDirectoryPath, "state.json.tmp");
        File.WriteAllText(tempFilePath, stateJson);
        File.Move(tempFilePath, StateFilePath, true);
        using JsonDocument document = JsonDocument.Parse(stateJson);
        _publishedStateId = document.RootElement.GetProperty("state_id").GetString();
        _publishedGuardJson = BuildGuardJson(snapshot);
    }

    private static string DetermineScene(
        RunManager? runManager,
        CombatManager? combatManager,
        RunState? runState,
        CombatState? combatState,
        CardSelectionContextSnapshot? cardSelection,
        RelicSelectionContextSnapshot? relicSelection,
        MerchantContextSnapshot? merchant,
        RewardContextSnapshot? rewards,
        TreasureContextSnapshot? treasure,
        RestSiteContextSnapshot? restSite,
        MapContextSnapshot? map)
    {
        if (cardSelection is not null)
        {
            return "card_selection";
        }

        if (relicSelection is not null)
        {
            return "relic_selection";
        }

        if (map is not null)
        {
            return "map";
        }

        if (treasure is not null)
        {
            return "treasure";
        }

        if (restSite is not null)
        {
            return "rest_site";
        }

        if (merchant is not null)
        {
            return merchant.Kind == "inventory" ? "shop" : "merchant_room";
        }

        if (rewards is not null)
        {
            return "rewards";
        }

        if (combatManager?.IsInProgress == true && combatState is not null)
        {
            return "combat";
        }

        if (runManager?.IsInProgress == true && runState is not null)
        {
            return BridgeIntrospection.DetermineNonCombatScene(runState);
        }

        return "main_menu";
    }

    private static bool DetermineWaitingForInput(
        RunManager? runManager,
        CombatManager? combatManager,
        PlayerCombatState? playerCombatState,
        int choiceCount,
        CardSelectionContextSnapshot? cardSelection,
        RelicSelectionContextSnapshot? relicSelection,
        MerchantContextSnapshot? merchant,
        RewardContextSnapshot? rewards,
        TreasureContextSnapshot? treasure,
        RestSiteContextSnapshot? restSite,
        ProceedContextSnapshot? proceedContext,
        MapContextSnapshot? map)
    {
        if (cardSelection is not null)
        {
            return true;
        }

        if (relicSelection is not null)
        {
            return true;
        }

        if (merchant is not null)
        {
            return merchant.EnterShopAvailable || merchant.LeaveAvailable || merchant.ProceedAvailable;
        }

        if (rewards is not null)
        {
            return true;
        }

        if (treasure is not null)
        {
            return treasure.ChestOpenAvailable || treasure.ProceedAvailable || treasure.Relics.Count > 0;
        }

        if (restSite is not null)
        {
            return restSite.ProceedAvailable || restSite.Options.Count > 0;
        }

        if (map is not null)
        {
            return map.IsTravelEnabled && !map.IsTraveling;
        }

        if (proceedContext is not null)
        {
            return true;
        }

        if (choiceCount > 0)
        {
            return true;
        }

        if (combatManager is null)
        {
            return false;
        }

        if (!combatManager.IsInProgress || playerCombatState?.Phase != PlayerTurnPhase.Play
            || combatManager.PlayerActionsDisabled || BridgeIntrospection.HasPendingHandSelection)
        {
            return false;
        }

        return runManager?.ActionExecutor is not { IsRunning: true, CurrentlyRunningAction: not null };
    }

    private static RunSnapshot? BuildRunSnapshot(RunState? runState)
    {
        if (runState is null)
        {
            return null;
        }

        return new RunSnapshot
        {
            Seed = runState.Rng.StringSeed,
            Ascension = runState.AscensionLevel,
            BaseRoomType = runState.BaseRoom?.RoomType.ToString(),
            EventId = (BridgeIntrospection.GetEventModel(runState) as MegaCrit.Sts2.Core.Models.EventModel)?.Id.Entry,
            CurrentAct = runState.CurrentActIndex + 1,
            ActFloor = runState.ActFloor,
            TotalFloor = runState.TotalFloor,
            CurrentRoomType = runState.CurrentRoom?.RoomType.ToString(),
            IsGameOver = runState.IsGameOver,
        };
    }

    private static PlayerSnapshot? BuildPlayerSnapshot(Player? player)
    {
        if (player is null)
        {
            return null;
        }

        Creature? creature = player.Creature;
        PlayerCombatState? combatState = player.PlayerCombatState;
        int? energy = TryGetCombatEnergy(combatState);
        int maxEnergy = TryGetCombatMaxEnergy(combatState) ?? player.MaxEnergy;

        return new PlayerSnapshot
        {
            Character = player.Character.Id.Entry,
            Stars = combatState?.Stars,
            Hp = creature?.CurrentHp,
            MaxHp = creature?.MaxHp,
            Block = creature?.Block,
            Gold = player.Gold,
            Energy = energy,
            MaxEnergy = maxEnergy,
            DeckCount = GetPileCount(player.Deck),
            RelicCount = CountEntries(player.Relics),
            PotionCount = CountEntries(player.Potions),
            PotionCapacity = player.MaxPotionCount,
            Powers = BuildPowers(creature),
            Relics = player.Relics.Select(relic => new OwnedRelicSnapshot(
                relic.Id.Entry, relic.Title.GetFormattedText(), relic.DynamicDescription.GetFormattedText(),
                relic.Status.ToString(), relic.IsUsedUp, relic.ShowCounter ? relic.DisplayAmount : null,
                relic.StackCount, relic.DynamicVars.ToDictionary(v => v.Key, v => v.Value.BaseValue),
                HoverTipExporter.Build(relic.HoverTipsExcludingRelic))).ToList(),
        };
    }

    private static List<PowerSnapshot> BuildPowers(Creature? creature) => creature?.Powers
        .Where(power => power.IsVisible)
        .Select(power => new PowerSnapshot(power.Id.Entry, power.Title.GetFormattedText(), power.Amount,
            power.TypeForCurrentAmount.ToString(), power.StackType.ToString(),
            power.HoverTips.OfType<HoverTip>().Select(tip => tip.Description).ToList())).ToList() ?? [];

    private static List<IntentSnapshot> BuildIntents(Creature enemy, CombatState combat)
    {
        // Only the currently announced intents. Never inspect the move state
        // machine's future branches or turn a Hidden/Unknown intent into an attack.
        if (enemy.Monster is null || !enemy.IsAlive)
        {
            return [];
        }
        Creature[] targets = combat.Players.Select(player => player.Creature).ToArray();
        return enemy.Monster.NextMove.Intents.Select(intent =>
        {
            AttackIntent? attack = intent as AttackIntent;
            HoverTip? tip = intent.HasIntentTip ? intent.GetHoverTip(targets, enemy) : null;
            return new IntentSnapshot(intent.IntentType.ToString(), tip?.Title, tip?.Description,
                attack?.GetSingleDamage(targets, enemy), attack?.Repeats, attack?.GetTotalDamage(targets, enemy));
        }).ToList();
    }

    private static List<EnemySnapshot> BuildEnemySnapshots(CombatState? combatState)
    {
        List<EnemySnapshot> enemies = [];
        if (combatState is null || combatState.Enemies is null)
        {
            return enemies;
        }

        int index = 0;
        foreach (Creature enemy in combatState.Enemies)
        {
            enemies.Add(new EnemySnapshot
            {
                Id = BridgeIntrospection.BuildCreatureId(enemy, index),
                Name = enemy.Name,
                Hp = enemy.CurrentHp,
                MaxHp = enemy.MaxHp,
                Block = enemy.Block,
                IsHittable = enemy.IsHittable,
                ModelId = enemy.Monster?.Id.Entry,
                Powers = BuildPowers(enemy),
                Intents = BuildIntents(enemy, combatState),
            });
            index += 1;
        }

        return enemies;
    }

    private static int GetPileCount(CardPile? pile)
    {
        if (pile is null)
        {
            return 0;
        }

        return CountEntries(pile.Cards);
    }

    private static int CountEntries(IEnumerable? entries)
    {
        if (entries is null)
        {
            return 0;
        }

        int count = 0;
        foreach (object? _ in entries)
        {
            count += 1;
        }

        return count;
    }

    private static int? TryGetCombatEnergy(PlayerCombatState? combatState)
    {
        if (combatState is null)
        {
            return null;
        }

        try
        {
            return combatState.Energy;
        }
        catch
        {
            return null;
        }
    }

    private static int? TryGetCombatMaxEnergy(PlayerCombatState? combatState)
    {
        if (combatState is null)
        {
            return null;
        }

        try
        {
            return combatState.MaxEnergy;
        }
        catch
        {
            return null;
        }
    }
}

internal sealed record StableBridgeSnapshot
{
    [property: JsonPropertyName("protocol_version")]
    public required int ProtocolVersion { get; init; }

    [property: JsonPropertyName("scene")]
    public required string Scene { get; init; }

    [property: JsonPropertyName("waiting_for_input")]
    public required bool WaitingForInput { get; init; }

    [property: JsonPropertyName("screen")]
    public required ScreenSnapshot Screen { get; init; }

    [property: JsonPropertyName("combat")]
    public required CombatSnapshot? Combat { get; init; }

    [property: JsonPropertyName("run")]
    public required RunSnapshot? Run { get; init; }

    [property: JsonPropertyName("player")]
    public required PlayerSnapshot? Player { get; init; }

    [property: JsonPropertyName("enemies")]
    public required List<EnemySnapshot> Enemies { get; init; }

    [property: JsonPropertyName("hand")]
    public required List<CardSnapshot> Hand { get; init; }

    [property: JsonPropertyName("deck")]
    public required List<CardSnapshot> Deck { get; init; }

    [property: JsonPropertyName("piles")]
    public required CardPilesSnapshot? Piles { get; init; }

    [property: JsonPropertyName("cards_played")]
    public required List<CardPlaySnapshot> CardsPlayed { get; init; }

    [property: JsonPropertyName("potions")]
    public required List<PotionSnapshot> Potions { get; init; }

    [property: JsonPropertyName("card_selection")]
    public required CardSelectionContextSnapshot? CardSelection { get; init; }

    [property: JsonPropertyName("relic_selection")]
    public required RelicSelectionContextSnapshot? RelicSelection { get; init; }

    [property: JsonPropertyName("merchant")]
    public required MerchantContextSnapshot? Merchant { get; init; }

    [property: JsonPropertyName("rewards")]
    public required RewardContextSnapshot? Rewards { get; init; }

    [property: JsonPropertyName("treasure")]
    public required TreasureContextSnapshot? Treasure { get; init; }

    [property: JsonPropertyName("rest_site")]
    public required RestSiteContextSnapshot? RestSite { get; init; }

    [property: JsonPropertyName("map")]
    public required MapContextSnapshot? Map { get; init; }

    [property: JsonPropertyName("choice_context")]
    public required ChoiceContextSnapshot? ChoiceContext { get; init; }

    [property: JsonPropertyName("proceed_context")]
    public required ProceedContextSnapshot? ProceedContext { get; init; }

    [property: JsonPropertyName("choices")]
    public required List<ChoiceSnapshot> Choices { get; init; }

    [property: JsonPropertyName("draw_pile_count")]
    public required int DrawPileCount { get; init; }

    [property: JsonPropertyName("discard_pile_count")]
    public required int DiscardPileCount { get; init; }

    [property: JsonPropertyName("exhaust_pile_count")]
    public required int ExhaustPileCount { get; init; }

    [property: JsonPropertyName("play_pile_count")]
    public required int PlayPileCount { get; init; }
}

internal sealed record BridgeSnapshot
{
    [property: JsonPropertyName("protocol_version")]
    public required int ProtocolVersion { get; init; }

    [property: JsonPropertyName("command_guards")]
    public string[] CommandGuards { get; init; } = ["expected_state_id"];

    [property: JsonPropertyName("state_id")]
    public required string StateId { get; init; }

    [property: JsonPropertyName("timestamp")]
    public required string Timestamp { get; init; }

    [property: JsonPropertyName("scene")]
    public required string Scene { get; init; }

    [property: JsonPropertyName("waiting_for_input")]
    public required bool WaitingForInput { get; init; }

    [property: JsonPropertyName("screen")]
    public required ScreenSnapshot Screen { get; init; }

    [property: JsonPropertyName("combat")]
    public required CombatSnapshot? Combat { get; init; }

    [property: JsonPropertyName("run")]
    public required RunSnapshot? Run { get; init; }

    [property: JsonPropertyName("player")]
    public required PlayerSnapshot? Player { get; init; }

    [property: JsonPropertyName("enemies")]
    public required List<EnemySnapshot> Enemies { get; init; }

    [property: JsonPropertyName("hand")]
    public required List<CardSnapshot> Hand { get; init; }

    [property: JsonPropertyName("deck")]
    public required List<CardSnapshot> Deck { get; init; }

    [property: JsonPropertyName("piles")]
    public required CardPilesSnapshot? Piles { get; init; }

    [property: JsonPropertyName("cards_played")]
    public required List<CardPlaySnapshot> CardsPlayed { get; init; }

    [property: JsonPropertyName("potions")]
    public required List<PotionSnapshot> Potions { get; init; }

    [property: JsonPropertyName("card_selection")]
    public required CardSelectionContextSnapshot? CardSelection { get; init; }

    [property: JsonPropertyName("relic_selection")]
    public required RelicSelectionContextSnapshot? RelicSelection { get; init; }

    [property: JsonPropertyName("merchant")]
    public required MerchantContextSnapshot? Merchant { get; init; }

    [property: JsonPropertyName("rewards")]
    public required RewardContextSnapshot? Rewards { get; init; }

    [property: JsonPropertyName("treasure")]
    public required TreasureContextSnapshot? Treasure { get; init; }

    [property: JsonPropertyName("rest_site")]
    public required RestSiteContextSnapshot? RestSite { get; init; }

    [property: JsonPropertyName("map")]
    public required MapContextSnapshot? Map { get; init; }

    [property: JsonPropertyName("choice_context")]
    public required ChoiceContextSnapshot? ChoiceContext { get; init; }

    [property: JsonPropertyName("proceed_context")]
    public required ProceedContextSnapshot? ProceedContext { get; init; }

    [property: JsonPropertyName("choices")]
    public required List<ChoiceSnapshot> Choices { get; init; }

    [property: JsonPropertyName("draw_pile_count")]
    public required int DrawPileCount { get; init; }

    [property: JsonPropertyName("discard_pile_count")]
    public required int DiscardPileCount { get; init; }

    [property: JsonPropertyName("exhaust_pile_count")]
    public required int ExhaustPileCount { get; init; }

    [property: JsonPropertyName("play_pile_count")]
    public required int PlayPileCount { get; init; }
}

internal sealed record CombatSnapshot(
    [property: JsonPropertyName("round")] int Round,
    [property: JsonPropertyName("side")] string Side,
    [property: JsonPropertyName("player_turn")] int? PlayerTurn,
    [property: JsonPropertyName("player_phase")] string? PlayerPhase,
    [property: JsonPropertyName("actions_disabled")] bool ActionsDisabled,
    [property: JsonPropertyName("running_action")] string? RunningAction);

internal sealed record RunSnapshot
{
    [property: JsonPropertyName("seed")]
    public required string Seed { get; init; }

    [property: JsonPropertyName("ascension")]
    public required int Ascension { get; init; }

    [property: JsonPropertyName("base_room_type")]
    public required string? BaseRoomType { get; init; }

    [property: JsonPropertyName("event_id")]
    public required string? EventId { get; init; }

    [property: JsonPropertyName("current_act")]
    public required int CurrentAct { get; init; }

    [property: JsonPropertyName("act_floor")]
    public required int ActFloor { get; init; }

    [property: JsonPropertyName("total_floor")]
    public required int TotalFloor { get; init; }

    [property: JsonPropertyName("current_room_type")]
    public required string? CurrentRoomType { get; init; }

    [property: JsonPropertyName("is_game_over")]
    public required bool IsGameOver { get; init; }
}

internal sealed record PlayerSnapshot
{
    [property: JsonPropertyName("character")]
    public required string Character { get; init; }

    [property: JsonPropertyName("stars")]
    public required int? Stars { get; init; }

    [property: JsonPropertyName("hp")]
    public required int? Hp { get; init; }

    [property: JsonPropertyName("max_hp")]
    public required int? MaxHp { get; init; }

    [property: JsonPropertyName("block")]
    public required int? Block { get; init; }

    [property: JsonPropertyName("gold")]
    public required int Gold { get; init; }

    [property: JsonPropertyName("energy")]
    public required int? Energy { get; init; }

    [property: JsonPropertyName("max_energy")]
    public required int MaxEnergy { get; init; }

    [property: JsonPropertyName("deck_count")]
    public required int DeckCount { get; init; }

    [property: JsonPropertyName("relic_count")]
    public required int RelicCount { get; init; }

    [property: JsonPropertyName("potion_count")]
    public required int PotionCount { get; init; }

    [property: JsonPropertyName("potion_capacity")]
    public required int PotionCapacity { get; init; }

    [property: JsonPropertyName("powers")]
    public required List<PowerSnapshot> Powers { get; init; }

    [property: JsonPropertyName("relics")]
    public required List<OwnedRelicSnapshot> Relics { get; init; }
}

internal sealed record PowerSnapshot(
    [property: JsonPropertyName("model_id")] string ModelId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("amount")] int Amount,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("stack_type")] string StackType,
    [property: JsonPropertyName("descriptions")] List<string> Descriptions);

internal sealed record OwnedRelicSnapshot(
    [property: JsonPropertyName("model_id")] string ModelId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("used_up")] bool UsedUp,
    [property: JsonPropertyName("counter")] int? Counter,
    [property: JsonPropertyName("stack_count")] int StackCount,
    [property: JsonPropertyName("base_values")] Dictionary<string, decimal> BaseValues,
    [property: JsonPropertyName("hover_tips")] List<HoverTipSnapshot> HoverTips);

internal sealed record IntentSnapshot(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("damage_per_hit")] int? DamagePerHit,
    [property: JsonPropertyName("hits")] int? Hits,
    [property: JsonPropertyName("total_damage")] int? TotalDamage);

internal sealed record EnemySnapshot
{
    [property: JsonPropertyName("id")]
    public required string Id { get; init; }

    [property: JsonPropertyName("name")]
    public required string Name { get; init; }

    [property: JsonPropertyName("hp")]
    public required int Hp { get; init; }

    [property: JsonPropertyName("max_hp")]
    public required int MaxHp { get; init; }

    [property: JsonPropertyName("block")]
    public required int Block { get; init; }

    [property: JsonPropertyName("is_hittable")]
    public required bool IsHittable { get; init; }

    [property: JsonPropertyName("model_id")]
    public required string? ModelId { get; init; }

    [property: JsonPropertyName("powers")]
    public required List<PowerSnapshot> Powers { get; init; }

    [property: JsonPropertyName("intents")]
    public required List<IntentSnapshot> Intents { get; init; }
}

internal sealed record CardSnapshot
{
    [property: JsonPropertyName("id")]
    public required string Id { get; init; }

    [property: JsonPropertyName("name")]
    public required string Name { get; init; }

    [property: JsonPropertyName("description")]
    public required string Description { get; init; }

    [property: JsonPropertyName("hover_tips")]
    public required List<HoverTipSnapshot> HoverTips { get; init; }

    [property: JsonPropertyName("cost")]
    public required string Cost { get; init; }

    [property: JsonPropertyName("rarity")]
    public required string Rarity { get; init; }

    [property: JsonPropertyName("playable")]
    public required bool Playable { get; init; }

    [property: JsonPropertyName("model_id")]
    public required string ModelId { get; init; }

    [property: JsonPropertyName("deck_card_id")]
    public string? DeckCardId { get; init; }

    [property: JsonPropertyName("type")]
    public required string Type { get; init; }

    [property: JsonPropertyName("target_type")]
    public required string TargetType { get; init; }

    [property: JsonPropertyName("energy_cost")]
    public required int EnergyCost { get; init; }

    [property: JsonPropertyName("costs_x")]
    public required bool CostsX { get; init; }

    [property: JsonPropertyName("star_cost")]
    public required int StarCost { get; init; }

    [property: JsonPropertyName("costs_stars_x")]
    public required bool CostsStarsX { get; init; }

    [property: JsonPropertyName("upgrade_level")]
    public required int UpgradeLevel { get; init; }

    [property: JsonPropertyName("keywords")]
    public required List<string> Keywords { get; init; }

    [property: JsonPropertyName("retain_this_turn")]
    public required bool RetainThisTurn { get; init; }

    [property: JsonPropertyName("exhaust_on_next_play")]
    public required bool ExhaustOnNextPlay { get; init; }

    [property: JsonPropertyName("enchantment")]
    public string? Enchantment { get; init; }

    [property: JsonPropertyName("affliction")]
    public string? Affliction { get; init; }

    [property: JsonPropertyName("base_values")]
    public required Dictionary<string, decimal> BaseValues { get; init; }

    [property: JsonPropertyName("play_targets")]
    public required List<CardTargetPreview> PlayTargets { get; init; }

    [property: JsonPropertyName("upgrade_preview")]
    public CardUpgradePreviewSnapshot? UpgradePreview { get; init; }
}

internal sealed record PotionSnapshot
{
    [property: JsonPropertyName("id")]
    public required string? Id { get; init; }

    [property: JsonPropertyName("model_id")]
    public required string ModelId { get; init; }

    [property: JsonPropertyName("slot_index")]
    public int? SlotIndex { get; init; }

    [property: JsonPropertyName("discard_available")]
    public required bool DiscardAvailable { get; init; }

    [property: JsonPropertyName("base_values")]
    public required Dictionary<string, decimal> BaseValues { get; init; }

    [property: JsonPropertyName("title")]
    public required string Title { get; init; }

    [property: JsonPropertyName("description")]
    public required string Description { get; init; }

    [property: JsonPropertyName("hover_tips")]
    public required List<HoverTipSnapshot> HoverTips { get; init; }

    [property: JsonPropertyName("rarity")]
    public required string Rarity { get; init; }

    [property: JsonPropertyName("usage")]
    public required string Usage { get; init; }

    [property: JsonPropertyName("target_type")]
    public required string TargetType { get; init; }
}

internal sealed record ChoiceSnapshot
{
    [property: JsonPropertyName("id")]
    public required string Id { get; init; }

    [property: JsonPropertyName("title")]
    public required string Title { get; init; }

    [property: JsonPropertyName("description")]
    public required string Description { get; init; }

    [property: JsonPropertyName("hover_tips")]
    public required List<HoverTipSnapshot> HoverTips { get; init; }

    [property: JsonPropertyName("relic_model_id")]
    public string? RelicModelId { get; init; }

    [property: JsonPropertyName("locked")]
    public required bool Locked { get; init; }

    [property: JsonPropertyName("proceed")]
    public required bool Proceed { get; init; }

    [property: JsonPropertyName("will_kill_player")]
    public required bool WillKillPlayer { get; init; }
}

internal sealed record ChoiceContextSnapshot
{
    [property: JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [property: JsonPropertyName("title")]
    public required string Title { get; init; }

    [property: JsonPropertyName("description")]
    public required string Description { get; init; }
}

internal sealed record ProceedContextSnapshot
{
    [property: JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [property: JsonPropertyName("label")]
    public required string Label { get; init; }
}

internal sealed record CardSelectionContextSnapshot
{
    [property: JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [property: JsonPropertyName("source_pile")]
    public string? SourcePile { get; init; }

    [property: JsonPropertyName("selected_card_ids")]
    public List<string>? SelectedCardIds { get; init; }

    [property: JsonPropertyName("confirm_available")]
    public bool? ConfirmAvailable { get; init; }

    [property: JsonPropertyName("prompt")]
    public required string Prompt { get; init; }

    [property: JsonPropertyName("min_select")]
    public required int MinSelect { get; init; }

    [property: JsonPropertyName("max_select")]
    public required int MaxSelect { get; init; }

    [property: JsonPropertyName("require_manual_confirmation")]
    public required bool RequireManualConfirmation { get; init; }

    [property: JsonPropertyName("cancelable")]
    public required bool Cancelable { get; init; }

    [property: JsonPropertyName("alternatives")]
    public List<CardRewardAlternativeSnapshot>? Alternatives { get; init; }

    [property: JsonPropertyName("cards")]
    public required List<CardSnapshot> Cards { get; init; }
}

internal sealed record CardRewardAlternativeSnapshot(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("after_selected")] string AfterSelected,
    [property: JsonPropertyName("selectable")] bool Selectable);

internal sealed record RelicSelectionContextSnapshot
{
    [property: JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [property: JsonPropertyName("prompt")]
    public required string Prompt { get; init; }

    [property: JsonPropertyName("skip_available")]
    public required bool SkipAvailable { get; init; }

    [property: JsonPropertyName("relics")]
    public required List<RelicSnapshot> Relics { get; init; }
}

internal sealed record RelicSnapshot
{
    [property: JsonPropertyName("id")]
    public required string Id { get; init; }

    [property: JsonPropertyName("title")]
    public required string Title { get; init; }

    [property: JsonPropertyName("description")]
    public required string Description { get; init; }

    [property: JsonPropertyName("hover_tips")]
    public required List<HoverTipSnapshot> HoverTips { get; init; }

    [property: JsonPropertyName("rarity")]
    public required string Rarity { get; init; }
}

internal sealed record RestSiteContextSnapshot
{
    [property: JsonPropertyName("options")]
    public required List<RestSiteOptionSnapshot> Options { get; init; }

    [property: JsonPropertyName("proceed_available")]
    public required bool ProceedAvailable { get; init; }
}

internal sealed record RestSiteOptionSnapshot
{
    [property: JsonPropertyName("id")]
    public required string Id { get; init; }

    [property: JsonPropertyName("title")]
    public required string Title { get; init; }

    [property: JsonPropertyName("description")]
    public required string Description { get; init; }

    [property: JsonPropertyName("enabled")]
    public required bool Enabled { get; init; }
}

internal sealed record MerchantContextSnapshot
{
    [property: JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [property: JsonPropertyName("enter_shop_available")]
    public required bool EnterShopAvailable { get; init; }

    [property: JsonPropertyName("leave_available")]
    public required bool LeaveAvailable { get; init; }

    [property: JsonPropertyName("proceed_available")]
    public required bool ProceedAvailable { get; init; }

    [property: JsonPropertyName("items")]
    public required List<MerchantItemSnapshot> Items { get; init; }
}

internal sealed record MerchantItemSnapshot
{
    [property: JsonPropertyName("id")]
    public required string Id { get; init; }

    [property: JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [property: JsonPropertyName("title")]
    public required string Title { get; init; }

    [property: JsonPropertyName("model_id")]
    public required string? ModelId { get; init; }

    [property: JsonPropertyName("description")]
    public required string Description { get; init; }

    [property: JsonPropertyName("hover_tips")]
    public required List<HoverTipSnapshot> HoverTips { get; init; }

    [property: JsonPropertyName("base_values")]
    public required Dictionary<string, decimal> BaseValues { get; init; }

    [property: JsonPropertyName("card")]
    public CardSnapshot? Card { get; init; }

    [property: JsonPropertyName("cost")]
    public required int Cost { get; init; }

    [property: JsonPropertyName("affordable")]
    public required bool Affordable { get; init; }

    [property: JsonPropertyName("purchasable")]
    public required bool Purchasable { get; init; }

    [property: JsonPropertyName("rarity")]
    public required string? Rarity { get; init; }

    [property: JsonPropertyName("on_sale")]
    public required bool? OnSale { get; init; }
}

internal sealed record RewardContextSnapshot
{
    [property: JsonPropertyName("proceed_enabled")]
    public required bool ProceedEnabled { get; init; }

    [property: JsonPropertyName("rewards")]
    public required List<RewardSnapshot> Rewards { get; init; }
}

internal sealed record TreasureContextSnapshot
{
    [property: JsonPropertyName("chest_open_available")]
    public required bool ChestOpenAvailable { get; init; }

    [property: JsonPropertyName("proceed_available")]
    public required bool ProceedAvailable { get; init; }

    [property: JsonPropertyName("relics")]
    public required List<RelicSnapshot> Relics { get; init; }
}

internal sealed record RewardSnapshot
{
    [property: JsonPropertyName("id")]
    public required string Id { get; init; }

    [property: JsonPropertyName("reward_type")]
    public required string RewardType { get; init; }

    [property: JsonPropertyName("title")]
    public required string Title { get; init; }

    [property: JsonPropertyName("description")]
    public required string Description { get; init; }

    [property: JsonPropertyName("potion")]
    public PotionSnapshot? Potion { get; init; }

    [property: JsonPropertyName("model_id")]
    public string? ModelId { get; init; }

    [property: JsonPropertyName("rarity")]
    public string? Rarity { get; init; }

    [property: JsonPropertyName("base_values")]
    public Dictionary<string, decimal>? BaseValues { get; init; }

    [property: JsonPropertyName("hover_tips")]
    public List<HoverTipSnapshot>? HoverTips { get; init; }

    [property: JsonPropertyName("unavailable_reason")]
    public string? UnavailableReason { get; init; }

    [property: JsonPropertyName("skippable")]
    public required bool Skippable { get; init; }

    [property: JsonPropertyName("selectable")]
    public required bool Selectable { get; init; }
}

internal sealed record MapContextSnapshot
{
    [property: JsonPropertyName("is_travel_enabled")]
    public required bool IsTravelEnabled { get; init; }

    [property: JsonPropertyName("is_traveling")]
    public required bool IsTraveling { get; init; }

    [property: JsonPropertyName("current_point_id")]
    public required string? CurrentPointId { get; init; }

    [property: JsonPropertyName("points")]
    public required List<MapPointSnapshot> Points { get; init; }
}

internal sealed record MapPointSnapshot
{
    [property: JsonPropertyName("id")]
    public required string Id { get; init; }

    [property: JsonPropertyName("point_type")]
    public required string PointType { get; init; }

    [property: JsonPropertyName("room_kind")]
    public required string RoomKind { get; init; }

    [property: JsonPropertyName("col")]
    public required int? Col { get; init; }

    [property: JsonPropertyName("row")]
    public required int? Row { get; init; }

    [property: JsonPropertyName("children")]
    public required List<string> Children { get; init; }

    [property: JsonPropertyName("visited")]
    public required bool Visited { get; init; }

    [property: JsonPropertyName("current")]
    public required bool Current { get; init; }

    [property: JsonPropertyName("travelable")]
    public required bool Travelable { get; init; }
}
