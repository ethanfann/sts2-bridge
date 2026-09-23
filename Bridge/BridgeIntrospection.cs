using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Events.Custom;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Potions;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.RestSite;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2Bridge.Bridge;

internal static class BridgeIntrospection
{
    private sealed record PotionIdentity(string Id);
    private static readonly ConditionalWeakTable<PotionModel, PotionIdentity> PotionIdentities = new();

    public static RunState? GetRunState(RunManager? runManager)
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

    public static CombatState? GetCombatState(RunState? runState)
    {
        object? currentRoom = GetCurrentRoom(runState);
        if (currentRoom is null)
        {
            return null;
        }

        object? combatState = currentRoom.GetType().GetProperty("CombatState")?.GetValue(currentRoom);
        return combatState as CombatState;
    }

    public static object? GetCurrentRoom(RunState? runState)
    {
        return runState?.CurrentRoom;
    }

    public static object? GetEventModel(RunState? runState)
    {
        object? mutableEvent = GetMutableEventModel(runState);
        if (mutableEvent is not null)
        {
            return mutableEvent;
        }

        object? currentRoom = GetCurrentRoom(runState);
        if (currentRoom is null)
        {
            return null;
        }

        foreach (string propertyName in new[] { "CanonicalEvent", "Event" })
        {
            try
            {
                object? value = currentRoom.GetType().GetProperty(propertyName)?.GetValue(currentRoom);
                if (value is not null)
                {
                    return value;
                }
            }
            catch
            {
            }
        }

        return null;
    }

    private static object? GetMutableEventModel(RunState? runState)
    {
        object? currentRoom = runState?.BaseRoom ?? GetCurrentRoom(runState);
        if (currentRoom is null)
        {
            return null;
        }

        foreach (string propertyName in new[] { "LocalMutableEvent", "MutableEvent" })
        {
            try
            {
                object? value = currentRoom.GetType().GetProperty(propertyName)?.GetValue(currentRoom);
                if (value is not null)
                {
                    return value;
                }
            }
            catch
            {
            }
        }

        return null;
    }

    public static string DetermineNonCombatScene(RunState? runState)
    {
        CardSelectionContextSnapshot? cardSelection = BuildCardSelectionContext();
        if (cardSelection is not null)
        {
            return "card_selection";
        }

        RelicSelectionContextSnapshot? relicSelection = BuildRelicSelectionContext();
        if (relicSelection is not null)
        {
            return "relic_selection";
        }

        RewardContextSnapshot? rewards = BuildRewardContext();
        if (rewards is not null)
        {
            return "rewards";
        }

        TreasureContextSnapshot? treasure = BuildTreasureContext();
        if (treasure is not null)
        {
            return "treasure";
        }

        RestSiteContextSnapshot? restSite = BuildRestSiteContext(runState);
        if (restSite is not null)
        {
            return "rest_site";
        }

        MerchantContextSnapshot? merchant = BuildMerchantContext();
        if (merchant is not null)
        {
            return merchant.Kind == "inventory" ? "shop" : "merchant_room";
        }

        if (HasActivePostCombatProceed(runState))
        {
            return "post_combat";
        }

        MapContextSnapshot? map = BuildMapContext(runState);
        if (map is not null)
        {
            return "map";
        }

        object? eventModel = GetEventModel(runState);
        if (eventModel is not null)
        {
            return eventModel.GetType().Name switch
            {
                "Neow" => "neow",
                _ => "event",
            };
        }

        object? currentRoom = GetCurrentRoom(runState);
        if (currentRoom is null)
        {
            return "main_menu";
        }

        object? roomType = currentRoom.GetType().GetProperty("RoomType")?.GetValue(currentRoom);
        return roomType?.ToString() ?? currentRoom.GetType().Name;
    }

    public static List<ChoiceSnapshot> BuildChoiceSnapshots(RunState? runState)
    {
        List<ChoiceSnapshot> choices = [];
        if (ActiveScreenContext.Instance.GetCurrentScreen() is not NEventRoom
            || GetEventModel(runState) is EventModel { IsFinished: true })
        {
            return choices;
        }
        IEnumerable? currentOptions = GetCurrentOptions(runState);
        if (currentOptions is null)
        {
            return choices;
        }

        int index = 0;
        foreach (object? option in currentOptions)
        {
            if (option is null)
            {
                index += 1;
                continue;
            }

            choices.Add(new ChoiceSnapshot
            {
                Id = BuildChoiceId(option, index),
                Title = GetOptionTitle(option),
                Description = GetOptionDescription(option),
                HoverTips = option is EventOption eventOption ? HoverTipExporter.Build(eventOption.HoverTips) : [],
                RelicModelId = (option as EventOption)?.Relic?.Id.Entry,
                Locked = GetBoolProperty(option, "IsLocked"),
                Proceed = GetBoolProperty(option, "IsProceed"),
                WillKillPlayer = option is EventOption typedOption
                    && GetPrimaryPlayer(null, runState) is Player owner
                    && typedOption.WillKillPlayer?.Invoke(owner) == true,
            });
            index += 1;
        }

        return choices;
    }

    public static CardSelectionContextSnapshot? BuildCardSelectionContext()
    {
        object? screen = GetActiveCardSelectionScreen();
        if (screen is null)
        {
            return null;
        }

        if (screen is NPlayerHand hand)
        {
            CardSelectorPrefs handPrefs = (CardSelectorPrefs)GetFieldValue(hand, "_prefs")!;
            List<CardModel> selected = (List<CardModel>)GetFieldValue(hand, "_selectedCards")!;
            return new CardSelectionContextSnapshot
            {
                Kind = hand.CurrentMode == NPlayerHand.Mode.UpgradeSelect ? "combat_hand_upgrade" : "combat_hand",
                SourcePile = "hand",
                Prompt = handPrefs.Prompt.GetFormattedText(),
                MinSelect = handPrefs.MinSelect,
                MaxSelect = handPrefs.MaxSelect,
                // NPlayerHand never auto-submits on click, regardless of prefs.
                RequireManualConfirmation = true,
                Cancelable = false,
                SelectedCardIds = selected.Select(CardStateExporter.Id).ToList(),
                ConfirmAvailable = GetHandConfirmButton(hand) is not null,
                Cards = GetCardSelectionItems(hand).Cast<CardModel>().Select(card =>
                    CardStateExporter.BuildCard(card) with
                    {
                        Playable = selected.Contains(card) || selected.Count < handPrefs.MaxSelect,
                        UpgradePreview = hand.CurrentMode == NPlayerHand.Mode.UpgradeSelect
                            ? CardStateExporter.BuildUpgradePreview(card) : null,
                    }).ToList(),
            };
        }

        if (screen is NCombatPileCardSelectScreen pileScreen)
        {
            CardSelectorPrefs pilePrefs = (CardSelectorPrefs)GetFieldValue(pileScreen, "_prefs")!;
            HashSet<CardModel> selected = (HashSet<CardModel>)GetFieldValue(pileScreen, "_selectedCards")!;
            CardPile pile = (CardPile)GetFieldValue(pileScreen, "_pile")!;
            return new CardSelectionContextSnapshot
            {
                Kind = "combat_pile",
                SourcePile = pile.Type.ToString().ToLowerInvariant(),
                Prompt = pilePrefs.Prompt.GetFormattedText(),
                MinSelect = pilePrefs.MinSelect,
                MaxSelect = pilePrefs.MaxSelect,
                RequireManualConfirmation = pilePrefs.RequireManualConfirmation,
                Cancelable = false, // This screen has no cancel action, even if prefs requests one.
                SelectedCardIds = selected.Select(CardStateExporter.Id).Order(StringComparer.Ordinal).ToList(),
                ConfirmAvailable = GetCombatPileConfirmButton(pileScreen) is not null,
                Cards = GetCardSelectionItems(pileScreen).Cast<CardModel>().Select(card =>
                    CardStateExporter.BuildCard(card) with
                    {
                        // Clicking an already-selected card toggles it off, even at the limit.
                        Playable = selected.Contains(card) || selected.Count < pilePrefs.MaxSelect,
                    }).ToList(),
            };
        }

        if (screen is NCardGridSelectionScreen gridScreen)
        {
            CardSelectorPrefs gridPrefs = (CardSelectorPrefs)GetFieldValue(screen, "_prefs")!;
            HashSet<CardModel> selected = (HashSet<CardModel>)GetFieldValue(screen, "_selectedCards")!;
            bool interactive = IsSelectionGridInteractive(gridScreen);
            return new CardSelectionContextSnapshot
            {
                Kind = GetCardSelectionKind(screen),
                Prompt = gridPrefs.Prompt.GetFormattedText(),
                MinSelect = gridPrefs.MinSelect,
                MaxSelect = gridPrefs.MaxSelect,
                // Deck screens always open a confirmation preview at the limit,
                // even when RequireManualConfirmation is false (e.g. The Trial).
                RequireManualConfirmation = screen is not NSimpleCardSelectScreen || gridPrefs.RequireManualConfirmation,
                Cancelable = gridPrefs.Cancelable,
                SelectedCardIds = selected.Select(CardStateExporter.Id).Order(StringComparer.Ordinal).ToList(),
                ConfirmAvailable = GetGridConfirmButton(gridScreen) is not null,
                Cards = GetCardSelectionItems(screen).Cast<CardModel>().Select(card =>
                    CardStateExporter.BuildCard(card) with
                    {
                        Playable = interactive && (selected.Contains(card) || selected.Count < gridPrefs.MaxSelect),
                        UpgradePreview = screen is NDeckUpgradeSelectScreen
                            ? CardStateExporter.BuildUpgradePreview(card) : null,
                    }).ToList(),
            };
        }

        IEnumerable screenCards = GetCardSelectionItems(screen);
        List<CardSnapshot> cards = [];
        int index = 0;
        foreach (object? screenCard in screenCards)
        {
            if (screenCard is null || GetCardModel(screenCard) is not CardModel card)
            {
                index += 1;
                continue;
            }

            cards.Add(CardStateExporter.BuildCard(card) with
            {
                Playable = true,
                UpgradePreview = screen is NDeckUpgradeSelectScreen
                    ? CardStateExporter.BuildUpgradePreview(card) : null,
            });
            index += 1;
        }

        object? prefs = GetFieldValue(screen, "_prefs");
        string kind = GetCardSelectionKind(screen);
        return new CardSelectionContextSnapshot
        {
            Kind = kind,
            Prompt = GetCardSelectionPrompt(screen, prefs, cards),
            MinSelect = GetCardSelectionMinSelect(kind, prefs),
            MaxSelect = GetCardSelectionMaxSelect(kind, prefs),
            RequireManualConfirmation = GetBoolPropertyValue(prefs, "RequireManualConfirmation"),
            Cancelable = GetBoolPropertyValue(prefs, "Cancelable"),
            Alternatives = screen is NCardRewardSelectionScreen rewardScreen
                ? GetCardRewardAlternatives(rewardScreen).Select(entry => new CardRewardAlternativeSnapshot(
                    BuildCardRewardAlternativeId(entry.Button), entry.Option.OptionId,
                    entry.Option.Title.GetFormattedText(), entry.Option.AfterSelected.ToString(),
                    CanSelectCardRewardAlternative(rewardScreen, entry.Button))).ToList() : null,
            Cards = cards,
        };
    }

    private static IEnumerable<(CardRewardAlternative Option, NCardRewardAlternativeButton Button)>
        GetCardRewardAlternatives(NCardRewardSelectionScreen screen)
    {
        // Read the options already generated for this screen. Generate() runs
        // model hooks and must never be called by an observation or a command.
        if (GetFieldValue(screen, "_extraOptions") is not IReadOnlyList<CardRewardAlternative> options
            || GetFieldValue(screen, "_rewardAlternativesContainer") is not Node container)
            yield break;
        var buttons = container.GetChildren().OfType<NCardRewardAlternativeButton>()
            .Where(button => !button.IsQueuedForDeletion()).ToArray();
        if (buttons.Length != options.Count)
            yield break;
        for (int i = 0; i < buttons.Length; i++)
        {
            if (IsNodeVisible(buttons[i]))
                yield return (options[i], buttons[i]);
        }
    }

    private static string BuildCardRewardAlternativeId(NCardRewardAlternativeButton button) =>
        $"card_reward_alternative_{button.GetInstanceId().ToString(CultureInfo.InvariantCulture)}";

    private static bool CanSelectCardRewardAlternative(NCardRewardSelectionScreen screen, NCardRewardAlternativeButton button) =>
        ActiveScreenContext.Instance.IsCurrent(screen) && !screen.IsQueuedForDeletion()
        && button.IsEnabled && !button.IsQueuedForDeletion() && IsNodeVisible(button)
        && GetFieldValue(screen, "_completionSource") is TaskCompletionSource<int?> completion
        && !completion.Task.IsCompleted;

    public static bool TrySelectCardRewardAlternative(string optionId)
    {
        if (GetActiveCardSelectionScreen() is not NCardRewardSelectionScreen screen)
            return false;
        foreach (var entry in GetCardRewardAlternatives(screen))
        {
            if (BuildCardRewardAlternativeId(entry.Button) != optionId)
                continue;
            if (!CanSelectCardRewardAlternative(screen, entry.Button))
                return false;
            // The connected native callback completes the pending selection.
            // CardReward then synchronizes the choice, runs its effect, and
            // decides whether to close, consume, or refresh the reward.
            entry.Button.EmitSignal(NClickableControl.SignalName.Released, entry.Button);
            return true;
        }
        return false;
    }

    public static RelicSelectionContextSnapshot? BuildRelicSelectionContext()
    {
        NChooseARelicSelection? screen = GetActiveRelicSelectionScreen();
        if (screen is null)
        {
            return null;
        }

        List<RelicSnapshot> relics = BuildRelicSelectionRelics(screen);
        if (relics.Count == 0)
        {
            return null;
        }

        return new RelicSelectionContextSnapshot
        {
            Kind = "choose_a_relic",
            Prompt = "Choose a relic",
            SkipAvailable = GetFieldValue(screen, "_skipButton") is Node skipButton && IsNodeVisible(skipButton),
            Relics = relics,
        };
    }

    public static RestSiteContextSnapshot? BuildRestSiteContext(RunState? runState)
    {
        NRestSiteRoom? roomNode = GetActiveRestSiteRoom();
        if (roomNode is null || !IsNodeVisible(roomNode))
        {
            return null;
        }

        object? room = GetCurrentRoom(runState) ?? GetFieldValue(roomNode, "_room");
        IEnumerable? options = GetMemberValue(room, "Options") as IEnumerable;
        List<RestSiteOptionSnapshot> snapshots = [];
        if (options is not null)
        {
            foreach (object? option in options)
            {
                if (option is not RestSiteOption restOption)
                {
                    continue;
                }

                snapshots.Add(new RestSiteOptionSnapshot
                {
                    Id = BuildRestSiteOptionId(restOption),
                    Title = GetLocalizedText(restOption.Title),
                    Description = restOption.Description.GetFormattedText(),
                    Enabled = restOption.IsEnabled,
                });
            }
        }

        bool proceedAvailable = roomNode.ProceedButton is Node proceedButton && IsNodeVisible(proceedButton);
        if (snapshots.Count == 0 && !proceedAvailable)
        {
            return null;
        }

        return new RestSiteContextSnapshot
        {
            Options = snapshots,
            ProceedAvailable = proceedAvailable,
        };
    }

    public static TreasureContextSnapshot? BuildTreasureContext()
    {
        NTreasureRoom? treasureRoom = GetActiveTreasureRoom();
        if (treasureRoom is null)
        {
            return null;
        }

        bool chestOpenAvailable = IsTreasureChestOpenAvailable(treasureRoom);
        bool proceedAvailable = treasureRoom.ProceedButton.IsEnabled && IsNodeVisible(treasureRoom.ProceedButton);
        return new TreasureContextSnapshot
        {
            ChestOpenAvailable = chestOpenAvailable,
            ProceedAvailable = proceedAvailable,
            Relics = BuildTreasureRelics(treasureRoom),
        };
    }

    public static List<PotionSnapshot> BuildPotionSnapshots(Player? player)
    {
        return player?.Potions.Select(potion => BuildPotionSnapshot(potion, player)).ToList() ?? [];
    }

    private static PotionSnapshot BuildPotionSnapshot(PotionModel potion, Player? owner = null) => new()
    {
        // Offered potions are previews, not actionable inventory instances.
        Id = owner is null ? null : BuildPotionId(potion),
        ModelId = potion.Id.Entry,
        SlotIndex = owner?.GetPotionSlotIndex(potion),
        DiscardAvailable = owner is not null && CanDiscardPotion(owner, potion),
        Title = GetLocalizedText(potion.Title),
        Description = GetLocalizedText(potion.DynamicDescription),
        HoverTips = HoverTipExporter.Build(potion.ExtraHoverTips),
        BaseValues = potion.DynamicVars.ToDictionary(v => v.Key, v => v.Value.BaseValue),
        Rarity = potion.Rarity.ToString(),
        Usage = potion.Usage.ToString(),
        TargetType = potion.TargetType.ToString(),
    };

    private static NPotionHolder? GetPotionHolder(PotionModel potion) => NRun.Instance is { } run
        ? EnumerateNodes(run.GlobalUi.TopBar.PotionContainer).OfType<NPotionHolder>()
            .FirstOrDefault(holder => holder.Potion?.Model == potion)
        : null;

    private static bool CanDiscardPotion(Player player, PotionModel potion)
    {
        if (!player.CanRemovePotions || !player.Creature.IsAlive || player.RunState.IsGameOver
            || potion.IsQueued || potion.HasBeenRemovedFromState || !player.Potions.Contains(potion)
            || RunManager.Instance.ActionExecutor.IsRunning || HasPendingHandSelection)
        {
            return false;
        }

        object? screen = ActiveScreenContext.Instance.GetCurrentScreen();
        bool available = CombatManager.Instance.IsInProgress
            ? screen is NCombatRoom && player.PlayerCombatState?.Phase == PlayerTurnPhase.Play
                && !CombatManager.Instance.PlayerActionsDisabled
            : screen is NRewardsScreen or NMapScreen or NMerchantRoom or NMerchantInventory
                or NRestSiteRoom or NTreasureRoom or NEventRoom or NFakeMerchant;
        NPotionHolder? holder = GetPotionHolder(potion);
        return available && holder is not null && IsNodeVisible(holder)
            && GetFieldValue(holder, "_disabledUntilPotionRemoved") is false;
    }

    public static RewardContextSnapshot? BuildRewardContext()
    {
        NRewardsScreen? rewardsScreen = GetActiveRewardsScreen();
        if (rewardsScreen is null)
        {
            return null;
        }

        List<RewardSnapshot> rewards = [];
        int index = 0;
        foreach (NRewardButton button in GetRewardButtons(rewardsScreen))
        {
            object? reward = button.Reward;
            if (reward is null)
            {
                index += 1;
                continue;
            }

            string? unavailableReason = GetRewardUnavailableReason(button);
            RelicModel? relic = (reward as RelicReward)?.Relic;
            rewards.Add(new RewardSnapshot
            {
                Id = BuildRewardId(reward, index),
                RewardType = GetPropertyText(reward, "RewardType"),
                Title = GetRewardTitle(reward),
                Description = GetRewardDescription(reward),
                Potion = reward is PotionReward { Potion: { } potion } ? BuildPotionSnapshot(potion) : null,
                ModelId = relic?.Id.Entry,
                Rarity = relic?.Rarity.ToString(),
                BaseValues = relic?.DynamicVars.ToDictionary(v => v.Key, v => v.Value.BaseValue),
                HoverTips = relic is not null ? HoverTipExporter.Build(relic.HoverTipsExcludingRelic) : null,
                Skippable = IsRewardButtonSelectable(button) && IsRewardSkippable(rewardsScreen),
                Selectable = unavailableReason is null,
                UnavailableReason = unavailableReason,
            });
            index += 1;
        }

        return new RewardContextSnapshot
        {
            ProceedEnabled = IsRewardProceedEnabled(rewardsScreen),
            Rewards = rewards,
        };
    }

    public static MerchantContextSnapshot? BuildMerchantContext()
    {
        Node? merchantRoom = GetActiveMerchantRoom();
        NMerchantInventory? inventory = GetActiveMerchantInventory();
        if (merchantRoom is null && inventory is null)
        {
            return null;
        }

        bool inventoryOpen = inventory is not null;
        bool roomInputAvailable = merchantRoom is not null
            && merchantRoom.GetNodeOrNull<Control>("%InputBlocker")?.MouseFilter != Control.MouseFilterEnum.Stop;
        NMerchantButton? merchantButton = merchantRoom?.GetNode<NMerchantButton>("%MerchantButton");
        NProceedButton? proceedButton = merchantRoom?.GetNode<NProceedButton>("%ProceedButton");
        bool enterShopAvailable = roomInputAvailable
            && merchantButton is { IsEnabled: true, IsLocalPlayerDead: false } && IsNodeVisible(merchantButton);
        bool leaveAvailable = inventory is not null && CanUseMerchantInventory(inventory);
        bool proceedAvailable = roomInputAvailable && proceedButton is { IsEnabled: true } && IsNodeVisible(proceedButton);
        List<MerchantItemSnapshot> items = inventory is not null ? BuildMerchantItems(inventory) : [];

        return new MerchantContextSnapshot
        {
            Kind = inventoryOpen ? "inventory" : "room",
            EnterShopAvailable = enterShopAvailable,
            LeaveAvailable = leaveAvailable,
            ProceedAvailable = proceedAvailable,
            Items = items,
        };
    }

    public static MapContextSnapshot? BuildMapContext(RunState? runState)
    {
        NMapScreen? mapScreen = NMapScreen.Instance;
        if (mapScreen is null || !ActiveScreenContext.Instance.IsCurrent(mapScreen) || !mapScreen.IsOpen)
        {
            return null;
        }

        if (runState?.Map is null)
        {
            return null;
        }

        HashSet<string> visitedPointIds = BuildVisitedPointIdSet(runState);
        string? currentPointId = BuildMapPointId(runState?.CurrentMapCoord);

        List<MapPointSnapshot> points = [];
        // GetAllMapPoints() only enumerates the grid, excluding the ancient and
        // boss nodes. The native screen contains every selectable map point.
        foreach (NMapPoint node in EnumerateNodes(mapScreen).OfType<NMapPoint>())
        {
            MapPoint point = node.Point;
            string? pointId = BuildMapPointId(point);
            if (string.IsNullOrEmpty(pointId))
            {
                continue;
            }

            (int? col, int? row) = GetMapCoordParts(point);
            points.Add(new MapPointSnapshot
            {
                Id = pointId,
                PointType = GetPropertyText(point, "PointType"),
                RoomKind = GetMapRoomKind(point),
                Col = col,
                Row = row,
                Children = BuildChildPointIds(point),
                Visited = visitedPointIds.Contains(pointId),
                Current = string.Equals(pointId, currentPointId, StringComparison.Ordinal),
                Travelable = CanSelectMapPoint(mapScreen, node),
            });
        }

        return new MapContextSnapshot
        {
            IsTravelEnabled = mapScreen.IsTravelEnabled,
            IsTraveling = mapScreen.IsTraveling,
            CurrentPointId = currentPointId,
            Points = points,
        };
    }

    public static ProceedContextSnapshot? BuildProceedContext(RunState? runState, int choiceCount, CardSelectionContextSnapshot? cardSelection)
    {
        if (cardSelection is not null || BuildRelicSelectionContext() is not null || choiceCount > 0 || BuildRestSiteContext(runState) is not null || BuildTreasureContext() is not null || BuildMerchantContext() is not null || BuildRewardContext() is not null || BuildMapContext(runState) is not null)
        {
            return null;
        }

        if (HasActivePostCombatProceed(runState))
        {
            return new ProceedContextSnapshot
            {
                Kind = "post_combat",
                Label = "Proceed",
            };
        }

        NEventRoom? eventRoom = NEventRoom.Instance;
        if (eventRoom is not null && ActiveScreenContext.Instance.IsCurrent(eventRoom)
            && GetEventModel(runState) is EventModel { IsFinished: true })
        {
            return new ProceedContextSnapshot
            {
                Kind = DetermineNonCombatScene(runState),
                Label = "Proceed",
            };
        }

        return null;
    }

    public static bool TryProceed(RunState? runState)
    {
        NRewardsScreen? rewardsScreen = GetActiveRewardsScreen();
        if (rewardsScreen is not null && IsRewardProceedEnabled(rewardsScreen))
        {
            MethodInfo? proceedMethod = rewardsScreen.GetType().GetMethod("OnProceedButtonPressed", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (proceedMethod is not null)
            {
                proceedMethod.Invoke(rewardsScreen, new object?[] { null });
                return true;
            }
        }

        Node? merchantRoom = GetActiveMerchantRoom();
        if (merchantRoom is not null && BuildMerchantContext()?.ProceedAvailable == true)
        {
            NProceedButton button = merchantRoom.GetNode<NProceedButton>("%ProceedButton");
            button.EmitSignal(NClickableControl.SignalName.Released, button);
            return true;
        }

        NRestSiteRoom? restSiteRoom = GetActiveRestSiteRoom();
        if (restSiteRoom is not null && restSiteRoom.ProceedButton is Node restProceedButton && IsNodeVisible(restProceedButton))
        {
            MethodInfo? proceedMethod = restSiteRoom.GetType().GetMethod("OnProceedButtonReleased", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (proceedMethod is not null)
            {
                proceedMethod.Invoke(restSiteRoom, new object?[] { null });
                return true;
            }
        }

        NTreasureRoom? treasureRoom = GetActiveTreasureRoom();
        if (treasureRoom is not null && treasureRoom.ProceedButton.IsEnabled && IsNodeVisible(treasureRoom.ProceedButton))
        {
            MethodInfo? proceedPressed = treasureRoom.GetType().GetMethod("OnProceedButtonPressed", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            proceedPressed?.Invoke(treasureRoom, new object?[] { null });
            return proceedPressed is not null;
        }

        if (TryProceedPostCombat(runState))
        {
            return true;
        }

        NEventRoom? eventRoom = NEventRoom.Instance;
        if (eventRoom is not null && ActiveScreenContext.Instance.IsCurrent(eventRoom)
            && GetEventModel(runState) is EventModel { IsFinished: true })
        {
            MethodInfo? proceedMethod = eventRoom.GetType().GetMethod("Proceed", BindingFlags.Public | BindingFlags.Static);
            proceedMethod?.Invoke(null, null);
            return proceedMethod is not null;
        }

        return false;
    }

    public static bool TryEnterMerchant()
    {
        Node? merchantRoom = GetActiveMerchantRoom();
        if (merchantRoom is null || BuildMerchantContext()?.EnterShopAvailable != true)
        {
            return false;
        }

        NMerchantButton merchantButton = merchantRoom.GetNode<NMerchantButton>("%MerchantButton");
        MethodInfo? onRelease = typeof(NMerchantButton).GetMethod("OnRelease", BindingFlags.Instance | BindingFlags.NonPublic);
        if (onRelease is null)
        {
            return false;
        }

        onRelease.Invoke(merchantButton, null);
        return true;
    }

    public static bool TryLeaveMerchant()
    {
        NMerchantInventory? inventory = GetActiveMerchantInventory();
        if (inventory is null || !CanUseMerchantInventory(inventory))
        {
            return false;
        }

        NBackButton button = inventory.GetNode<NBackButton>("%BackButton");
        button.EmitSignal(NClickableControl.SignalName.Released, button);
        return true;
    }

    public static bool TryPurchaseMerchantItem(string itemId)
    {
        NMerchantInventory? inventory = GetActiveMerchantInventory();
        if (inventory is null || !CanUseMerchantInventory(inventory))
        {
            return false;
        }

        if (!TryGetMerchantSlot(inventory, itemId, out NMerchantSlot? slot) || slot is null || !IsNodeVisible(slot))
        {
            return false;
        }

        object? entry = slot.Entry;
        if (entry is null || !GetMerchantItemAffordable(entry) || !GetMerchantItemStocked(entry))
        {
            return false;
        }

        // OnSelected is private on the base class, including for fake relic
        // slots. Use the same entry point as mouse/controller selection.
        MethodInfo? onSelected = typeof(NMerchantSlot).GetMethod("OnSelected", BindingFlags.Instance | BindingFlags.NonPublic);
        if (onSelected?.Invoke(slot, null) is Task purchase)
        {
            TaskHelper.RunSafely(purchase);
            return true;
        }

        return false;
    }

    public static bool TrySelectRestSiteOption(RunState? runState, string optionId)
    {
        NRestSiteRoom? roomNode = NRestSiteRoom.Instance;
        if (roomNode is null || !IsNodeVisible(roomNode))
        {
            return false;
        }

        object? room = GetCurrentRoom(runState) ?? GetFieldValue(roomNode, "_room");
        IEnumerable? options = GetMemberValue(room, "Options") as IEnumerable;
        if (options is null)
        {
            return false;
        }

        foreach (object? option in options)
        {
            if (option is not RestSiteOption restOption || !string.Equals(BuildRestSiteOptionId(restOption), optionId, StringComparison.Ordinal))
            {
                continue;
            }

            if (!restOption.IsEnabled)
            {
                return false;
            }

            object? button = null;
            foreach (MethodInfo getButton in roomNode.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (!string.Equals(getButton.Name, "GetButtonForOption", StringComparison.Ordinal))
                {
                    continue;
                }

                ParameterInfo[] parameters = getButton.GetParameters();
                try
                {
                    if (parameters.Length == 1)
                    {
                        button = getButton.Invoke(roomNode, new object[] { restOption });
                        break;
                    }

                    if (parameters.Length == 2)
                    {
                        button = getButton.Invoke(roomNode, new object?[] { null, restOption });
                        break;
                    }
                }
                catch
                {
                }
            }
            if (button is not null)
            {
                MethodInfo? onPress = button.GetType().GetMethod("OnPress", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                MethodInfo? onRelease = button.GetType().GetMethod("OnRelease", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                onPress?.Invoke(button, null);
                onRelease?.Invoke(button, null);
                return onRelease is not null || onPress is not null;
            }

            restOption.OnSelect();
            return true;
        }

        return false;
    }

    private static bool IsTreasureChestOpenAvailable(NTreasureRoom room) =>
        GetFieldValue(room, "_hasChestBeenOpened") is not true
        && GetFieldValue(room, "_chestButton") is NButton { IsEnabled: true } button
        && IsNodeVisible(button);

    public static bool TryOpenTreasureChest()
    {
        NTreasureRoom? room = GetActiveTreasureRoom();
        if (room is null || !IsTreasureChestOpenAvailable(room))
        {
            return false;
        }

        // Use the click handler, which disables the button immediately and
        // observes the asynchronous opening task. Do not call OpenChest directly.
        MethodInfo? onChestButtonReleased = room.GetType().GetMethod("OnChestButtonReleased", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (onChestButtonReleased is not null)
        {
            onChestButtonReleased.Invoke(room, new object?[] { null });
            return true;
        }

        return false;
    }

    public static bool TrySelectTreasureRelic(string relicId)
    {
        NTreasureRoom? room = GetActiveTreasureRoom();
        if (room is null)
        {
            return false;
        }

        if (!TryFindTreasureRelicHolder(room, relicId, out NTreasureRoomRelicHolder? holder) || holder is null)
        {
            return false;
        }

        // OnRelease alone only animates the holder. Emit the native Released
        // signal after validating that this is an interactive offered relic.
        holder.ForceClick();
        return true;
    }

    public static bool TrySelectRelic(string relicId)
    {
        NChooseARelicSelection? screen = GetActiveRelicSelectionScreen();
        if (screen is not null && TryFindRelicHolder(screen, relicId, out Node? holder) && holder is not null)
        {
            MethodInfo? selectHolder = FindSingleParameterMethod(screen.GetType(), "SelectHolder");
            if (selectHolder is not null)
            {
                selectHolder.Invoke(screen, new object[] { holder });
                return true;
            }
        }

        return TrySelectTreasureRelic(relicId);
    }

    public static bool TryUsePotion(Player? player, CombatState? combatState, string potionId, string? targetId)
    {
        if (player?.Potions is null)
        {
            return false;
        }

        foreach (object? potionLike in player.Potions)
        {
            if (potionLike is not PotionModel potion)
            {
                continue;
            }

            if (!string.Equals(BuildPotionId(potion), potionId, StringComparison.Ordinal))
            {
                continue;
            }

            if (!CanDiscardPotion(player, potion))
            {
                return false;
            }

            object? target = ResolveCommandTarget(targetId, combatState, player);
            try
            {
                MethodInfo? enqueueManualUse = potion.GetType().GetMethod("EnqueueManualUse", BindingFlags.Instance | BindingFlags.Public);
                if (enqueueManualUse is null)
                {
                    return false;
                }

                enqueueManualUse.Invoke(potion, new[] { target });
                return true;
            }
            catch
            {
                return false;
            }
        }

        return false;
    }

    public static bool TryDiscardPotion(Player player, string potionId)
    {
        PotionModel? potion = player.Potions.FirstOrDefault(p => BuildPotionId(p) == potionId);
        if (potion is null || !CanDiscardPotion(player, potion))
        {
            return false;
        }

        // Match NPotionPopup: lock this holder until native removal/cancellation
        // and enqueue the synchronized discard, including history and hooks.
        GetPotionHolder(potion)!.DisableUntilPotionRemoved();
        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new DiscardPotionGameAction(
            player, (uint)player.GetPotionSlotIndex(potion), CombatManager.Instance.IsInProgress));
        return true;
    }

    public static bool TryTakeReward(string rewardId)
    {
        if (!TryGetRewardButton(rewardId, out NRewardsScreen? rewardsScreen, out NRewardButton? button) || rewardsScreen is null || button is null)
        {
            return false;
        }

        if (GetRewardUnavailableReason(button) is not null)
        {
            return false;
        }

        MethodInfo? onRelease = button.GetType().GetMethod("OnRelease", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
        if (onRelease is not null)
        {
            onRelease.Invoke(button, null);
            return true;
        }

        return false;
    }

    public static bool TrySkipReward(string rewardId)
    {
        if (!TryGetRewardButton(rewardId, out NRewardsScreen? rewardsScreen, out NRewardButton? button) || rewardsScreen is null || button is null)
        {
            return false;
        }

        // OnSkipped only records history; RewardSkippedFrom only pulses Skip.
        // The native lifecycle skips the SET on Proceed. Never silently drop
        // other unclaimed loot for a command naming a single reward.
        return IsRewardSkippable(rewardsScreen) && TryProceed(GetRunState(RunManager.Instance));
    }

    public static bool TrySelectMapPoint(RunState? runState, string pointId)
    {
        NMapScreen? mapScreen = NMapScreen.Instance;
        if (runState?.Map is null || mapScreen is null || !ActiveScreenContext.Instance.IsCurrent(mapScreen) || !mapScreen.IsOpen)
        {
            return false;
        }

        NMapPoint? point = EnumerateNodes(mapScreen).OfType<NMapPoint>()
            .FirstOrDefault(node => string.Equals(BuildMapPointId(node.Point), pointId, StringComparison.Ordinal)
                && CanSelectMapPoint(mapScreen, node));
        if (point is null)
        {
            return false;
        }

        // Follow the same selection/vote action as a click, not the downstream
        // travel method which assumes the destination has already been checked.
        mapScreen.OnMapPointSelectedLocally(point);
        return true;
    }

    public static bool TrySelectCard(string cardId)
    {
        object? screen = GetActiveCardSelectionScreen();
        if (screen is null)
        {
            return false;
        }

        if (!TryGetCardSelectionItem(screen, cardId, out object? card) || card is null)
        {
            return false;
        }

        if (screen is NPlayerHand hand)
        {
            CardSelectorPrefs prefs = (CardSelectorPrefs)GetFieldValue(hand, "_prefs")!;
            List<CardModel> selected = (List<CardModel>)GetFieldValue(hand, "_selectedCards")!;
            bool isSelected = selected.Contains((CardModel)card);
            if (!isSelected && selected.Count >= prefs.MaxSelect)
            {
                return false;
            }
            // Selected cards move out of the ordinary hand-holder container.
            // Upgrade mode instead puts the original card in a preview holder.
            NCardHolder? holder = isSelected && hand.CurrentMode == NPlayerHand.Mode.UpgradeSelect
                ? ((NUpgradePreview)GetFieldValue(hand, "_upgradePreview")!).DefaultFocusedControl as NCardHolder
                : hand.GetCardHolder((CardModel)card);
            if (holder?.CardNode?.Model != card || !holder.IsVisibleInTree())
            {
                return false;
            }
            holder.EmitSignal(NCardHolder.SignalName.Pressed, holder);
            return true;
        }

        if (screen is NCardGridSelectionScreen gridScreen)
        {
            CardSelectorPrefs prefs = (CardSelectorPrefs)GetFieldValue(screen, "_prefs")!;
            HashSet<CardModel> selected = (HashSet<CardModel>)GetFieldValue(screen, "_selectedCards")!;
            if (!IsSelectionGridInteractive(gridScreen)
                || (!selected.Contains((CardModel)card) && selected.Count >= prefs.MaxSelect))
            {
                return false;
            }
        }

        string screenTypeName = screen.GetType().FullName ?? screen.GetType().Name;
        if (string.Equals(screenTypeName, "MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NChooseACardSelectionScreen", StringComparison.Ordinal))
        {
            return TrySelectChooseACardScreen(screen, card);
        }

        if (string.Equals(screenTypeName, "MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NCardRewardSelectionScreen", StringComparison.Ordinal))
        {
            return TrySelectCardRewardScreen(screen, card);
        }

        MethodInfo? onCardClicked = FindSingleParameterMethod(screen.GetType(), "OnCardClicked");
        if (onCardClicked is null)
        {
            return false;
        }

        onCardClicked.Invoke(screen, new[] { card });
        return true;
    }

    public static bool TryConfirmCardSelection()
    {
        NConfirmButton? button = GetActiveCardSelectionScreen() switch
        {
            NPlayerHand hand => GetHandConfirmButton(hand),
            NCombatPileCardSelectScreen pile => GetCombatPileConfirmButton(pile),
            NCardGridSelectionScreen grid => GetGridConfirmButton(grid),
            _ => null,
        };
        if (button is null)
        {
            return false;
        }

        // Use the native button callback: it resolves the pending choice and
        // removes the overlay. Never force CheckIfSelectionComplete on a click;
        // that bypasses manual confirmation (or completes an automatic choice twice).
        button.ForceClick();
        return true;
    }

    private static NConfirmButton? GetHandConfirmButton(NPlayerHand hand)
    {
        CardSelectorPrefs prefs = (CardSelectorPrefs)GetFieldValue(hand, "_prefs")!;
        List<CardModel> selected = (List<CardModel>)GetFieldValue(hand, "_selectedCards")!;
        HashSet<CardModel> candidates = GetCardSelectionItems(hand).Cast<CardModel>().ToHashSet();
        return selected.Count >= prefs.MinSelect && selected.Count <= prefs.MaxSelect
            && selected.All(candidates.Contains)
            && GetFieldValue(hand, "_selectModeConfirmButton") is NConfirmButton { IsEnabled: true } button
            && button.IsVisibleInTree() ? button : null;
    }

    private static NConfirmButton? GetCombatPileConfirmButton(NCombatPileCardSelectScreen screen)
    {
        return GetFieldValue(screen, "_confirmButton") is NConfirmButton { IsEnabled: true } button
            && button.IsVisibleInTree() ? button : null;
    }

    private static bool IsSelectionGridInteractive(NCardGridSelectionScreen screen)
    {
        return GetFieldValue(screen, "_peekButton") is NPeekButton { IsPeeking: false }
            && GetFieldValue(screen, "_grid") is NCardGrid grid && grid.IsVisibleInTree()
            && grid.FocusBehaviorRecursive != Control.FocusBehaviorRecursiveEnum.Disabled;
    }

    private static NConfirmButton? GetGridConfirmButton(NCardGridSelectionScreen screen)
    {
        if (!screen.IsVisibleInTree()
            || GetFieldValue(screen, "_peekButton") is not NPeekButton { IsPeeking: false })
            return null;
        CardSelectorPrefs prefs = (CardSelectorPrefs)GetFieldValue(screen, "_prefs")!;
        HashSet<CardModel> selected = (HashSet<CardModel>)GetFieldValue(screen, "_selectedCards")!;
        if (selected.Count < prefs.MinSelect || selected.Count > prefs.MaxSelect)
            return null;

        // Range selections can have both the grid's Continue button and a
        // preview Confirm enabled. The visible preview owns input in that case.
        foreach (string field in new[] { "_singlePreviewConfirmButton", "_multiPreviewConfirmButton", "_previewConfirmButton", "_confirmButton" })
        {
            if (GetFieldValue(screen, field) is NConfirmButton { IsEnabled: true } button
                && button.IsVisibleInTree())
                return button;
        }
        return null;
    }

    public static ChoiceContextSnapshot? BuildChoiceContext(RunState? runState)
    {
        if (GetMutableEventModel(runState) is not EventModel eventModel
            || eventModel.Description is not LocString source || eventModel.Owner is not Player owner)
        {
            return null;
        }

        // The room can exist before NEventRoom populates its description. Use
        // its localization inputs on a copy, never format an unstarted event's
        // InitialDescription or mutate the live description during observation.
        LocString description = new(source.LocTable, source.LocEntryKey);
        description.AddVariablesFrom(source);
        if (description.Exists())
        {
            owner.Character.AddDetailsTo(description);
            description.Add("IsMultiplayer", owner.RunState.Players.Count > 1);
            eventModel.DynamicVars.AddTo(description);
        }
        return new ChoiceContextSnapshot
        {
            Kind = DetermineNonCombatScene(runState),
            Title = eventModel.Title.GetFormattedText(),
            // Ancients can display dialogue instead of this description. Match
            // NEventRoom: don't present a missing localization key as game text.
            // Already-rendered dialogue remains in screen.visible_controls.
            Description = description.Exists() ? description.GetFormattedText() : string.Empty,
        };
    }

    public static bool TrySelectChoice(RunState? runState, string choiceId)
    {
        if (!TryGetChoice(runState, choiceId, out object? option, out int index) || option is null)
        {
            return false;
        }

        if (GetBoolProperty(option, "IsLocked"))
        {
            return false;
        }

        NEventRoom? eventRoom = NEventRoom.Instance;
        if (eventRoom is null || !ActiveScreenContext.Instance.IsCurrent(eventRoom))
        {
            return false;
        }

        MethodInfo? optionButtonClicked = GetEventOptionButtonClickedMethod(eventRoom.GetType(), option.GetType());
        if (optionButtonClicked is null)
        {
            return false;
        }

        optionButtonClicked.Invoke(eventRoom, new[] { option, (object)index });
        return true;
    }

    public static Player? GetPrimaryPlayer(CombatState? combatState, RunState? runState)
    {
        if (combatState?.Players is not null)
        {
            foreach (Player player in combatState.Players)
            {
                return player;
            }
        }

        IEnumerable? players = runState?.Players;
        if (players is not null)
        {
            foreach (object? player in players)
            {
                if (player is Player typedPlayer)
                {
                    return typedPlayer;
                }
            }
        }

        return null;
    }

    public static CardModel? GetCardModel(object handItem)
    {
        if (handItem is CardModel model)
        {
            return model;
        }

        foreach (string propertyName in new[] { "Model", "CardModel", "Card" })
        {
            object? value = handItem.GetType().GetProperty(propertyName)?.GetValue(handItem);
            if (value is CardModel cardModel)
            {
                return cardModel;
            }
        }

        return null;
    }

    public static string BuildCardId(object handItem, int index)
    {
        return GetCardModel(handItem) is CardModel card ? CardStateExporter.Id(card)
            : $"hand_{index.ToString(CultureInfo.InvariantCulture)}";
    }

    public static bool CardMatchesId(object handItem, int index, string commandCardId)
    {
        return string.Equals(BuildCardId(handItem, index), commandCardId, StringComparison.Ordinal);
    }

    public static string GetCardName(object handItem)
    {
        CardModel? model = GetCardModel(handItem);
        if (!string.IsNullOrEmpty(model?.Title))
        {
            return model.Title;
        }

        return GetStringProperty(handItem, "Title");
    }

    public static string GetCardDescription(object handItem)
    {
        CardModel? model = GetCardModel(handItem);
        object? modelDescription = model?.Description;
        if (modelDescription is not null)
        {
            string text = GetRawLocalizedText(modelDescription);
            if (!string.IsNullOrEmpty(text))
            {
                return text;
            }
        }

        return GetStringProperty(handItem, "Description");
    }

    public static string GetCardCostText(object handItem)
    {
        foreach (string propertyName in new[] { "Cost", "CurrentCost", "EnergyCost" })
        {
            object? value = handItem.GetType().GetProperty(propertyName)?.GetValue(handItem);
            string resolved = ReadCostValue(value);
            if (!string.IsNullOrEmpty(resolved))
            {
                return resolved;
            }
        }

        CardModel? model = GetCardModel(handItem);
        string modelCost = ReadCostValue(model?.EnergyCost);
        if (!string.IsNullOrEmpty(modelCost))
        {
            return modelCost;
        }

        return string.Empty;
    }

    public static string GetCardRarity(object handItem)
    {
        CardModel? model = GetCardModel(handItem);
        object? rarity = model is null ? GetMemberValue(handItem, "Rarity") : model.Rarity;
        return rarity?.ToString() ?? string.Empty;
    }

    public static bool IsCardPlayable(object handItem)
    {
        // IsPlayable only covers the card's own logic; CanPlay also checks
        // current energy/stars, keywords, and powers/relics that prevent play.
        return GetCardModel(handItem)?.CanPlay() == true;
    }

    public static string BuildCreatureId(Creature creature, int index)
    {
        if (creature.CombatId is uint id)
        {
            return $"enemy_{id.ToString(CultureInfo.InvariantCulture)}";
        }
        if (!string.IsNullOrEmpty(creature.SlotName))
        {
            return creature.SlotName;
        }

        return $"enemy_{index.ToString(CultureInfo.InvariantCulture)}";
    }

    public static object? ResolveCommandTarget(string? targetId, CombatState? combatState, Player? player)
    {
        if (string.IsNullOrEmpty(targetId))
        {
            return null;
        }

        if (string.Equals(targetId, "self", StringComparison.Ordinal))
        {
            return player?.Creature;
        }

        if (combatState?.Enemies is not null)
        {
            int index = 0;
            foreach (Creature enemy in combatState.Enemies)
            {
                if (string.Equals(BuildCreatureId(enemy, index), targetId, StringComparison.Ordinal))
                {
                    return enemy;
                }

                index += 1;
            }
        }

        return null;
    }

    public static object? FindHandCardById(PlayerCombatState? combatState, string cardId)
    {
        if (combatState?.Hand is null)
        {
            return null;
        }

        int index = 0;
        foreach (object? handItem in combatState.Hand.Cards)
        {
            if (handItem is not null && CardMatchesId(handItem, index, cardId))
            {
                return handItem;
            }

            index += 1;
        }

        return null;
    }

    private static IEnumerable? GetCurrentOptions(RunState? runState)
    {
        if (GetMutableEventModel(runState) is not EventModel eventModel)
        {
            return null;
        }

        // Exports can run before option buttons finish _Ready. Match the
        // game's NEventOptionButton localization setup rather than formatting
        // descriptions with missing event variables during that interval.
        foreach (EventOption option in eventModel.CurrentOptions)
        {
            eventModel.DynamicVars.AddTo(option.Title);
            eventModel.DynamicVars.AddTo(option.Description);
        }
        return eventModel.CurrentOptions;
    }

    private static bool TryGetChoice(RunState? runState, string choiceId, out object? option, out int index)
    {
        option = null;
        index = -1;

        IEnumerable? currentOptions = GetCurrentOptions(runState);
        if (currentOptions is null)
        {
            return false;
        }

        int currentIndex = 0;
        foreach (object? currentOption in currentOptions)
        {
            if (currentOption is not null && string.Equals(BuildChoiceId(currentOption, currentIndex), choiceId, StringComparison.Ordinal))
            {
                option = currentOption;
                index = currentIndex;
                return true;
            }

            currentIndex += 1;
        }

        return false;
    }

    private static bool TryGetRewardButton(string rewardId, out NRewardsScreen? rewardsScreen, out NRewardButton? rewardButton)
    {
        rewardsScreen = GetActiveRewardsScreen();
        rewardButton = null;
        if (rewardsScreen is null)
        {
            return false;
        }

        int index = 0;
        foreach (NRewardButton button in GetRewardButtons(rewardsScreen))
        {
            object? reward = button.Reward;
            if (reward is not null && string.Equals(BuildRewardId(reward, index), rewardId, StringComparison.Ordinal))
            {
                if (!IsRewardButtonSelectable(button))
                {
                    return false;
                }

                rewardButton = button;
                return true;
            }

            index += 1;
        }

        return false;
    }

    private static bool TrySelectChooseACardScreen(object screen, object card)
    {
        object? cardHolder = FindCardHolderForScreen(screen, card);
        if (cardHolder is null)
        {
            return false;
        }

        MethodInfo? selectHolder = FindSingleParameterMethod(screen.GetType(), "SelectHolder");
        if (selectHolder is null)
        {
            return false;
        }

        selectHolder.Invoke(screen, new[] { cardHolder });
        return true;
    }

    private static bool TrySelectCardRewardScreen(object screen, object card)
    {
        object? cardHolder = FindCardHolderForScreen(screen, card);
        if (cardHolder is null)
        {
            return false;
        }

        MethodInfo? selectCard = FindSingleParameterMethod(screen.GetType(), "SelectCard");
        if (selectCard is null)
        {
            return false;
        }

        selectCard.Invoke(screen, new[] { cardHolder });
        return true;
    }

    private static object? FindCardHolderForScreen(object screen, object card)
    {
        object? cardRow = GetFieldValue(screen, "_cardRow");
        if (cardRow is not Node cardRowNode)
        {
            return null;
        }

        CardModel? targetModel = GetCardModel(card);
        object? fallbackHolder = null;
        foreach (Node node in EnumerateNodes(cardRowNode))
        {
            string typeName = node.GetType().FullName ?? node.GetType().Name;
            if (!IsCardHolderTypeName(typeName))
            {
                continue;
            }

            object? holderModelObject = node.GetType().GetProperty("CardModel", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(node);
            if (targetModel is not null && ReferenceEquals(holderModelObject, targetModel))
            {
                return node;
            }

            if (fallbackHolder is null)
            {
                fallbackHolder = node;
            }
        }

        return fallbackHolder;
    }

    private static bool TryGetCardSelectionItem(object screen, string cardId, out object? card)
    {
        card = null;
        int index = 0;
        foreach (object? screenCard in GetCardSelectionItems(screen))
        {
            if (screenCard is not null && CardMatchesId(screenCard, index, cardId))
            {
                card = screenCard;
                return true;
            }

            index += 1;
        }

        return false;
    }

    private static NRewardsScreen? GetActiveRewardsScreen()
    {
        // IsComplete means rewards were claimed/skipped, not that the screen
        // closed. Terminal reward screens still need their Proceed button.
        return ActiveScreenContext.Instance.GetCurrentScreen() as NRewardsScreen;
    }

    private static Node? GetActiveMerchantRoom()
    {
        // Resolve the active native screen, not the ordinary shop singleton:
        // event shops have a different owner and overlays must block actions.
        return ActiveScreenContext.Instance.GetCurrentScreen() switch
        {
            NMerchantRoom room when IsNodeVisible(room) => room,
            NFakeMerchant room when IsNodeVisible(room) => room,
            _ => null,
        };
    }

    private static NMerchantInventory? GetActiveMerchantInventory()
    {
        NMerchantInventory? inventory = ActiveScreenContext.Instance.GetCurrentScreen() as NMerchantInventory;
        if (inventory is null || !inventory.IsOpen || !IsNodeVisible(inventory))
        {
            return null;
        }

        return inventory;
    }

    private static bool CanUseMerchantInventory(NMerchantInventory inventory)
    {
        NBackButton button = inventory.GetNode<NBackButton>("%BackButton");
        return button.IsEnabled && IsNodeVisible(button);
    }

    private static NRestSiteRoom? GetActiveRestSiteRoom()
    {
        return ActiveScreenContext.Instance.GetCurrentScreen() as NRestSiteRoom;
    }

    private static NTreasureRoom? GetActiveTreasureRoom()
    {
        return ActiveScreenContext.Instance.GetCurrentScreen() as NTreasureRoom;
    }

    private static NChooseARelicSelection? GetActiveRelicSelectionScreen()
    {
        return ActiveScreenContext.Instance.GetCurrentScreen() as NChooseARelicSelection;
    }

    private static List<RelicSnapshot> BuildRelicSelectionRelics(NChooseARelicSelection screen)
    {
        List<RelicSnapshot> relics = [];
        Type? holderType = FindSingleParameterMethod(screen.GetType(), "SelectHolder")?.GetParameters()[0].ParameterType;
        int index = 0;
        foreach (Node node in EnumerateNodes(screen))
        {
            if (holderType is not null && !holderType.IsInstanceOfType(node))
            {
                continue;
            }

            object? relicLike = GetMemberValue(node, "Relic");
            if (relicLike is not RelicModel relic)
            {
                continue;
            }

            relics.Add(BuildRelicSnapshot(relic, index));
            index += 1;
        }

        return relics;
    }

    private static List<RelicSnapshot> BuildTreasureRelics(NTreasureRoom room)
    {
        List<RelicSnapshot> relics = [];
        int index = 0;
        foreach (NTreasureRoomRelicHolder holder in GetTreasureRelicHolders(room))
        {
            relics.Add(BuildRelicSnapshot(holder.Relic.Model, index));
            index += 1;
        }

        return relics;
    }

    private static IEnumerable<NTreasureRoomRelicHolder> GetTreasureRelicHolders(NTreasureRoom room)
    {
        if (GetFieldValue(room, "_isRelicCollectionOpen") is not true
            || GetFieldValue(room, "_relicCollection") is not Node collection)
        {
            yield break;
        }

        foreach (Node node in EnumerateNodes(collection))
        {
            // The scene includes unused single-/multiplayer holders whose
            // NRelic exists but whose Model getter throws. Visibility must be
            // checked before accessing it. Use the same candidates for actions.
            if (node is NTreasureRoomRelicHolder holder && IsNodeVisible(holder)
                && holder.IsEnabled && holder.MouseFilter != Control.MouseFilterEnum.Ignore)
            {
                yield return holder;
            }
        }
    }

    private static RelicSnapshot BuildRelicSnapshot(object relicLike, int index)
    {
        object? model = GetMemberValue(relicLike, "Model") ?? relicLike;
        string title = GetLocalizedPropertyValue(model, "Title");
        if (string.IsNullOrEmpty(title))
        {
            title = GetPropertyTextOrEmpty(model, "Name");
        }

        return new RelicSnapshot
        {
            Id = BuildRelicId(title, index),
            Title = title,
            Description = model is RelicModel relic ? GetLocalizedText(relic.DynamicDescription)
                : GetRawLocalizedPropertyValue(model, "Description"),
            HoverTips = model is RelicModel relicModel ? HoverTipExporter.Build(relicModel.HoverTipsExcludingRelic) : [],
            Rarity = GetPropertyTextOrEmpty(model, "Rarity"),
        };
    }

    private static bool TryFindRelicHolder(NChooseARelicSelection screen, string relicId, out Node? holder)
    {
        holder = null;
        Type? holderType = FindSingleParameterMethod(screen.GetType(), "SelectHolder")?.GetParameters()[0].ParameterType;
        int index = 0;
        foreach (Node node in EnumerateNodes(screen))
        {
            if (holderType is not null && !holderType.IsInstanceOfType(node))
            {
                continue;
            }

            object? relicLike = GetMemberValue(node, "Relic");
            if (relicLike is null)
            {
                continue;
            }

            if (string.Equals(BuildRelicSnapshot(relicLike, index).Id, relicId, StringComparison.Ordinal))
            {
                holder = node;
                return true;
            }

            index += 1;
        }

        return false;
    }

    private static bool TryFindTreasureRelicHolder(NTreasureRoom room, string relicId, out NTreasureRoomRelicHolder? holder)
    {
        holder = null;
        int index = 0;
        foreach (NTreasureRoomRelicHolder relicHolder in GetTreasureRelicHolders(room))
        {
            if (string.Equals(BuildRelicSnapshot(relicHolder.Relic.Model, index).Id, relicId, StringComparison.Ordinal))
            {
                holder = relicHolder;
                return true;
            }

            index += 1;
        }

        return false;
    }

    private static List<MerchantItemSnapshot> BuildMerchantItems(NMerchantInventory inventory)
    {
        List<MerchantItemSnapshot> items = [];
        int index = 0;
        foreach (NMerchantSlot slot in GetMerchantSlots(inventory))
        {
            object? entry = slot.Entry;
            if (entry is null)
            {
                index += 1;
                continue;
            }

            string kind = GetMerchantItemKind(slot, entry);
            string title = GetMerchantItemTitle(slot, entry, kind);
            AbstractModel? model = entry switch
            {
                MerchantCardEntry cardEntry => cardEntry.CreationResult?.Card,
                MerchantRelicEntry relicEntry => relicEntry.Model,
                MerchantPotionEntry potionEntry => potionEntry.Model,
                _ => null,
            };
            CardSnapshot? card = model is CardModel cardModel ? CardStateExporter.BuildCard(cardModel) : null;
            int cost = GetMerchantItemCost(entry);
            bool affordable = GetMerchantItemAffordable(entry);
            bool stocked = GetMerchantItemStocked(entry);

            items.Add(new MerchantItemSnapshot
            {
                Id = BuildMerchantItemId(kind, title, cost, index),
                Kind = kind,
                Title = title,
                ModelId = model?.Id.Entry,
                Description = card?.Description ?? GetMerchantItemDescription(slot, model, kind),
                HoverTips = model switch
                {
                    RelicModel relic => HoverTipExporter.Build(relic.HoverTipsExcludingRelic),
                    PotionModel potion => HoverTipExporter.Build(potion.ExtraHoverTips),
                    _ => card?.HoverTips ?? [],
                },
                BaseValues = model switch
                {
                    RelicModel relic => relic.DynamicVars.ToDictionary(v => v.Key, v => v.Value.BaseValue),
                    PotionModel potion => potion.DynamicVars.ToDictionary(v => v.Key, v => v.Value.BaseValue),
                    _ => card?.BaseValues ?? [],
                },
                Card = card,
                Cost = cost,
                Affordable = affordable,
                Purchasable = affordable && stocked && IsNodeVisible(slot) && CanUseMerchantInventory(inventory),
                Rarity = GetMerchantItemRarity(entry, kind),
                OnSale = GetMerchantItemOnSale(entry),
            });
            index += 1;
        }

        return items;
    }

    private static IEnumerable<NMerchantSlot> GetMerchantSlots(NMerchantInventory inventory)
    {
        MethodInfo? getAllSlots = inventory.GetType().GetMethod("GetAllSlots", BindingFlags.Instance | BindingFlags.Public);
        if (getAllSlots?.Invoke(inventory, null) is IEnumerable slotEnumerable)
        {
            foreach (object? slotLike in slotEnumerable)
            {
                if (slotLike is NMerchantSlot slot)
                {
                    yield return slot;
                }
            }
        }
    }

    private static bool TryGetMerchantSlot(NMerchantInventory inventory, string itemId, out NMerchantSlot? matchedSlot)
    {
        matchedSlot = null;
        int index = 0;
        foreach (NMerchantSlot slot in GetMerchantSlots(inventory))
        {
            object? entry = slot.Entry;
            if (entry is null)
            {
                index += 1;
                continue;
            }

            string kind = GetMerchantItemKind(slot, entry);
            string title = GetMerchantItemTitle(slot, entry, kind);
            int cost = GetMerchantItemCost(entry);
            if (string.Equals(BuildMerchantItemId(kind, title, cost, index), itemId, StringComparison.Ordinal))
            {
                matchedSlot = slot;
                return true;
            }

            index += 1;
        }

        return false;
    }

    private static string BuildMerchantItemId(string kind, string title, int cost, int index)
    {
        uint hash = 2166136261;
        foreach (char character in $"{kind}|{title}|{cost.ToString(CultureInfo.InvariantCulture)}|{index.ToString(CultureInfo.InvariantCulture)}")
        {
            hash ^= character;
            hash *= 16777619;
        }

        return $"shop_{kind}_{index.ToString(CultureInfo.InvariantCulture)}_{hash.ToString(CultureInfo.InvariantCulture)}";
    }

    private static string BuildPotionId(PotionModel potion) => PotionIdentities.GetValue(potion,
        _ => new PotionIdentity($"potion_{Guid.NewGuid():N}")).Id;

    private static string BuildRelicId(string title, int index)
    {
        return BuildStableId("relic", title, index);
    }

    private static string BuildRestSiteOptionId(RestSiteOption option)
    {
        string title = GetLocalizedText(option.Title);
        string optionId = option.OptionId.ToString();
        return BuildStableId("rest", $"{optionId}|{title}", 0);
    }

    private static string BuildStableId(string prefix, string key, int index)
    {
        uint hash = 2166136261;
        foreach (char character in $"{key}|{index.ToString(CultureInfo.InvariantCulture)}")
        {
            hash ^= character;
            hash *= 16777619;
        }

        return $"{prefix}_{index.ToString(CultureInfo.InvariantCulture)}_{hash.ToString(CultureInfo.InvariantCulture)}";
    }

    private static string GetMerchantItemKind(NMerchantSlot slot, object entry)
    {
        object? visual = GetMemberValue(slot, "Visual");
        string slotTypeName = visual?.GetType().Name ?? slot.GetType().Name;
        if (slotTypeName.Contains("CardRemoval", StringComparison.Ordinal))
        {
            return "remove_card";
        }

        if (slotTypeName.Contains("Relic", StringComparison.Ordinal))
        {
            return "relic";
        }

        if (slotTypeName.Contains("Potion", StringComparison.Ordinal))
        {
            return "potion";
        }

        if (slotTypeName.Contains("Card", StringComparison.Ordinal))
        {
            return "card";
        }

        string entryTypeName = entry.GetType().Name;
        if (entryTypeName.Contains("CardRemoval", StringComparison.Ordinal))
        {
            return "remove_card";
        }

        if (entryTypeName.Contains("Relic", StringComparison.Ordinal))
        {
            return "relic";
        }

        if (entryTypeName.Contains("Potion", StringComparison.Ordinal))
        {
            return "potion";
        }

        return "card";
    }

    private static string GetMerchantItemTitle(NMerchantSlot slot, object entry, string kind)
    {
        object? visual = GetMemberValue(slot, "Visual");
        if (kind == "remove_card")
        {
            string text = GetLocalizedPropertyValue(visual, "Title");
            if (!string.IsNullOrEmpty(text))
            {
                return text;
            }

            return "Remove a card";
        }

        if (kind == "card")
        {
            object? creationResult = GetMemberValue(entry, "CreationResult");
            object? card = GetMemberValue(creationResult, "Card");
            if (card is not null)
            {
                return GetCardName(card);
            }
        }

        foreach (string memberName in new[] { "Model", "Potion", "Relic" })
        {
            object? model = GetMemberValue(entry, memberName);
            string text = GetLocalizedPropertyValue(model, "Title");
            if (!string.IsNullOrEmpty(text))
            {
                return text;
            }

            text = GetPropertyTextOrEmpty(model, "Name");
            if (!string.IsNullOrEmpty(text))
            {
                return text;
            }
        }

        return kind;
    }

    private static string GetMerchantItemDescription(NMerchantSlot slot, AbstractModel? model, string kind)
    {
        object? visual = GetMemberValue(slot, "Visual");
        if (kind == "remove_card")
        {
            string text = GetLocalizedPropertyValue(visual, "Description");
            if (!string.IsNullOrEmpty(text))
            {
                return text;
            }

            return "Remove a card from your deck.";
        }

        return model switch
        {
            RelicModel relic => relic.DynamicDescription.GetFormattedText(),
            PotionModel potion => potion.DynamicDescription.GetFormattedText(),
            _ => string.Empty,
        };
    }

    private static int GetMerchantItemCost(object entry)
    {
        object? cost = GetMemberValue(entry, "Cost");
        return cost is int intCost ? intCost : 0;
    }

    private static bool GetMerchantItemAffordable(object entry)
    {
        object? enoughGold = GetMemberValue(entry, "EnoughGold");
        return enoughGold is bool boolValue && boolValue;
    }

    private static bool GetMerchantItemStocked(object entry)
    {
        object? isStocked = GetMemberValue(entry, "IsStocked");
        return isStocked is bool boolValue && boolValue;
    }

    private static string? GetMerchantItemRarity(object entry, string kind)
    {
        if (kind == "card")
        {
            object? creationResult = GetMemberValue(entry, "CreationResult");
            object? card = GetMemberValue(creationResult, "Card");
            if (card is not null)
            {
                string rarity = GetCardRarity(card);
                return string.IsNullOrEmpty(rarity) ? null : rarity;
            }
        }

        object? model = GetMemberValue(entry, "Model");
        string modelRarity = GetPropertyTextOrEmpty(model, "Rarity");
        return string.IsNullOrEmpty(modelRarity) ? null : modelRarity;
    }

    private static bool? GetMerchantItemOnSale(object entry)
    {
        object? onSale = GetMemberValue(entry, "IsOnSale");
        return onSale is bool boolValue ? boolValue : null;
    }

    private static bool HasActivePostCombatProceed(RunState? runState)
    {
        NCombatRoom? combatRoom = NCombatRoom.Instance;
        if (combatRoom is null || !ActiveScreenContext.Instance.IsCurrent(combatRoom))
        {
            return false;
        }

        if (BuildRewardContext() is not null || BuildCardSelectionContext() is not null || BuildMapContext(runState) is not null)
        {
            return false;
        }

        if (runState?.CurrentRoom?.RoomType.ToString() != "Monster")
        {
            return false;
        }

        IEnumerable<Creature> enemies = GetCombatEnemies(runState);
        foreach (Creature _ in enemies)
        {
            return false;
        }

        return GetMemberValue(combatRoom, "Ui") is not null;
    }

    private static bool TryProceedPostCombat(RunState? runState)
    {
        if (!HasActivePostCombatProceed(runState))
        {
            return false;
        }

        NCombatRoom? combatRoom = NCombatRoom.Instance;
        object? ui = GetMemberValue(combatRoom, "Ui");
        MethodInfo? proceedWithoutRewards = ui?.GetType().GetMethod("ProceedWithoutRewards", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
        if (proceedWithoutRewards is null)
        {
            return false;
        }

        proceedWithoutRewards.Invoke(ui, null);
        return true;
    }

    private static IEnumerable<Creature> GetCombatEnemies(RunState? runState)
    {
        CombatState? combatState = GetCombatState(runState);
        if (combatState?.Enemies is not null)
        {
            foreach (Creature enemy in combatState.Enemies)
            {
                yield return enemy;
            }
        }
    }

    private static IEnumerable<NRewardButton> GetRewardButtons(NRewardsScreen rewardsScreen)
    {
        object? buttons = GetFieldValue(rewardsScreen, "_rewardButtons");
        if (buttons is IEnumerable enumerable)
        {
            foreach (object? entry in enumerable)
            {
                if (entry is NRewardButton rewardButton)
                {
                    yield return rewardButton;
                }
            }
        }
    }

    private static bool IsRewardProceedEnabled(NRewardsScreen rewardsScreen)
    {
        return GetFieldValue(rewardsScreen, "_proceedButton") is NProceedButton { IsEnabled: true } button
            && IsNodeVisible(button);
    }

    private static string BuildRewardId(object reward, int index)
    {
        string rewardType = GetPropertyText(reward, "RewardType");
        string title = GetRewardTitle(reward);
        string description = GetRewardDescription(reward);
        uint hash = 2166136261;
        foreach (char character in $"{index}|{rewardType}|{title}|{description}")
        {
            hash ^= character;
            hash *= 16777619;
        }

        return $"reward_{index.ToString(CultureInfo.InvariantCulture)}_{hash.ToString(CultureInfo.InvariantCulture)}";
    }

    private static string GetRewardTitle(object reward)
    {
        string rewardType = GetPropertyText(reward, "RewardType");
        string subtypeTitle = string.Empty;
        object? potionValue = GetMemberValue(reward, "Potion");
        if (potionValue is not null)
        {
            subtypeTitle = GetLocalizedPropertyTextOrEmpty(potionValue, "Title");
            if (string.IsNullOrEmpty(subtypeTitle))
            {
                subtypeTitle = GetPropertyTextOrEmpty(potionValue, "Name");
            }
        }

        if (!string.IsNullOrEmpty(subtypeTitle) && !subtypeTitle.Contains('.'))
        {
            return subtypeTitle;
        }

        foreach (string propertyName in new[] { "ClaimedRelic", "Potion", "Relic" })
        {
            object? value = GetMemberValue(reward, propertyName);
            string text = GetLocalizedPropertyTextOrEmpty(value, "Title");
            if (!string.IsNullOrEmpty(text))
            {
                return text;
            }

            text = GetPropertyTextOrEmpty(value, "Name");
            if (!string.IsNullOrEmpty(text))
            {
                return text;
            }
        }

        if (string.Equals(rewardType, "Gold", StringComparison.OrdinalIgnoreCase))
        {
            object? amount = GetMemberValue(reward, "Amount");
            return amount is int gold ? $"{gold.ToString(CultureInfo.InvariantCulture)} Gold" : "Gold";
        }

        return rewardType;
    }

    private static string GetRewardDescription(object reward)
    {
        return reward switch
        {
            PotionReward { Potion: { } potion } => GetLocalizedText(potion.DynamicDescription),
            RelicReward { Relic: { } relic } => GetLocalizedText(relic.DynamicDescription),
            _ => GetLocalizedPropertyValue(reward, "Description"),
        };
    }

    private static bool IsRewardSkippable(NRewardsScreen screen) => IsRewardProceedEnabled(screen)
        && GetFieldValue(screen, "_rewardsSet") is RewardsSet { DisallowSkipping: false }
        && GetFieldValue(screen, "_rewardButtons") is ICollection { Count: 1 };

    private static string? GetRewardUnavailableReason(NRewardButton button)
    {
        if (!IsRewardButtonSelectable(button))
        {
            return "reward_unavailable";
        }
        if (button.Reward is PotionReward { Potion: { } potion } reward)
        {
            if (!Hook.ShouldProcurePotion(reward.Player.RunState, reward.Player.Creature.CombatState, potion, reward.Player))
            {
                return "potion_acquisition_blocked";
            }
            if (!reward.Player.HasOpenPotionSlots)
            {
                return "potion_belt_full";
            }
        }
        return null;
    }

    private static bool IsRewardButtonSelectable(NRewardButton button) => button.IsEnabled && IsNodeVisible(button);

    private static bool HasMethod(object source, string methodName)
    {
        return source.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public) is not null;
    }

    private static HashSet<string> BuildVisitedPointIdSet(RunState? runState)
    {
        HashSet<string> visited = [];
        IEnumerable? visitedCoords = runState?.VisitedMapCoords;
        if (visitedCoords is null)
        {
            return visited;
        }

        foreach (object? coord in visitedCoords)
        {
            string? pointId = BuildMapPointId(coord);
            if (!string.IsNullOrEmpty(pointId))
            {
                visited.Add(pointId);
            }
        }

        return visited;
    }

    private static bool CanSelectMapPoint(NMapScreen mapScreen, NMapPoint point) =>
        mapScreen.IsTravelEnabled && !mapScreen.IsTraveling
        && mapScreen.Drawings.GetLocalDrawingMode() == DrawingMode.None
        && point.State == MapPointState.Travelable && point.IsEnabled && point.IsVisibleInTree();

    private static List<string> BuildChildPointIds(object point)
    {
        List<string> children = [];
        if (point.GetType().GetProperty("Children")?.GetValue(point) is not IEnumerable childPoints)
        {
            return children;
        }

        foreach (object? child in childPoints)
        {
            string? childId = BuildMapPointId(child);
            if (!string.IsNullOrEmpty(childId))
            {
                children.Add(childId);
            }
        }

        return children;
    }

    private static string GetMapRoomKind(object point)
    {
        string pointType = GetPropertyText(point, "PointType");
        return pointType switch
        {
            "Monster" => "monster",
            "Unknown" => "event",
            "Shop" => "shop",
            "RestSite" => "rest",
            "Elite" => "elite",
            "Treasure" => "treasure",
            "Boss" => "boss",
            "Ancient" => "ancient",
            _ => pointType.ToLowerInvariant(),
        };
    }

    private static (int? Col, int? Row) GetMapCoordParts(object pointOrCoord)
    {
        object? coord = pointOrCoord.GetType().GetField("coord", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(pointOrCoord) ?? pointOrCoord;
        object? col = coord.GetType().GetField("col", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(coord);
        object? row = coord.GetType().GetField("row", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(coord);
        int? colValue = col is int colInt ? colInt : null;
        int? rowValue = row is int rowInt ? rowInt : null;
        return (colValue, rowValue);
    }

    private static string? BuildMapPointId(object? pointOrCoord)
    {
        if (pointOrCoord is null)
        {
            return null;
        }

        (int? col, int? row) = GetMapCoordParts(pointOrCoord);
        if (col is int colInt && row is int rowInt)
        {
            return $"point_{colInt.ToString(CultureInfo.InvariantCulture)}_{rowInt.ToString(CultureInfo.InvariantCulture)}";
        }

        return null;
    }

    private static string BuildChoiceId(object option, int index)
    {
        string title = GetOptionTitle(option);
        string description = GetOptionDescription(option);
        string textKey = GetPropertyText(option, "TextKey");
        uint hash = 2166136261;
        foreach (char character in $"{index}|{title}|{description}|{textKey}")
        {
            hash ^= character;
            hash *= 16777619;
        }

        return $"choice_{index.ToString(CultureInfo.InvariantCulture)}_{hash.ToString(CultureInfo.InvariantCulture)}";
    }

    private static string GetOptionTitle(object option)
    {
        string title = GetLocalizedPropertyText(option, "Title");
        if (!string.IsNullOrEmpty(title))
        {
            return title;
        }

        return GetPropertyText(option, "TextKey");
    }

    private static string GetOptionDescription(object option)
    {
        if (option is EventOption eventOption)
        {
            // Continuation choices may have no description. Preserve empty
            // localized text instead of falling through to LocString.ToString().
            LocString description = eventOption.Description;
            return description.Exists() ? description.GetFormattedText() : string.Empty;
        }
        return GetLocalizedPropertyText(option, "Description");
    }

    public static bool HasPendingHandSelection => NPlayerHand.Instance?.IsInCardSelection == true;

    private static object? GetActiveCardSelectionScreen()
    {
        object? screen = ActiveScreenContext.Instance.GetCurrentScreen();
        if (screen is NCombatRoom && NPlayerHand.Instance is { IsInCardSelection: true } hand
            && hand.IsVisibleInTree() && !hand.PeekButton.IsPeeking
            && NOverlayStack.Instance?.ScreenCount == 0)
        {
            return hand;
        }
        return screen is not null && IsCardSelectionTypeName(screen.GetType().FullName!) ? screen : null;
    }

    private static IEnumerable<Node> EnumerateNodes(Node root)
    {
        yield return root;
        foreach (Node child in root.GetChildren())
        {
            foreach (Node descendant in EnumerateNodes(child))
            {
                yield return descendant;
            }
        }
    }

    private static bool IsCardSelectionTypeName(string typeName)
    {
        return typeName switch
        {
            "MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NCombatPileCardSelectScreen" => true,
            "MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NCardRewardSelectionScreen" => true,
            "MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NDeckTransformSelectScreen" => true,
            "MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NDeckCardSelectScreen" => true,
            "MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NChooseACardSelectionScreen" => true,
            "MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NSimpleCardSelectScreen" => true,
            "MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NDeckUpgradeSelectScreen" => true,
            "MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NDeckEnchantSelectScreen" => true,
            _ => false,
        };
    }

    private static bool IsCardHolderTypeName(string typeName)
    {
        return typeName switch
        {
            "MegaCrit.Sts2.Core.Nodes.Cards.Holders.NCardHolder" => true,
            "MegaCrit.Sts2.Core.Nodes.Cards.Holders.NGridCardHolder" => true,
            _ => false,
        };
    }

    private static IEnumerable GetCardSelectionItems(object screen)
    {
        if (screen is NPlayerHand hand)
        {
            List<CardModel> selected = (List<CardModel>)GetFieldValue(hand, "_selectedCards")!;
            var filter = (Func<CardModel, bool>?)GetFieldValue(hand, "_currentSelectionFilter");
            return hand.ActiveHolders.Select(holder => holder.CardNode?.Model)
                .OfType<CardModel>().Concat(selected).Distinct()
                .Where(card => card.Pile?.Type == PileType.Hand && (filter?.Invoke(card) ?? true)).ToArray();
        }

        if (screen is NCombatPileCardSelectScreen)
        {
            // _cards stays empty on this screen. The grid tracks the native
            // filter and live pile changes; don't offer the whole source pile.
            NCardGrid grid = (NCardGrid)GetFieldValue(screen, "_grid")!;
            CardPile pile = (CardPile)GetFieldValue(screen, "_pile")!;
            IEnumerable<CardModel> cards = grid.CurrentlyDisplayedCards;
            if (pile.Type == PileType.Draw)
            {
                // Duplicate cards must not disclose their relative draw order.
                cards = cards.OrderBy(card => card.Id.Entry, StringComparer.Ordinal)
                    .ThenBy(CardStateExporter.Id, StringComparer.Ordinal);
            }
            return cards.ToArray();
        }

        foreach (string fieldName in new[] { "_cards", "_cardResults", "_options", "_extraOptions" })
        {
            object? cards = GetFieldValue(screen, fieldName);
            if (cards is IEnumerable enumerable)
            {
                return enumerable;
            }
        }

        return Array.Empty<object>();
    }

    private static object? GetFieldValue(object source, string fieldName)
    {
        Type? currentType = source.GetType();
        while (currentType is not null)
        {
            FieldInfo? field = currentType.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (field is not null)
            {
                return field.GetValue(source);
            }

            currentType = currentType.BaseType;
        }

        return null;
    }

    private static string GetCardSelectionKind(object screen)
    {
        return screen.GetType().Name switch
        {
            "NCardRewardSelectionScreen" => "card_reward",
            "NDeckTransformSelectScreen" => "deck_transform",
            "NDeckCardSelectScreen" => "deck_card_select",
            "NChooseACardSelectionScreen" => "choose_a_card",
            "NSimpleCardSelectScreen" => "simple_card_select",
            "NDeckUpgradeSelectScreen" => "deck_upgrade",
            "NDeckEnchantSelectScreen" => "deck_enchant",
            _ => "card_selection",
        };
    }

    private static string GetCardSelectionPrompt(object screen, object? prefs, List<CardSnapshot> cards)
    {
        string prompt = GetLocalizedPropertyValue(prefs, "Prompt");
        if (!string.IsNullOrEmpty(prompt))
        {
            return prompt;
        }

        string infoLabel = GetNodeText(GetFieldValue(screen, "_infoLabel"));
        if (!string.IsNullOrEmpty(infoLabel))
        {
            return infoLabel;
        }

        // Read this selector's banner, not an offered card's on-play prompt
        // (e.g. Headbutt's discard recovery prompt in an ordinary card reward).
        string banner = GetNodeText(GetMemberValue(GetFieldValue(screen, "_banner"), "label"));
        if (!string.IsNullOrEmpty(banner))
        {
            return banner;
        }

        if (cards.Count > 0)
        {
            return "Choose a card";
        }

        return string.Empty;
    }

    private static int GetCardSelectionMinSelect(string kind, object? prefs)
    {
        int minSelect = GetIntPropertyValue(prefs, "MinSelect");
        if (minSelect > 0)
        {
            return minSelect;
        }

        return kind == "card_reward" ? 1 : 0;
    }

    private static int GetCardSelectionMaxSelect(string kind, object? prefs)
    {
        int maxSelect = GetIntPropertyValue(prefs, "MaxSelect");
        if (maxSelect > 0)
        {
            return maxSelect;
        }

        return kind == "card_reward" ? 1 : 0;
    }

    private static string GetNodeText(object? nodeLike)
    {
        if (nodeLike is null)
        {
            return string.Empty;
        }

        foreach (string propertyName in new[] { "Text", "text", "Title" })
        {
            string text = GetLocalizedPropertyValue(nodeLike, propertyName);
            if (!string.IsNullOrEmpty(text))
            {
                return text;
            }
        }

        return string.Empty;
    }

    private static string GetLocalizedPropertyValue(object? source, string propertyName)
    {
        if (source is null)
        {
            return string.Empty;
        }

        object? value = source.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(source);
        return GetLocalizedText(value);
    }

    private static string GetRawLocalizedPropertyValue(object? source, string propertyName)
    {
        if (source is null)
        {
            return string.Empty;
        }

        object? value = source.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(source);
        return GetRawLocalizedText(value);
    }

    private static int GetIntPropertyValue(object? source, string propertyName)
    {
        if (source is null)
        {
            return 0;
        }

        object? value = source.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(source);
        return value is int intValue ? intValue : 0;
    }

    private static bool GetBoolPropertyValue(object? source, string propertyName)
    {
        if (source is null)
        {
            return false;
        }

        object? value = source.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(source);
        return value is bool boolValue && boolValue;
    }

    private static MethodInfo? FindSingleParameterMethod(Type type, string methodName)
    {
        Type? currentType = type;
        while (currentType is not null)
        {
            foreach (MethodInfo method in currentType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (string.Equals(method.Name, methodName, StringComparison.Ordinal) && method.GetParameters().Length == 1)
                {
                    return method;
                }
            }

            currentType = currentType.BaseType;
        }

        return null;
    }

    private static void InvokeParameterlessMethod(object source, string methodName)
    {
        MethodInfo? method = source.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
        method?.Invoke(source, null);
    }

    private static bool IsNodeVisible(Node node)
    {
        MethodInfo? isVisibleInTree = node.GetType().GetMethod("IsVisibleInTree", BindingFlags.Instance | BindingFlags.Public);
        if (isVisibleInTree?.Invoke(node, null) is bool visibleInTree)
        {
            return visibleInTree;
        }

        return node.IsInsideTree();
    }

    private static string GetStringProperty(object source, string propertyName)
    {
        if (source.GetType().GetProperty(propertyName)?.GetValue(source) is string value)
        {
            return value;
        }

        return string.Empty;
    }

    private static string GetPropertyText(object source, string propertyName)
    {
        object? value = GetMemberValue(source, propertyName);
        return value?.ToString() ?? string.Empty;
    }

    private static string GetPropertyTextOrEmpty(object? source, string propertyName)
    {
        if (source is null)
        {
            return string.Empty;
        }

        object? value = GetMemberValue(source, propertyName);
        return value?.ToString() ?? string.Empty;
    }

    private static string GetLocalizedPropertyText(object source, string propertyName)
    {
        object? value = GetMemberValue(source, propertyName);
        return GetLocalizedText(value);
    }

    private static string GetLocalizedPropertyTextOrEmpty(object? source, string propertyName)
    {
        if (source is null)
        {
            return string.Empty;
        }

        object? value = GetMemberValue(source, propertyName);
        return GetLocalizedText(value);
    }

    private static object? GetMemberValue(object? source, string memberName)
    {
        if (source is null)
        {
            return null;
        }

        Type? currentType = source.GetType();
        while (currentType is not null)
        {
            PropertyInfo? property = currentType.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property is not null)
            {
                return property.GetValue(source);
            }

            FieldInfo? field = currentType.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field is not null)
            {
                return field.GetValue(source);
            }

            MethodInfo? getter = currentType.GetMethod($"get_{memberName}", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            if (getter is not null)
            {
                return getter.Invoke(source, null);
            }

            currentType = currentType.BaseType;
        }

        return null;
    }

    private static string GetFirstLocalizedPropertyText(object source, params string[] propertyNames)
    {
        foreach (string propertyName in propertyNames)
        {
            string text = GetLocalizedPropertyText(source, propertyName);
            if (!string.IsNullOrEmpty(text) && !text.Contains("LocString", StringComparison.Ordinal))
            {
                return text;
            }
        }

        foreach (string propertyName in propertyNames)
        {
            string text = GetLocalizedPropertyText(source, propertyName);
            if (!string.IsNullOrEmpty(text))
            {
                return text;
            }
        }

        return string.Empty;
    }

    private static bool GetBoolProperty(object source, string propertyName)
    {
        if (source.GetType().GetProperty(propertyName)?.GetValue(source) is bool value)
        {
            return value;
        }

        return false;
    }

    private static MethodInfo? GetEventOptionButtonClickedMethod(Type eventRoomType, Type optionType)
    {
        foreach (MethodInfo method in eventRoomType.GetMethods(BindingFlags.Instance | BindingFlags.Public))
        {
            if (!string.Equals(method.Name, "OptionButtonClicked", StringComparison.Ordinal))
            {
                continue;
            }

            ParameterInfo[] parameters = method.GetParameters();
            if (parameters.Length != 2)
            {
                continue;
            }

            if (parameters[0].ParameterType.IsAssignableFrom(optionType) && parameters[1].ParameterType == typeof(int))
            {
                return method;
            }
        }

        return null;
    }

    private static string ReadCostValue(object? costValue)
    {
        if (costValue is null)
        {
            return string.Empty;
        }

        if (costValue is int intValue)
        {
            return intValue.ToString(CultureInfo.InvariantCulture);
        }

        Type type = costValue.GetType();
        foreach (string propertyName in new[] { "Value", "Current", "Effective", "Canonical" })
        {
            object? value = type.GetProperty(propertyName)?.GetValue(costValue);
            if (value is int propertyInt)
            {
                return propertyInt.ToString(CultureInfo.InvariantCulture);
            }
        }

        return string.Empty;
    }

    private static string GetLocalizedText(object? value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        if (value is string text)
        {
            return text;
        }

        MethodInfo? getFormattedText = value.GetType().GetMethod("GetFormattedText", BindingFlags.Instance | BindingFlags.Public);
        if (getFormattedText is not null)
        {
            try
            {
                if (getFormattedText.Invoke(value, null) is string formatted && !string.IsNullOrEmpty(formatted))
                {
                    return formatted;
                }
            }
            catch
            {
            }
        }

        MethodInfo? getRawText = value.GetType().GetMethod("GetRawText", BindingFlags.Instance | BindingFlags.Public);
        if (getRawText is not null)
        {
            try
            {
                if (getRawText.Invoke(value, null) is string rawText && !string.IsNullOrEmpty(rawText))
                {
                    return rawText;
                }
            }
            catch
            {
            }
        }

        return value.ToString() ?? string.Empty;
    }

    private static string GetRawLocalizedText(object? value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        if (value is string text)
        {
            return text;
        }

        MethodInfo? getRawText = value.GetType().GetMethod("GetRawText", BindingFlags.Instance | BindingFlags.Public);
        if (getRawText is not null)
        {
            try
            {
                if (getRawText.Invoke(value, null) is string rawText && !string.IsNullOrEmpty(rawText))
                {
                    return rawText;
                }
            }
            catch
            {
            }
        }

        return value.ToString() ?? string.Empty;
    }
}
