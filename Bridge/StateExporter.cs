using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace FirstMod.Bridge;

internal static class StateExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    public static string StateFilePath => Path.Combine(StateDirectoryPath, "state.json");

    private static string StateDirectoryPath => ProjectSettings.GlobalizePath("user://first-mod-bridge");

    public static StableBridgeSnapshot BuildStableSnapshot()
    {
        RunManager? runManager = RunManager.Instance;
        CombatManager? combatManager = CombatManager.Instance;
        RunState? runState = GetRunState(runManager);
        CombatState? combatState = GetCombatState(runState);
        Player? player = GetPrimaryPlayer(combatState, runState);
        PlayerCombatState? playerCombatState = player?.PlayerCombatState;

        return new StableBridgeSnapshot
        {
            ProtocolVersion = 1,
            Scene = DetermineScene(runManager, combatManager, runState, combatState),
            WaitingForInput = DetermineWaitingForInput(runManager, combatManager),
            Run = BuildRunSnapshot(runState),
            Player = BuildPlayerSnapshot(player),
            Enemies = BuildEnemySnapshots(combatState),
            Hand = BuildHandSnapshots(playerCombatState),
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
        BridgeSnapshot snapshot = new()
        {
            ProtocolVersion = stableSnapshot.ProtocolVersion,
            StateId = $"state_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}",
            Timestamp = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            Scene = stableSnapshot.Scene,
            WaitingForInput = stableSnapshot.WaitingForInput,
            Run = stableSnapshot.Run,
            Player = stableSnapshot.Player,
            Enemies = stableSnapshot.Enemies,
            Hand = stableSnapshot.Hand,
            DrawPileCount = stableSnapshot.DrawPileCount,
            DiscardPileCount = stableSnapshot.DiscardPileCount,
            ExhaustPileCount = stableSnapshot.ExhaustPileCount,
            PlayPileCount = stableSnapshot.PlayPileCount,
        };

        return JsonSerializer.Serialize(snapshot, JsonOptions);
    }

    public static void WriteStateJson(string stateJson)
    {
        Directory.CreateDirectory(StateDirectoryPath);

        string tempFilePath = Path.Combine(StateDirectoryPath, "state.json.tmp");
        File.WriteAllText(tempFilePath, stateJson);
        File.Copy(tempFilePath, StateFilePath, true);
        File.Delete(tempFilePath);
    }

    private static string DetermineScene(
        RunManager? runManager,
        CombatManager? combatManager,
        RunState? runState,
        CombatState? combatState)
    {
        if (combatManager?.IsInProgress == true && combatState is not null)
        {
            return "combat";
        }

        if (runManager?.IsInProgress == true && runState?.CurrentRoom is not null)
        {
            return runState.CurrentRoom.RoomType.ToString();
        }

        return "main_menu";
    }

    private static bool DetermineWaitingForInput(RunManager? runManager, CombatManager? combatManager)
    {
        if (combatManager is null)
        {
            return false;
        }

        if (!combatManager.IsInProgress || !combatManager.IsPlayPhase || combatManager.PlayerActionsDisabled)
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

        return new PlayerSnapshot
        {
            Hp = creature?.CurrentHp,
            MaxHp = creature?.MaxHp,
            Block = creature?.Block,
            Gold = player.Gold,
            Energy = combatState?.Energy,
            MaxEnergy = combatState?.MaxEnergy ?? player.MaxEnergy,
            DeckCount = GetPileCount(player.Deck),
            RelicCount = CountEntries(player.Relics),
            PotionCount = CountEntries(player.Potions),
        };
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
                Id = string.IsNullOrEmpty(enemy.SlotName) ? $"enemy_{index.ToString(CultureInfo.InvariantCulture)}" : enemy.SlotName,
                Name = enemy.Name,
                Hp = enemy.CurrentHp,
                MaxHp = enemy.MaxHp,
                Block = enemy.Block,
                IsHittable = enemy.IsHittable,
            });
            index += 1;
        }

        return enemies;
    }

    private static List<CardSnapshot> BuildHandSnapshots(PlayerCombatState? combatState)
    {
        List<CardSnapshot> cards = [];
        if (combatState?.Hand is null)
        {
            return cards;
        }

        int index = 0;
        foreach (object? card in combatState.Hand.Cards)
        {
            if (card is null)
            {
                continue;
            }

            cards.Add(new CardSnapshot
            {
                Id = $"hand_{index.ToString(CultureInfo.InvariantCulture)}",
                Name = GetStringProperty(card, "Title"),
                Description = GetStringProperty(card, "Description"),
                Cost = GetPropertyText(card, "EnergyCost"),
                Playable = GetBoolProperty(card, "IsPlayable"),
            });
            index += 1;
        }

        return cards;
    }

    private static CombatState? GetCombatState(RunState? runState)
    {
        if (runState?.CurrentRoom is CombatRoom combatRoom)
        {
            return combatRoom.CombatState;
        }

        return null;
    }

    private static RunState? GetRunState(RunManager? runManager)
    {
        if (runManager is null)
        {
            return null;
        }

        object? reflectedState = runManager.GetType().GetProperty("State")?.GetValue(runManager);
        if (reflectedState is RunState runState)
        {
            return runState;
        }

        MethodInfo? debugStateMethod = runManager.GetType().GetMethod(
            "DebugOnlyGetState",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (debugStateMethod?.Invoke(runManager, null) is RunState debugState)
        {
            return debugState;
        }

        return null;
    }

    private static Player? GetPrimaryPlayer(CombatState? combatState, RunState? runState)
    {
        if (combatState?.Players is not null)
        {
            foreach (Player player in combatState.Players)
            {
                return player;
            }
        }

        if (runState?.Players is not null)
        {
            foreach (Player player in runState.Players)
            {
                return player;
            }
        }

        return null;
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

    private static string GetStringProperty(object source, string propertyName)
    {
        PropertyInfo? property = source.GetType().GetProperty(propertyName);
        if (property?.GetValue(source) is string value)
        {
            return value;
        }

        return string.Empty;
    }

    private static string GetPropertyText(object source, string propertyName)
    {
        PropertyInfo? property = source.GetType().GetProperty(propertyName);
        object? value = property?.GetValue(source);
        return value?.ToString() ?? string.Empty;
    }

    private static bool GetBoolProperty(object source, string propertyName)
    {
        PropertyInfo? property = source.GetType().GetProperty(propertyName);
        if (property?.GetValue(source) is bool value)
        {
            return value;
        }

        return false;
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

    [property: JsonPropertyName("run")]
    public required RunSnapshot? Run { get; init; }

    [property: JsonPropertyName("player")]
    public required PlayerSnapshot? Player { get; init; }

    [property: JsonPropertyName("enemies")]
    public required List<EnemySnapshot> Enemies { get; init; }

    [property: JsonPropertyName("hand")]
    public required List<CardSnapshot> Hand { get; init; }

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

    [property: JsonPropertyName("state_id")]
    public required string StateId { get; init; }

    [property: JsonPropertyName("timestamp")]
    public required string Timestamp { get; init; }

    [property: JsonPropertyName("scene")]
    public required string Scene { get; init; }

    [property: JsonPropertyName("waiting_for_input")]
    public required bool WaitingForInput { get; init; }

    [property: JsonPropertyName("run")]
    public required RunSnapshot? Run { get; init; }

    [property: JsonPropertyName("player")]
    public required PlayerSnapshot? Player { get; init; }

    [property: JsonPropertyName("enemies")]
    public required List<EnemySnapshot> Enemies { get; init; }

    [property: JsonPropertyName("hand")]
    public required List<CardSnapshot> Hand { get; init; }

    [property: JsonPropertyName("draw_pile_count")]
    public required int DrawPileCount { get; init; }

    [property: JsonPropertyName("discard_pile_count")]
    public required int DiscardPileCount { get; init; }

    [property: JsonPropertyName("exhaust_pile_count")]
    public required int ExhaustPileCount { get; init; }

    [property: JsonPropertyName("play_pile_count")]
    public required int PlayPileCount { get; init; }
}

internal sealed record RunSnapshot
{
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
}

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
}

internal sealed record CardSnapshot
{
    [property: JsonPropertyName("id")]
    public required string Id { get; init; }

    [property: JsonPropertyName("name")]
    public required string Name { get; init; }

    [property: JsonPropertyName("description")]
    public required string Description { get; init; }

    [property: JsonPropertyName("cost")]
    public required string Cost { get; init; }

    [property: JsonPropertyName("playable")]
    public required bool Playable { get; init; }
}
