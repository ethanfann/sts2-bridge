using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.RestSite;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Runs;

namespace FirstMod.Bridge;

internal static class BridgeIntrospection
{
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
        object? currentRoom = GetCurrentRoom(runState);
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
                Locked = GetBoolProperty(option, "IsLocked"),
                Proceed = GetBoolProperty(option, "IsProceed"),
                WillKillPlayer = GetBoolProperty(option, "WillKillPlayer"),
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

        IEnumerable screenCards = GetCardSelectionItems(screen);
        List<CardSnapshot> cards = [];
        int index = 0;
        foreach (object? screenCard in screenCards)
        {
            if (screenCard is null)
            {
                index += 1;
                continue;
            }

            cards.Add(new CardSnapshot
            {
                Id = BuildCardId(screenCard, index),
                Name = GetCardName(screenCard),
                Description = GetCardDescription(screenCard),
                Cost = GetCardCostText(screenCard),
                Rarity = GetCardRarity(screenCard),
                Playable = true,
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
            Cards = cards,
        };
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
        NRestSiteRoom? roomNode = NRestSiteRoom.Instance;
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
                    Description = GetRawLocalizedText(restOption.Description),
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

        bool chestOpenAvailable = GetFieldValue(treasureRoom, "_chestButton") is Node chestButton && IsNodeVisible(chestButton);
        bool proceedAvailable = treasureRoom.ProceedButton is Node proceedButton && IsNodeVisible(proceedButton);
        return new TreasureContextSnapshot
        {
            ChestOpenAvailable = chestOpenAvailable,
            ProceedAvailable = proceedAvailable,
            Relics = BuildTreasureRelics(treasureRoom),
        };
    }

    public static List<PotionSnapshot> BuildPotionSnapshots(Player? player)
    {
        List<PotionSnapshot> potions = [];
        if (player?.Potions is null)
        {
            return potions;
        }

        int index = 0;
        foreach (object? potionLike in player.Potions)
        {
            if (potionLike is not PotionModel potion)
            {
                continue;
            }

            string title = GetLocalizedText(potion.Title);
            potions.Add(new PotionSnapshot
            {
                Id = BuildPotionId(title, index),
                Title = title,
                Description = GetLocalizedText(potion.DynamicDescription),
                Rarity = potion.Rarity.ToString(),
                Usage = potion.Usage.ToString(),
                TargetType = potion.TargetType.ToString(),
            });
            index += 1;
        }

        return potions;
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

            rewards.Add(new RewardSnapshot
            {
                Id = BuildRewardId(reward, index),
                RewardType = GetPropertyText(reward, "RewardType"),
                Title = GetRewardTitle(reward),
                Description = GetRewardDescription(reward),
                Skippable = IsRewardSkippable(reward),
                Selectable = IsRewardButtonSelectable(button),
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
        NMerchantRoom? merchantRoom = GetActiveMerchantRoom();
        if (merchantRoom is null)
        {
            return null;
        }

        NMerchantInventory? inventory = merchantRoom.Inventory;
        bool inventoryOpen = inventory is not null && inventory.IsOpen && IsNodeVisible(inventory);
        bool enterShopAvailable = !inventoryOpen && merchantRoom.MerchantButton is Node merchantButton && IsNodeVisible(merchantButton);
        bool leaveAvailable = inventoryOpen;
        bool proceedAvailable = !inventoryOpen && merchantRoom.ProceedButton is Node proceedButton && IsNodeVisible(proceedButton);
        List<MerchantItemSnapshot> items = inventoryOpen && inventory is not null ? BuildMerchantItems(inventory) : [];

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
        if (mapScreen is null || !IsNodeVisible(mapScreen) || !mapScreen.IsOpen)
        {
            return null;
        }

        object? actMap = runState?.Map;
        if (actMap is null)
        {
            return null;
        }

        HashSet<string> visitedPointIds = BuildVisitedPointIdSet(runState);
        HashSet<string> travelablePointIds = BuildTravelablePointIdSet(mapScreen);
        string? currentPointId = BuildMapPointId(runState?.CurrentMapCoord);

        List<MapPointSnapshot> points = [];
        IEnumerable allPoints = GetAllMapPoints(actMap);
        foreach (object? point in allPoints)
        {
            if (point is null)
            {
                continue;
            }

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
                Travelable = travelablePointIds.Contains(pointId),
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
        if (eventRoom is not null && IsNodeVisible(eventRoom))
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

        NMerchantRoom? merchantRoom = GetActiveMerchantRoom();
        if (TryProceedMerchantRoom(merchantRoom, runState))
        {
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
        if (treasureRoom is not null && treasureRoom.ProceedButton is Node treasureProceedButton && IsNodeVisible(treasureProceedButton))
        {
            MethodInfo? proceedPressed = treasureRoom.GetType().GetMethod("OnProceedButtonPressed", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            MethodInfo? proceedReleased = treasureRoom.GetType().GetMethod("OnProceedButtonReleased", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            proceedPressed?.Invoke(treasureRoom, new object?[] { null });
            proceedReleased?.Invoke(treasureRoom, new object?[] { null });
            return proceedPressed is not null || proceedReleased is not null;
        }

        if (TryProceedPostCombat(runState))
        {
            return true;
        }

        NEventRoom? eventRoom = NEventRoom.Instance;
        if (eventRoom is not null && IsNodeVisible(eventRoom))
        {
            MethodInfo? proceedMethod = eventRoom.GetType().GetMethod("Proceed", BindingFlags.Public | BindingFlags.Static);
            proceedMethod?.Invoke(null, null);
            return proceedMethod is not null;
        }

        return false;
    }

    public static bool TryEnterMerchant()
    {
        NMerchantRoom? merchantRoom = GetActiveMerchantRoom();
        if (merchantRoom is null)
        {
            return false;
        }

        if (merchantRoom.Inventory is not null && merchantRoom.Inventory.IsOpen)
        {
            return true;
        }

        MethodInfo? openInventory = merchantRoom.GetType().GetMethod("OpenInventory", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
        if (openInventory is not null)
        {
            openInventory.Invoke(merchantRoom, null);
            return true;
        }

        Node? merchantButton = merchantRoom.MerchantButton;
        if (merchantButton is null || !IsNodeVisible(merchantButton))
        {
            return false;
        }

        MethodInfo? onRelease = merchantButton.GetType().GetMethod("OnRelease", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
        if (onRelease is null)
        {
            return false;
        }

        onRelease.Invoke(merchantButton, null);
        return true;
    }

    private static bool TryProceedMerchantRoom(NMerchantRoom? merchantRoom, RunState? runState)
    {
        if (merchantRoom is null)
        {
            return false;
        }

        if (merchantRoom.ProceedButton is Node merchantProceedButton && IsNodeVisible(merchantProceedButton))
        {
            MethodInfo? onPress = merchantProceedButton.GetType().GetMethod("OnPress", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            MethodInfo? onRelease = merchantProceedButton.GetType().GetMethod("OnRelease", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            onPress?.Invoke(merchantProceedButton, null);
            if (onRelease is not null)
            {
                onRelease.Invoke(merchantProceedButton, null);
                InvokeParameterlessMethod(merchantRoom, "OnActiveScreenUpdated");
                TryInvokeHideScreen(merchantRoom);
                return true;
            }
        }

        foreach (string methodName in new[] { "OnProceedButtonPressed", "OnProceedPressed", "OnProceedButtonReleased", "Proceed" })
        {
            if (TryInvokeSemanticMerchantProceed(merchantRoom, methodName, merchantRoom.ProceedButton))
            {
                return true;
            }
        }

        object? room = GetMemberValue(merchantRoom, "Room");
        if (runState is not null && room is not null)
        {
            MethodInfo? exitMethod = room.GetType().GetMethod("Exit", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { runState.GetType() }, null);
            if (exitMethod is not null)
            {
                exitMethod.Invoke(room, new object[] { runState });
                return true;
            }
        }

        if (TryInvokeHideScreen(merchantRoom))
        {
            return true;
        }

        return false;
    }

    private static bool TryInvokeSemanticMerchantProceed(NMerchantRoom merchantRoom, string methodName, Node? proceedButton)
    {
        foreach (MethodInfo method in merchantRoom.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (!string.Equals(method.Name, methodName, StringComparison.Ordinal))
            {
                continue;
            }

            ParameterInfo[] parameters = method.GetParameters();
            if (parameters.Length == 0)
            {
                method.Invoke(merchantRoom, null);
                return true;
            }

            if (parameters.Length == 1)
            {
                object? argument = null;
                Type parameterType = parameters[0].ParameterType;
                if (proceedButton is not null && parameterType.IsInstanceOfType(proceedButton))
                {
                    argument = proceedButton;
                }

                method.Invoke(merchantRoom, new[] { argument });
                return true;
            }
        }

        return false;
    }

    private static bool TryInvokeHideScreen(object source)
    {
        foreach (MethodInfo method in source.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (!string.Equals(method.Name, "HideScreen", StringComparison.Ordinal))
            {
                continue;
            }

            ParameterInfo[] parameters = method.GetParameters();
            if (parameters.Length == 0)
            {
                method.Invoke(source, null);
                return true;
            }

            if (parameters.Length == 1)
            {
                method.Invoke(source, new object?[] { null });
                return true;
            }
        }

        return false;
    }

    public static bool TryLeaveMerchant()
    {
        NMerchantInventory? inventory = GetActiveMerchantInventory();
        if (inventory is null)
        {
            return false;
        }

        MethodInfo? close = inventory.GetType().GetMethod("Close", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
        if (close is null)
        {
            return false;
        }

        close.Invoke(inventory, null);
        return true;
    }

    public static bool TryPurchaseMerchantItem(string itemId)
    {
        NMerchantInventory? inventory = GetActiveMerchantInventory();
        if (inventory is null)
        {
            return false;
        }

        if (!TryGetMerchantSlot(inventory, itemId, out NMerchantSlot? slot) || slot is null)
        {
            return false;
        }

        object? entry = slot.Entry;
        if (entry is null || !GetMerchantItemAffordable(entry) || !GetMerchantItemStocked(entry))
        {
            return false;
        }

        MethodInfo? onRelease = slot.GetType().GetMethod("OnReleased", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
        if (onRelease is not null)
        {
            onRelease.Invoke(slot, null);
            return true;
        }

        MethodInfo? onTryPurchase = slot.GetType().GetMethod("OnTryPurchase", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(MerchantInventory) }, null);
        if (onTryPurchase is not null)
        {
            MerchantInventory? merchantInventory = inventory.Inventory;
            if (merchantInventory is null)
            {
                return false;
            }

            onTryPurchase.Invoke(slot, new object[] { merchantInventory });
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

    public static bool TryOpenTreasureChest()
    {
        NTreasureRoom? room = GetActiveTreasureRoom();
        if (room is null)
        {
            return false;
        }

        MethodInfo? openChest = room.GetType().GetMethod("OpenChest", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
        if (openChest is not null)
        {
            openChest.Invoke(room, null);
            return true;
        }

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

        MethodInfo? onPress = holder.GetType().GetMethod("OnPress", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
        MethodInfo? onRelease = holder.GetType().GetMethod("OnRelease", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
        onPress?.Invoke(holder, null);
        onRelease?.Invoke(holder, null);
        return onRelease is not null || onPress is not null;
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

        int index = 0;
        foreach (object? potionLike in player.Potions)
        {
            if (potionLike is not PotionModel potion)
            {
                continue;
            }

            string title = GetLocalizedText(potion.Title);
            if (!string.Equals(BuildPotionId(title, index), potionId, StringComparison.Ordinal))
            {
                index += 1;
                continue;
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

    public static bool TryTakeReward(string rewardId)
    {
        if (!TryGetRewardButton(rewardId, out NRewardsScreen? rewardsScreen, out NRewardButton? button) || rewardsScreen is null || button is null)
        {
            return false;
        }

        MethodInfo? onRelease = button.GetType().GetMethod("OnRelease", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
        if (onRelease is not null)
        {
            onRelease.Invoke(button, null);
            return true;
        }

        rewardsScreen.RewardCollectedFrom(button);
        return true;
    }

    public static bool TrySkipReward(string rewardId)
    {
        if (!TryGetRewardButton(rewardId, out NRewardsScreen? rewardsScreen, out NRewardButton? button) || rewardsScreen is null || button is null)
        {
            return false;
        }

        object? reward = button.Reward;
        if (reward is not null)
        {
            MethodInfo? onSkipped = reward.GetType().GetMethod("OnSkipped", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            if (onSkipped is not null)
            {
                onSkipped.Invoke(reward, null);
                return true;
            }
        }

        rewardsScreen.RewardSkippedFrom(button);
        return true;
    }

    public static bool TrySelectMapPoint(RunState? runState, string pointId)
    {
        NMapScreen? mapScreen = NMapScreen.Instance;
        if (mapScreen is null || !IsNodeVisible(mapScreen) || !mapScreen.IsOpen || !mapScreen.IsTravelEnabled || mapScreen.IsTraveling)
        {
            return false;
        }

        object? actMap = runState?.Map;
        if (actMap is null)
        {
            return false;
        }

        MapCoord? coord = ParseMapCoord(pointId);
        if (coord is not MapCoord resolvedCoord)
        {
            return false;
        }

        mapScreen.TravelToMapCoord(resolvedCoord);
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
        InvokeParameterlessMethod(screen, "CheckIfSelectionComplete");

        return true;
    }

    public static ChoiceContextSnapshot? BuildChoiceContext(RunState? runState)
    {
        object? eventModel = GetEventModel(runState);
        if (eventModel is null)
        {
            return null;
        }

        return new ChoiceContextSnapshot
        {
            Kind = DetermineNonCombatScene(runState),
            Title = GetLocalizedPropertyText(eventModel, "Title"),
            Description = GetFirstLocalizedPropertyText(eventModel, "Description", "InitialDescription"),
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
        if (eventRoom is null)
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
        int? stableId = TryGetStableCardId(handItem);
        if (stableId is int id)
        {
            return $"card_{id.ToString(CultureInfo.InvariantCulture)}";
        }

        return $"hand_{index.ToString(CultureInfo.InvariantCulture)}";
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
        object? directPlayable = handItem.GetType().GetProperty("IsPlayable")?.GetValue(handItem);
        if (directPlayable is bool boolValue)
        {
            return boolValue;
        }

        CardModel? model = GetCardModel(handItem);
        if (model is null)
        {
            return false;
        }

        try
        {
            return model.CanPlay();
        }
        catch
        {
            object? reflectedPlayable = model.GetType().GetProperty("IsPlayable")?.GetValue(model);
            if (reflectedPlayable is bool reflectedBoolValue)
            {
                return reflectedBoolValue;
            }

            return false;
        }
    }

    public static string BuildCreatureId(Creature creature, int index)
    {
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
        object? eventModel = GetMutableEventModel(runState);
        if (eventModel is null)
        {
            return null;
        }

        try
        {
            return eventModel.GetType().GetProperty("CurrentOptions")?.GetValue(eventModel) as IEnumerable;
        }
        catch
        {
            return null;
        }
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
        if (Engine.GetMainLoop() is not SceneTree tree)
        {
            return null;
        }

        NRewardsScreen? activeScreen = null;
        foreach (Node node in EnumerateNodes(tree.Root))
        {
            if (node is NRewardsScreen rewardsScreen && IsNodeVisible(rewardsScreen) && !rewardsScreen.IsComplete)
            {
                activeScreen = rewardsScreen;
            }
        }

        return activeScreen;
    }

    private static NMerchantRoom? GetActiveMerchantRoom()
    {
        NMerchantRoom? merchantRoom = NMerchantRoom.Instance;
        if (merchantRoom is null || !IsNodeVisible(merchantRoom))
        {
            return null;
        }

        return merchantRoom;
    }

    private static NMerchantInventory? GetActiveMerchantInventory()
    {
        NMerchantRoom? merchantRoom = GetActiveMerchantRoom();
        NMerchantInventory? inventory = merchantRoom?.Inventory;
        if (inventory is null || !inventory.IsOpen || !IsNodeVisible(inventory))
        {
            return null;
        }

        return inventory;
    }

    private static NRestSiteRoom? GetActiveRestSiteRoom()
    {
        NRestSiteRoom? room = NRestSiteRoom.Instance;
        return room is not null && IsNodeVisible(room) ? room : null;
    }

    private static NTreasureRoom? GetActiveTreasureRoom()
    {
        if (Engine.GetMainLoop() is not SceneTree tree)
        {
            return null;
        }

        NTreasureRoom? active = null;
        foreach (Node node in EnumerateNodes(tree.Root))
        {
            if (node is NTreasureRoom treasureRoom && IsNodeVisible(treasureRoom))
            {
                active = treasureRoom;
            }
        }

        return active;
    }

    private static NChooseARelicSelection? GetActiveRelicSelectionScreen()
    {
        if (Engine.GetMainLoop() is not SceneTree tree)
        {
            return null;
        }

        NChooseARelicSelection? active = null;
        foreach (Node node in EnumerateNodes(tree.Root))
        {
            if (node is NChooseARelicSelection relicSelection && IsNodeVisible(relicSelection))
            {
                active = relicSelection;
            }
        }

        return active;
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
        object? collection = GetFieldValue(room, "_relicCollection");
        if (collection is not Node collectionNode || !IsNodeVisible(collectionNode))
        {
            return relics;
        }

        int index = 0;
        foreach (Node node in EnumerateNodes(collectionNode))
        {
            if (node is not NTreasureRoomRelicHolder holder || holder.Relic is null)
            {
                continue;
            }

            relics.Add(BuildRelicSnapshot(holder.Relic, index));
            index += 1;
        }

        return relics;
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
            Description = GetRawLocalizedPropertyValue(model, "Description"),
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
        object? collection = GetFieldValue(room, "_relicCollection");
        if (collection is not Node collectionNode)
        {
            return false;
        }

        int index = 0;
        foreach (Node node in EnumerateNodes(collectionNode))
        {
            if (node is not NTreasureRoomRelicHolder relicHolder || relicHolder.Relic is null)
            {
                continue;
            }

            if (string.Equals(BuildRelicSnapshot(relicHolder.Relic, index).Id, relicId, StringComparison.Ordinal))
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
            string description = GetMerchantItemDescription(slot, entry, kind);
            int cost = GetMerchantItemCost(entry);
            bool affordable = GetMerchantItemAffordable(entry);
            bool stocked = GetMerchantItemStocked(entry);

            items.Add(new MerchantItemSnapshot
            {
                Id = BuildMerchantItemId(kind, title, cost, index),
                Kind = kind,
                Title = title,
                Description = description,
                Cost = cost,
                Affordable = affordable,
                Purchasable = affordable && stocked,
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

    private static string BuildPotionId(string title, int index)
    {
        return BuildStableId("potion", title, index);
    }

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

    private static string GetMerchantItemDescription(NMerchantSlot slot, object entry, string kind)
    {
        object? visual = GetMemberValue(slot, "Visual");
        if (kind == "remove_card")
        {
            string text = GetRawLocalizedPropertyValue(visual, "Description");
            if (!string.IsNullOrEmpty(text))
            {
                return text;
            }

            return "Remove a card from your deck.";
        }

        if (kind == "card")
        {
            object? creationResult = GetMemberValue(entry, "CreationResult");
            object? card = GetMemberValue(creationResult, "Card");
            if (card is not null)
            {
                return GetCardDescription(card);
            }
        }

        foreach (string memberName in new[] { "Model", "Potion", "Relic" })
        {
            object? model = GetMemberValue(entry, memberName);
            string text = GetRawLocalizedPropertyValue(model, "Description");
            if (!string.IsNullOrEmpty(text))
            {
                return text;
            }
        }

        return string.Empty;
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
        if (combatRoom is null || !IsNodeVisible(combatRoom))
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
        object? proceedButton = GetFieldValue(rewardsScreen, "_proceedButton");
        if (proceedButton is null)
        {
            return false;
        }

        foreach (string propertyName in new[] { "Disabled", "IsDisabled" })
        {
            object? value = proceedButton.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(proceedButton);
            if (value is bool disabled)
            {
                return !disabled;
            }
        }

        return GetBoolPropertyValue(proceedButton, "Visible") || GetBoolPropertyValue(proceedButton, "ButtonPressed");
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
        return GetLocalizedPropertyValue(reward, "Description");
    }

    private static bool IsRewardSkippable(object reward)
    {
        object? canSkip = reward.GetType().GetProperty("CanSkip", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(reward);
        if (canSkip is bool canSkipValue)
        {
            return canSkipValue;
        }

        return HasMethod(reward, "OnSkipped");
    }

    private static bool IsRewardButtonSelectable(NRewardButton button)
    {
        foreach (string propertyName in new[] { "IsDisabled", "Disabled" })
        {
            object? value = button.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(button);
            if (value is bool disabled)
            {
                return !disabled;
            }
        }

        foreach (string propertyName in new[] { "IsInteractable", "Interactable" })
        {
            object? value = button.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(button);
            if (value is bool interactable)
            {
                return interactable;
            }
        }

        return true;
    }

    private static bool HasMethod(object source, string methodName)
    {
        return source.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public) is not null;
    }

    private static IEnumerable GetAllMapPoints(object actMap)
    {
        MethodInfo? getAllMapPoints = actMap.GetType().GetMethod("GetAllMapPoints", BindingFlags.Instance | BindingFlags.Public);
        if (getAllMapPoints?.Invoke(actMap, null) is IEnumerable points)
        {
            return points;
        }

        return Array.Empty<object>();
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

    private static HashSet<string> BuildTravelablePointIdSet(NMapScreen mapScreen)
    {
        HashSet<string> travelable = [];
        foreach (Node node in EnumerateNodes(mapScreen))
        {
            string typeName = node.GetType().FullName ?? node.GetType().Name;
            if (!string.Equals(typeName, "MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapPoint", StringComparison.Ordinal) &&
                !string.Equals(typeName, "MegaCrit.Sts2.Core.Nodes.Screens.Map.NNormalMapPoint", StringComparison.Ordinal) &&
                !string.Equals(typeName, "MegaCrit.Sts2.Core.Nodes.Screens.Map.NBossMapPoint", StringComparison.Ordinal) &&
                !string.Equals(typeName, "MegaCrit.Sts2.Core.Nodes.Screens.Map.NAncientMapPoint", StringComparison.Ordinal))
            {
                continue;
            }

            object? point = node.GetType().GetProperty("Point", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(node);
            bool isTravelable = node.GetType().GetProperty("IsTravelable", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(node) is bool value && value;
            string? pointId = BuildMapPointId(point);
            if (isTravelable && !string.IsNullOrEmpty(pointId))
            {
                travelable.Add(pointId);
            }
        }

        return travelable;
    }

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

    private static MapCoord? ParseMapCoord(string pointId)
    {
        string[] parts = pointId.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || parts[0] != "point")
        {
            return null;
        }

        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int col))
        {
            return null;
        }

        if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int row))
        {
            return null;
        }

        return new MapCoord(col, row);
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
        return GetLocalizedPropertyText(option, "Description");
    }

    private static object? GetActiveCardSelectionScreen()
    {
        if (Engine.GetMainLoop() is not SceneTree tree)
        {
            return null;
        }

        object? activeScreen = null;
        foreach (Node node in EnumerateNodes(tree.Root))
        {
            string typeName = node.GetType().FullName ?? node.GetType().Name;
            if (IsCardSelectionTypeName(typeName) && IsNodeVisible(node))
            {
                activeScreen = node;
            }
        }

        return activeScreen;
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

        object? options = GetFieldValue(screen, "_options");
        if (options is IEnumerable optionEnumerable)
        {
            foreach (object? option in optionEnumerable)
            {
                if (option is null)
                {
                    continue;
                }

                CardModel? cardModel = GetCardModel(option);
                if (cardModel is not null)
                {
                    try
                    {
                        object? selectionPromptValue = GetMemberValue(cardModel, "SelectionScreenPrompt");
                        string selectionPrompt = GetLocalizedText(selectionPromptValue);
                        if (!string.IsNullOrEmpty(selectionPrompt))
                        {
                            return selectionPrompt;
                        }
                    }
                    catch
                    {
                    }
                }
            }
        }

        string infoLabel = GetNodeText(GetFieldValue(screen, "_infoLabel"));
        if (!string.IsNullOrEmpty(infoLabel))
        {
            return infoLabel;
        }

        string banner = GetNodeText(GetFieldValue(screen, "_banner"));
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

    private static int? TryGetStableCardId(object handItem)
    {
        CardModel? model = GetCardModel(handItem);
        foreach (object candidate in EnumerateCardIdCandidates(handItem, model))
        {
            int? stableId = TryGetStableCardIdFromCandidate(candidate);
            if (stableId is not null)
            {
                return stableId;
            }
        }

        return null;
    }

    private static IEnumerable<object> EnumerateCardIdCandidates(object handItem, CardModel? model)
    {
        yield return handItem;

        if (model is not null && !ReferenceEquals(model, handItem))
        {
            yield return model;
        }

        foreach (string propertyName in new[] { "NetCombatCard", "Card", "Model" })
        {
            object? value = handItem.GetType().GetProperty(propertyName)?.GetValue(handItem);
            if (value is not null && !ReferenceEquals(value, handItem) && !ReferenceEquals(value, model))
            {
                yield return value;
            }
        }
    }

    private static int? TryGetStableCardIdFromCandidate(object candidate)
    {
        Type? dbType = candidate.GetType().Assembly.GetType("MegaCrit.Sts2.Core.GameActions.Multiplayer.NetCombatCardDb");
        object? instance = dbType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        if (instance is null || dbType is null)
        {
            return null;
        }

        MethodInfo? tryGetCardId = dbType.GetMethod("TryGetCardId", BindingFlags.Public | BindingFlags.Instance);
        if (tryGetCardId is not null)
        {
            ParameterInfo[] parameters = tryGetCardId.GetParameters();
            if (parameters.Length == 2)
            {
                object?[] args = { candidate, 0 };
                try
                {
                    if (tryGetCardId.Invoke(instance, args) is bool found && found && args[1] is int tryId)
                    {
                        return tryId;
                    }
                }
                catch
                {
                }
            }
        }

        MethodInfo? getCardId = dbType.GetMethod("GetCardId", BindingFlags.Public | BindingFlags.Instance);
        if (getCardId is null)
        {
            return null;
        }

        try
        {
            object? result = getCardId.Invoke(instance, new[] { candidate });
            if (result is int id)
            {
                return id;
            }

            return null;
        }
        catch
        {
            return null;
        }
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
