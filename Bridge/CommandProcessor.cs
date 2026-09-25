using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2Bridge.Bridge;

internal static class CommandProcessor
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private static string? _lastProcessedCommandId;

    public static string CommandFilePath => Path.Combine(StateExporter.StateDirectoryPath, "command.json");

    private static string ResultFilePath => Path.Combine(StateExporter.StateDirectoryPath, "command-result.json");

    public static void ProcessPendingCommand()
    {
        if (!File.Exists(CommandFilePath))
        {
            return;
        }

        string payload;
        try
        {
            payload = File.ReadAllText(CommandFilePath);
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(payload))
        {
            return;
        }

        BridgeCommand? command;
        try
        {
            command = JsonSerializer.Deserialize<BridgeCommand>(payload, JsonOptions);
        }
        catch (JsonException)
        {
            return;
        }

        if (command is null)
        {
            WriteResult(new CommandResult
            {
                CommandId = null,
                Status = "error",
                Message = "Command payload could not be parsed.",
                ProcessedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            });
            DeleteCommandFile();
            return;
        }

        if (string.IsNullOrWhiteSpace(command.CommandId))
        {
            WriteResult(Error(null, "command_id is required."));
            DeleteCommandFile();
            return;
        }

        if (command.CommandId == _lastProcessedCommandId)
        {
            DeleteCommandFile();
            return;
        }

        CommandResult result = Execute(command);
        WriteResult(result);
        _lastProcessedCommandId = command.CommandId;
        DeleteCommandFile();
    }

    private static CommandResult Execute(BridgeCommand command)
    {
        try
        {
            TraceRecorder.Record("command", command);
            if (command.ExpectedStateId is not null && !StateExporter.MatchesCurrentObservation(command.ExpectedStateId))
            {
                BridgeRuntime.RequestExport();
                return Error(command.CommandId, "Observed state is stale; read state again. Nothing was executed.");
            }
            if (command.Type == "mark")
            {
                bool recorded = TraceRecorder.Record("marker", new { command.CommandId, command.Note, screen = ScreenDiagnostics.CaptureForError() });
                BridgeRuntime.RequestExport();
                return recorded ? Success(command.CommandId, "Playtest marker recorded.")
                    : Error(command.CommandId, "Recording is unavailable; marker was not saved.");
            }
            object? screen = ActiveScreenContext.Instance.GetCurrentScreen();
            if (!ScreenDiagnostics.IsSupported(screen))
            {
                return Error(command.CommandId, $"Unsupported active screen '{screen?.GetType().Name}'; manual input required.");
            }
            return command.Type switch
            {
                "end_turn" => ExecuteEndTurn(command),
                "enter_merchant" => ExecuteEnterMerchant(command),
                "leave_shop" => ExecuteLeaveShop(command),
                "purchase_shop_item" => ExecutePurchaseShopItem(command),
                "play_card" => ExecutePlayCard(command),
                "proceed" => ExecuteProceed(command),
                "select_rest_site_option" => ExecuteSelectRestSiteOption(command),
                "open_treasure_chest" => ExecuteOpenTreasureChest(command),
                "select_relic" => ExecuteSelectRelic(command),
                "take_reward" => ExecuteTakeReward(command),
                "skip_reward" => ExecuteSkipReward(command),
                "select_map_point" => ExecuteSelectMapPoint(command),
                "select_card" => ExecuteSelectCard(command),
                "select_card_reward_alternative" => ExecuteSelectCardRewardAlternative(command),
                "confirm_card_selection" => ExecuteConfirmCardSelection(command),
                "select_choice" => ExecuteSelectChoice(command),
                "select_crystal_sphere_tool" => ExecuteCrystalSphere(command, selectTool: true),
                "reveal_crystal_sphere_cell" => ExecuteCrystalSphere(command, selectTool: false),
                "use_potion" => ExecuteUsePotion(command),
                "discard_potion" => ExecuteDiscardPotion(command),
                _ => Error(command.CommandId, $"Unsupported command type '{command.Type}'."),
            };
        }
        catch (Exception exception)
        {
            Log.Error($"STS2 Bridge command failed: {exception}");
            return Error(command.CommandId, exception.Message);
        }
    }

    private static CommandResult ExecuteEndTurn(BridgeCommand command)
    {
        BridgeContext? context = BuildContext();
        if (context is null)
        {
            return Error(command.CommandId, "Combat context unavailable.");
        }

        if (!IsWaitingForInput(context.RunManager, context.CombatManager, context.Player.PlayerCombatState))
        {
            return Error(command.CommandId, "Combat is not waiting for player input.");
        }

        Type? actionType = context.Player.GetType().Assembly.GetType("MegaCrit.Sts2.Core.GameActions.EndPlayerTurnAction");
        object? action = actionType is null
            ? null
            : Activator.CreateInstance(actionType, context.Player, context.CombatState.RoundNumber);
        if (action is null)
        {
            return Error(command.CommandId, "Could not construct EndPlayerTurnAction.");
        }

        if (!TryEnqueueAction(context.RunManager, action))
        {
            return Error(command.CommandId, "Could not enqueue EndPlayerTurnAction.");
        }

        BridgeRuntime.RequestExport();
        return Success(command.CommandId, "End turn queued.");
    }

    private static CommandResult ExecutePlayCard(BridgeCommand command)
    {
        if (string.IsNullOrEmpty(command.CardId))
        {
            return Error(command.CommandId, "play_card requires card_id.");
        }

        BridgeContext? context = BuildContext();
        if (context is null)
        {
            return Error(command.CommandId, "Combat context unavailable.");
        }

        if (!IsWaitingForInput(context.RunManager, context.CombatManager, context.Player.PlayerCombatState))
        {
            return Error(command.CommandId, "Combat is not waiting for player input.");
        }

        object? handItem = BridgeIntrospection.FindHandCardById(context.Player.PlayerCombatState, command.CardId);
        if (handItem is null)
        {
            return Error(command.CommandId, $"Card '{command.CardId}' not found in hand.");
        }

        CardModel? cardModel = BridgeIntrospection.GetCardModel(handItem);
        if (cardModel is null)
        {
            return Error(command.CommandId, "Could not resolve CardModel.");
        }

        if (!BridgeIntrospection.IsCardPlayable(handItem))
        {
            return Error(command.CommandId, $"Card '{command.CardId}' is not playable.");
        }

        object? target = BridgeIntrospection.ResolveCommandTarget(command.TargetId, context.CombatState, context.Player);
        if (!IsValidTarget(cardModel, target))
        {
            return Error(command.CommandId, $"Target '{command.TargetId ?? "<none>"}' is invalid.");
        }

        if (!TryManualPlay(cardModel, target))
        {
            return Error(command.CommandId, $"Card '{command.CardId}' could not be queued for play.");
        }

        BridgeRuntime.RequestExport();
        return Success(command.CommandId, $"Queued card '{command.CardId}'.");
    }

    private static CommandResult ExecuteEnterMerchant(BridgeCommand command)
    {
        if (!BridgeIntrospection.TryEnterMerchant())
        {
            return Error(command.CommandId, "Merchant could not be opened.");
        }

        BridgeRuntime.RequestExport();
        return Success(command.CommandId, "Merchant opened.");
    }

    private static CommandResult ExecutePurchaseShopItem(BridgeCommand command)
    {
        if (string.IsNullOrEmpty(command.ItemId))
        {
            return Error(command.CommandId, "purchase_shop_item requires item_id.");
        }

        if (!BridgeIntrospection.TryPurchaseMerchantItem(command.ItemId))
        {
            return Error(command.CommandId, $"Shop item '{command.ItemId}' could not be purchased.");
        }

        BridgeRuntime.RequestExport();
        return Success(command.CommandId, $"Purchased shop item '{command.ItemId}'.");
    }

    private static CommandResult ExecuteLeaveShop(BridgeCommand command)
    {
        if (!BridgeIntrospection.TryLeaveMerchant())
        {
            return Error(command.CommandId, "Shop could not be closed.");
        }

        BridgeRuntime.RequestExport();
        return Success(command.CommandId, "Shop closed.");
    }

    private static CommandResult ExecuteSelectChoice(BridgeCommand command)
    {
        if (string.IsNullOrEmpty(command.ChoiceId))
        {
            return Error(command.CommandId, "select_choice requires choice_id.");
        }

        RunManager? runManager = RunManager.Instance;
        RunState? runState = BridgeIntrospection.GetRunState(runManager);
        if (runState is null)
        {
            return Error(command.CommandId, "Run context unavailable.");
        }

        if (!BridgeIntrospection.TrySelectChoice(runState, command.ChoiceId))
        {
            return Error(command.CommandId, $"Choice '{command.ChoiceId}' could not be selected.");
        }

        BridgeRuntime.RequestExport();
        return Success(command.CommandId, $"Selected choice '{command.ChoiceId}'.");
    }

    private static CommandResult ExecuteSelectRestSiteOption(BridgeCommand command)
    {
        if (string.IsNullOrEmpty(command.OptionId))
        {
            return Error(command.CommandId, "select_rest_site_option requires option_id.");
        }

        RunManager? runManager = RunManager.Instance;
        RunState? runState = BridgeIntrospection.GetRunState(runManager);
        if (runState is null)
        {
            return Error(command.CommandId, "Run context unavailable.");
        }

        if (!BridgeIntrospection.TrySelectRestSiteOption(runState, command.OptionId))
        {
            return Error(command.CommandId, $"Rest site option '{command.OptionId}' could not be selected.");
        }

        BridgeRuntime.RequestExport();
        return Success(command.CommandId, $"Selected rest site option '{command.OptionId}'.");
    }

    private static CommandResult ExecuteSelectCard(BridgeCommand command)
    {
        if (string.IsNullOrEmpty(command.CardId))
        {
            return Error(command.CommandId, "select_card requires card_id.");
        }

        if (!BridgeIntrospection.TrySelectCard(command.CardId))
        {
            return Error(command.CommandId, $"Card '{command.CardId}' could not be selected.");
        }

        BridgeRuntime.RequestExport();
        return Success(command.CommandId, $"Selected card '{command.CardId}'.");
    }

    private static CommandResult ExecuteSelectCardRewardAlternative(BridgeCommand command)
    {
        if (string.IsNullOrEmpty(command.OptionId))
        {
            return Error(command.CommandId, "select_card_reward_alternative requires option_id.");
        }

        if (!BridgeIntrospection.TrySelectCardRewardAlternative(command.OptionId))
        {
            return Error(command.CommandId, $"Card reward alternative '{command.OptionId}' is not available.");
        }

        BridgeRuntime.RequestExport();
        return Success(command.CommandId, $"Selected card reward alternative '{command.OptionId}'.");
    }

    private static CommandResult ExecuteConfirmCardSelection(BridgeCommand command)
    {
        if (!BridgeIntrospection.TryConfirmCardSelection())
        {
            return Error(command.CommandId, "Card selection confirmation is not available.");
        }

        BridgeRuntime.RequestExport();
        return Success(command.CommandId, "Card selection confirmed.");
    }

    private static CommandResult ExecuteCrystalSphere(BridgeCommand command, bool selectTool)
    {
        string? id = selectTool ? command.ToolId : command.CellId;
        if (string.IsNullOrEmpty(id))
            return Error(command.CommandId, $"{command.Type} requires {(selectTool ? "tool_id" : "cell_id")}.");
        bool accepted = selectTool ? CrystalSphereAdapter.TrySelectTool(id) : CrystalSphereAdapter.TryRevealCell(id);
        if (!accepted)
            return Error(command.CommandId, $"Crystal Sphere {(selectTool ? "tool" : "cell")} '{id}' is not available.");
        BridgeRuntime.RequestExport();
        return Success(command.CommandId, $"Crystal Sphere {(selectTool ? "tool selected" : "reveal started")}.");
    }

    private static CommandResult ExecuteProceed(BridgeCommand command)
    {
        RunManager? runManager = RunManager.Instance;
        RunState? runState = BridgeIntrospection.GetRunState(runManager);
        if (runState is null)
        {
            return Error(command.CommandId, "Run context unavailable.");
        }

        if (!CrystalSphereAdapter.TryProceed() && !BridgeIntrospection.TryProceed(runState))
        {
            return Error(command.CommandId, "Proceed is not available.");
        }

        BridgeRuntime.RequestExport();
        return Success(command.CommandId, "Proceed triggered.");
    }

    private static CommandResult ExecuteOpenTreasureChest(BridgeCommand command)
    {
        if (!BridgeIntrospection.TryOpenTreasureChest())
        {
            return Error(command.CommandId, "Treasure chest could not be opened.");
        }

        BridgeRuntime.RequestExport();
        return Success(command.CommandId, "Treasure chest opened.");
    }

    private static CommandResult ExecuteSelectRelic(BridgeCommand command)
    {
        if (string.IsNullOrEmpty(command.RelicId))
        {
            return Error(command.CommandId, "select_relic requires relic_id.");
        }

        if (!BridgeIntrospection.TrySelectRelic(command.RelicId))
        {
            return Error(command.CommandId, $"Relic '{command.RelicId}' could not be selected.");
        }

        BridgeRuntime.RequestExport();
        return Success(command.CommandId, $"Selected relic '{command.RelicId}'.");
    }

    private static CommandResult ExecuteTakeReward(BridgeCommand command)
    {
        if (string.IsNullOrEmpty(command.RewardId))
        {
            return Error(command.CommandId, "take_reward requires reward_id.");
        }

        if (!BridgeIntrospection.TryTakeReward(command.RewardId))
        {
            return Error(command.CommandId, $"Reward '{command.RewardId}' could not be taken.");
        }

        BridgeRuntime.RequestExport();
        return Success(command.CommandId, $"Took reward '{command.RewardId}'.");
    }

    private static CommandResult ExecuteSkipReward(BridgeCommand command)
    {
        if (string.IsNullOrEmpty(command.RewardId))
        {
            return Error(command.CommandId, "skip_reward requires reward_id.");
        }

        if (!BridgeIntrospection.TrySkipReward(command.RewardId))
        {
            return Error(command.CommandId, $"Reward '{command.RewardId}' could not be skipped. It must be the last remaining reward with Skip enabled; collect other loot first, or use proceed to leave all remaining rewards.");
        }

        BridgeRuntime.RequestExport();
        return Success(command.CommandId, $"Skipped reward '{command.RewardId}'.");
    }

    private static CommandResult ExecuteSelectMapPoint(BridgeCommand command)
    {
        if (string.IsNullOrEmpty(command.PointId))
        {
            return Error(command.CommandId, "select_map_point requires point_id.");
        }

        RunManager? runManager = RunManager.Instance;
        RunState? runState = BridgeIntrospection.GetRunState(runManager);
        if (runState is null)
        {
            return Error(command.CommandId, "Run context unavailable.");
        }

        if (!BridgeIntrospection.TrySelectMapPoint(runState, command.PointId))
        {
            return Error(command.CommandId, $"Map point '{command.PointId}' could not be selected.");
        }

        BridgeRuntime.RequestExport();
        return Success(command.CommandId, $"Selected map point '{command.PointId}'.");
    }

    private static CommandResult ExecuteDiscardPotion(BridgeCommand command)
    {
        if (string.IsNullOrEmpty(command.PotionId))
        {
            return Error(command.CommandId, "discard_potion requires potion_id.");
        }

        RunState? runState = BridgeIntrospection.GetRunState(RunManager.Instance);
        Player? player = BridgeIntrospection.GetPrimaryPlayer(BridgeIntrospection.GetCombatState(runState), runState);
        if (player is null || !BridgeIntrospection.TryDiscardPotion(player, command.PotionId))
        {
            return Error(command.CommandId, $"Potion '{command.PotionId}' could not be discarded.");
        }

        BridgeRuntime.RequestExport();
        return Success(command.CommandId, $"Queued discard of potion '{command.PotionId}'. Wait for the belt to update before taking a replacement.");
    }

    private static CommandResult ExecuteUsePotion(BridgeCommand command)
    {
        BridgeContext? context = BuildContext();
        if (context is null)
        {
            return Error(command.CommandId, "Combat context unavailable.");
        }

        if (!IsWaitingForInput(context.RunManager, context.CombatManager, context.Player.PlayerCombatState))
        {
            return Error(command.CommandId, "Combat is not waiting for player input.");
        }

        if (string.IsNullOrEmpty(command.PotionId))
        {
            return Error(command.CommandId, "use_potion requires potion_id.");
        }

        if (!BridgeIntrospection.TryUsePotion(context.Player, context.CombatState, command.PotionId, command.TargetId))
        {
            return Error(command.CommandId, $"Potion '{command.PotionId}' could not be used.");
        }

        BridgeRuntime.RequestExport();
        return Success(command.CommandId, $"Used potion '{command.PotionId}'.");
    }

    private static BridgeContext? BuildContext()
    {
        RunManager? runManager = RunManager.Instance;
        CombatManager? combatManager = CombatManager.Instance;
        RunState? runState = BridgeIntrospection.GetRunState(runManager);
        CombatState? combatState = BridgeIntrospection.GetCombatState(runState);
        Player? player = BridgeIntrospection.GetPrimaryPlayer(combatState, runState);

        if (runManager is null || combatManager is null || combatState is null || player is null)
        {
            return null;
        }

        return new BridgeContext
        {
            RunManager = runManager,
            CombatManager = combatManager,
            CombatState = combatState,
            Player = player,
        };
    }

    private static bool IsWaitingForInput(RunManager runManager, CombatManager combatManager, PlayerCombatState? playerCombatState)
    {
        if (ActiveScreenContext.Instance.GetCurrentScreen() is not NCombatRoom
            || BridgeIntrospection.HasPendingHandSelection
            || !combatManager.IsInProgress || playerCombatState?.Phase != PlayerTurnPhase.Play || combatManager.PlayerActionsDisabled)
        {
            return false;
        }

        return runManager.ActionExecutor is not { IsRunning: true, CurrentlyRunningAction: not null };
    }

    private static bool TryEnqueueAction(RunManager runManager, object action)
    {
        object? synchronizer = runManager.GetType().GetProperty("ActionQueueSynchronizer")?.GetValue(runManager);
        MethodInfo? requestEnqueue = synchronizer?.GetType().GetMethod("RequestEnqueue", BindingFlags.Instance | BindingFlags.Public);
        if (synchronizer is null || requestEnqueue is null)
        {
            return false;
        }

        requestEnqueue.Invoke(synchronizer, new[] { action });
        return true;
    }

    private static bool TryManualPlay(CardModel cardModel, object? target)
    {
        MethodInfo? method = cardModel.GetType().GetMethod("TryManualPlay", BindingFlags.Instance | BindingFlags.Public);
        if (method?.Invoke(cardModel, new[] { target }) is bool played)
        {
            return played;
        }

        return false;
    }

    private static bool IsValidTarget(CardModel cardModel, object? target)
    {
        MethodInfo? method = cardModel.GetType().GetMethod("IsValidTarget", BindingFlags.Instance | BindingFlags.Public);
        if (method is null)
        {
            return target is null;
        }

        if (method.Invoke(cardModel, new[] { target }) is bool valid)
        {
            return valid;
        }

        return target is null;
    }

    private static void WriteResult(CommandResult result)
    {
        Directory.CreateDirectory(StateExporter.StateDirectoryPath);
        string payload = JsonSerializer.Serialize(result, JsonOptions);
        File.WriteAllText(ResultFilePath + ".tmp", payload);
        File.Move(ResultFilePath + ".tmp", ResultFilePath, true);
        TraceRecorder.Record("command_result", result);
    }

    private static void DeleteCommandFile()
    {
        try
        {
            if (File.Exists(CommandFilePath))
            {
                File.Delete(CommandFilePath);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static CommandResult Success(string? commandId, string message)
    {
        TraceRecorder.Log("command.success", ("command_id", commandId), ("message", message));
        return new CommandResult
        {
            CommandId = commandId,
            Status = "ok",
            Message = message,
            ProcessedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        };
    }

    private static CommandResult Error(string? commandId, string message)
    {
        TraceRecorder.Log("command.error", ("command_id", commandId), ("message", message));
        return new CommandResult
        {
            CommandId = commandId,
            Status = "error",
            Message = message,
            ProcessedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        };
    }
}

internal sealed record BridgeCommand
{
    [property: JsonPropertyName("command_id")]
    public required string? CommandId { get; init; }

    [property: JsonPropertyName("type")]
    public required string Type { get; init; }

    [property: JsonPropertyName("expected_state_id")]
    public string? ExpectedStateId { get; init; }

    [property: JsonPropertyName("note")]
    public string? Note { get; init; }

    [property: JsonPropertyName("card_id")]
    public string? CardId { get; init; }

    [property: JsonPropertyName("target_id")]
    public string? TargetId { get; init; }

    [property: JsonPropertyName("choice_id")]
    public string? ChoiceId { get; init; }

    [property: JsonPropertyName("point_id")]
    public string? PointId { get; init; }

    [property: JsonPropertyName("reward_id")]
    public string? RewardId { get; init; }

    [property: JsonPropertyName("item_id")]
    public string? ItemId { get; init; }

    [property: JsonPropertyName("option_id")]
    public string? OptionId { get; init; }

    [property: JsonPropertyName("relic_id")]
    public string? RelicId { get; init; }

    [property: JsonPropertyName("potion_id")]
    public string? PotionId { get; init; }

    [property: JsonPropertyName("tool_id")]
    public string? ToolId { get; init; }

    [property: JsonPropertyName("cell_id")]
    public string? CellId { get; init; }
}

internal sealed record CommandResult
{
    [property: JsonPropertyName("command_id")]
    public required string? CommandId { get; init; }

    [property: JsonPropertyName("status")]
    public required string Status { get; init; }

    [property: JsonPropertyName("message")]
    public required string Message { get; init; }

    [property: JsonPropertyName("processed_at")]
    public required string ProcessedAt { get; init; }
}

internal sealed record BridgeContext
{
    public required RunManager RunManager { get; init; }
    public required CombatManager CombatManager { get; init; }
    public required CombatState CombatState { get; init; }
    public required Player Player { get; init; }
}
