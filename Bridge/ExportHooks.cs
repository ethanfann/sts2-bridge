using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Events.Custom;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.RestSite;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2Bridge.Bridge;

internal static class ExportHooks
{
    private static bool _applied;
    private static IntPtr _linuxUnwindLibrary;

    [DllImport("libdl.so.2", EntryPoint = "dlopen")]
    private static extern IntPtr Dlopen(string filename, int flags);

    [DllImport("libdl.so.2", EntryPoint = "dlerror")]
    private static extern IntPtr Dlerror();

    public static void Apply()
    {
        if (_applied)
        {
            return;
        }

        if (OperatingSystem.IsLinux())
        {
            // The game's Harmony/MonoMod helper needs globally visible
            // _Unwind_* symbols. NativeLibrary.Load uses local visibility.
            // Keep this dlopen reference for process lifetime; do not dlclose.
            const int rtldNow = 2;
            const int rtldGlobal = 0x100;
            _linuxUnwindLibrary = Dlopen("libgcc_s.so.1", rtldNow | rtldGlobal);
            if (_linuxUnwindLibrary == IntPtr.Zero)
            {
                throw new InvalidOperationException($"Unable to load Harmony's Linux unwind dependency: {Marshal.PtrToStringAnsi(Dlerror())}");
            }
        }

        Harmony harmony = new("sts2-bridge.exports");
        harmony.PatchAll(typeof(ExportHooks).Assembly);
        _applied = true;
        Log.Warn("STS2 Bridge export hooks applied.");
    }

    private static void QueueExport()
    {
        BridgeRuntime.RequestExport();
    }

    private static void Trace(string eventName, params (string Key, object? Value)[] fields)
    {
        TraceRecorder.Log(eventName, fields);
    }

    private static void QueueExportIfCombat()
    {
        if (CombatManager.Instance?.IsInProgress == true)
        {
            BridgeRuntime.RequestExport();
        }
    }

    [HarmonyPatch]
    private static class HandSelectionChangedPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(NPlayerHand), "RefreshSelectModeConfirmButton");
            yield return AccessTools.Method(typeof(NPlayerHand), "AfterCardsSelected");
            yield return AccessTools.Method(typeof(NPlayerHand), "OnPeekButtonToggled");
        }

        [HarmonyPostfix]
        private static void Postfix() => QueueExport();
    }

    [HarmonyPatch(typeof(CombatHistory), nameof(CombatHistory.CardPlayFinished))]
    private static class CardPlayFinishedPatch
    {
        [HarmonyPostfix]
        private static void Postfix(ICombatState __0, CardPlay __1)
        {
            Trace("card.play_finished", ("card_id", CardStateExporter.Id(__1.Card)),
                ("model_id", __1.Card.Id.Entry), ("target_id", CardStateExporter.TargetId(__1.Target, __0)),
                ("round", __0.RoundNumber), ("player_turn", __1.Card.Owner.PlayerCombatState?.TurnNumber),
                ("energy_spent", __1.Resources.EnergySpent), ("stars_spent", __1.Resources.StarsSpent),
                ("auto_play", __1.IsAutoPlay), ("play_index", __1.PlayIndex), ("play_count", __1.PlayCount));
            QueueExport();
        }
    }

    [HarmonyPatch]
    private static class CombatContextChangedPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(PowerModel), nameof(PowerModel.SetAmount));
            yield return AccessTools.Method(typeof(PowerModel), nameof(PowerModel.ApplyInternal));
            yield return AccessTools.Method(typeof(PowerModel), nameof(PowerModel.RemoveInternal));
            yield return AccessTools.Method(typeof(MonsterModel), nameof(MonsterModel.RollMove));
            yield return AccessTools.Method(typeof(MonsterModel), nameof(MonsterModel.SetMoveImmediate));
        }

        [HarmonyPostfix]
        private static void Postfix() => QueueExportIfCombat();
    }

    [HarmonyPatch(typeof(CardModel), nameof(CardModel.TryManualPlay), new[] { typeof(Creature) })]
    private static class CardManualPlayPatch
    {
        [HarmonyPostfix]
        private static void Postfix(CardModel __instance, Creature? __0, bool __result)
        {
            Trace("card.manual_play", ("card", __instance.Id.Entry), ("target", __0?.Name), ("accepted", __result));
            QueueExport();
        }
    }

    [HarmonyPatch(typeof(NEndTurnButton), "OnRelease")]
    private static class EndTurnButtonPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            Trace("combat.end_turn_clicked");
            QueueExport();
        }
    }

    [HarmonyPatch(typeof(RunManager), "AfterMapLocationChanged")]
    private static class RunManagerAfterMapLocationChangedPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            Trace("run.after_location_changed");
            QueueExport();
        }
    }

    [HarmonyPatch(typeof(NCombatRoom), "OnCombatSetUp")]
    private static class NCombatRoomOnCombatSetUpPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            Trace("combat.setup");
            QueueExport();
        }
    }

    [HarmonyPatch(typeof(NEventRoom), "SetOptions", new[] { typeof(EventModel) })]
    private static class NEventRoomSetOptionsPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            Trace("event.set_options");
            QueueExport();
        }
    }

    [HarmonyPatch(typeof(NEventRoom), "RefreshEventState", new[] { typeof(EventModel) })]
    private static class NEventRoomRefreshEventStatePatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            Trace("event.refresh_state");
            QueueExport();
        }
    }

    [HarmonyPatch(typeof(NEventRoom), nameof(NEventRoom.OptionButtonClicked), new[] { typeof(EventOption), typeof(int) })]
    private static class NEventRoomOptionButtonClickedPatch
    {
        [HarmonyPrefix]
        private static void Prefix(EventOption __0, int __1)
        {
            Trace("event.option_clicked", ("text_key", __0.TextKey), ("index", __1),
                ("locked", __0.IsLocked), ("proceed", __0.IsProceed));
            QueueExport();
        }
    }

    [HarmonyPatch(typeof(NEventRoom), nameof(NEventRoom.Proceed))]
    private static class NEventRoomProceedPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            Trace("event.proceed");
            QueueExport();
        }
    }

    [HarmonyPatch(typeof(NDeckTransformSelectScreen), "ShowScreen")]
    private static class NDeckTransformSelectScreenShowScreenPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            Trace("card_selection.deck_transform_show");
            QueueExport();
        }
    }

    [HarmonyPatch(typeof(NDeckCardSelectScreen), "Create", new[] { typeof(IReadOnlyList<CardModel>), typeof(CardSelectorPrefs) })]
    private static class NDeckCardSelectScreenCreatePatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            Trace("card_selection.deck_select_create");
            QueueExport();
        }
    }

    [HarmonyPatch(typeof(NChooseACardSelectionScreen), "ShowScreen")]
    private static class NChooseACardSelectionScreenShowScreenPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            Trace("card_selection.choose_a_card_show");
            QueueExport();
        }
    }

    [HarmonyPatch(typeof(NMerchantRoom), "HideScreen")]
    private static class NMerchantRoomHideScreenPatch
    {
        [HarmonyPostfix]
        private static void Postfix(object? _)
        {
            Trace("merchant.hide_screen");
            QueueExport();
        }
    }

    [HarmonyPatch(typeof(NMerchantRoom), "AfterRoomIsLoaded")]
    private static class NMerchantRoomAfterRoomIsLoadedPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            Trace("merchant.after_room_loaded");
            QueueExport();
        }
    }

    [HarmonyPatch(typeof(NMerchantRoom), "OnActiveScreenUpdated")]
    private static class NMerchantRoomOnActiveScreenUpdatedPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            Trace("merchant.active_screen_updated");
            QueueExport();
        }
    }

    [HarmonyPatch(typeof(NProceedButton), "OnPress")]
    private static class NProceedButtonOnPressPatch
    {
        [HarmonyPostfix]
        private static void Postfix(NProceedButton __instance)
        {
            Trace("proceed_button.press", ("is_skip", __instance.IsSkip));
        }
    }

    [HarmonyPatch(typeof(NProceedButton), "OnRelease")]
    private static class NProceedButtonOnReleasePatch
    {
        [HarmonyPostfix]
        private static void Postfix(NProceedButton __instance)
        {
            Trace("proceed_button.release", ("is_skip", __instance.IsSkip));
        }
    }

    [HarmonyPatch(typeof(NCardRewardSelectionScreen), "ShowScreen")]
    private static class NCardRewardSelectionScreenShowScreenPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            Trace("card_reward.show");
            QueueExport();
        }
    }

    [HarmonyPatch(typeof(NCardRewardSelectionScreen), "RefreshOptions")]
    private static class NCardRewardSelectionScreenRefreshOptionsPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            Trace("card_reward.refresh_options");
            QueueExport();
        }
    }

    [HarmonyPatch(typeof(NCardRewardSelectionScreen), "SelectCard")]
    private static class NCardRewardSelectionScreenSelectCardPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            Trace("card_reward.select_card");
            QueueExport();
        }
    }

    [HarmonyPatch(typeof(NSimpleCardSelectScreen), "Create", new[] { typeof(IReadOnlyList<CardModel>), typeof(CardSelectorPrefs) })]
    private static class NSimpleCardSelectScreenCreatePatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            QueueExport();
        }
    }

    [HarmonyPatch(typeof(NDeckUpgradeSelectScreen), "ShowScreen")]
    private static class NDeckUpgradeSelectScreenShowScreenPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            QueueExport();
        }
    }

    [HarmonyPatch(typeof(NDeckEnchantSelectScreen), "ShowScreen")]
    private static class NDeckEnchantSelectScreenShowScreenPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            QueueExport();
        }
    }

    [HarmonyPatch(typeof(NDeckTransformSelectScreen), "OnCardClicked")]
    private static class NDeckTransformSelectScreenOnCardClickedPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            QueueExport();
        }
    }

    [HarmonyPatch(typeof(NDeckCardSelectScreen), "OnCardClicked")]
    private static class NDeckCardSelectScreenOnCardClickedPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            QueueExport();
        }
    }

    [HarmonyPatch(typeof(NChooseACardSelectionScreen), "SelectHolder")]
    private static class NChooseACardSelectionScreenSelectHolderPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            QueueExport();
        }
    }

    [HarmonyPatch(typeof(NSimpleCardSelectScreen), "OnCardClicked")]
    private static class NSimpleCardSelectScreenOnCardClickedPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            QueueExport();
        }
    }

    [HarmonyPatch(typeof(NDeckUpgradeSelectScreen), "OnCardClicked")]
    private static class NDeckUpgradeSelectScreenOnCardClickedPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            QueueExport();
        }
    }

    [HarmonyPatch(typeof(NDeckEnchantSelectScreen), "OnCardClicked")]
    private static class NDeckEnchantSelectScreenOnCardClickedPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            QueueExport();
        }
    }

    [HarmonyPatch(typeof(NMapScreen), "Open")]
    private static class NMapScreenOpenPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            QueueExport();
        }
    }

    [HarmonyPatch(typeof(NMapScreen), "SetMap")]
    private static class NMapScreenSetMapPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            QueueExport();
        }
    }

    [HarmonyPatch(typeof(NMapScreen), "SetTravelEnabled")]
    private static class NMapScreenSetTravelEnabledPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            QueueExport();
        }
    }

    [HarmonyPatch(typeof(NMapScreen), "OnMapPointSelectedLocally")]
    private static class NMapScreenOnMapPointSelectedLocallyPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            QueueExport();
        }
    }

    [HarmonyPatch(typeof(NMapScreen), "TravelToMapCoord")]
    private static class NMapScreenTravelToMapCoordPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            QueueExport();
        }
    }

    [HarmonyPatch(typeof(NRewardsScreen), "ShowScreen")]
    private static class NRewardsScreenShowScreenPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            QueueExport();
        }
    }

    [HarmonyPatch(typeof(NRewardsScreen), "_Ready")]
    private static class NRewardsScreenReadyPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            QueueExport();
        }
    }

    [HarmonyPatch(typeof(NRewardsScreen), "RewardCollectedFrom")]
    private static class NRewardsScreenRewardCollectedFromPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            QueueExport();
        }
    }

    [HarmonyPatch(typeof(NRewardsScreen), "RewardSkippedFrom")]
    private static class NRewardsScreenRewardSkippedFromPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            QueueExport();
        }
    }

    [HarmonyPatch(typeof(NRewardsScreen), "OnProceedButtonPressed")]
    private static class NRewardsScreenOnProceedButtonPressedPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            QueueExport();
        }
    }

    [HarmonyPatch(typeof(NMerchantRoom), "Create")]
    private static class NMerchantRoomCreatePatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            QueueExport();
        }
    }

    [HarmonyPatch(typeof(NMerchantRoom), "OpenInventory")]
    private static class NMerchantRoomOpenInventoryPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            QueueExport();
        }
    }

    [HarmonyPatch(typeof(NMerchantRoom), "OnMerchantOpened")]
    private static class NMerchantRoomOnMerchantOpenedPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            QueueExport();
        }
    }

    [HarmonyPatch(typeof(NMerchantInventory), "Open")]
    private static class NMerchantInventoryOpenPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            QueueExport();
        }
    }

    // Open/close/purchase exports already hook the shared inventory base class.
    [HarmonyPatch]
    private static class NFakeMerchantContextPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(NFakeMerchant), "AfterRoomIsLoaded");
            yield return AccessTools.Method(typeof(NFakeMerchant), "OnActiveScreenUpdated");
            yield return AccessTools.Method(typeof(NFakeMerchant), "BlockInput");
            yield return AccessTools.Method(typeof(NFakeMerchant), "UnblockInput");
            yield return AccessTools.Method(typeof(NMerchantInventory), "BlockInput");
            yield return AccessTools.Method(typeof(NMerchantInventory), "UnblockInput");
        }

        [HarmonyPostfix]
        private static void Postfix() => QueueExport();
    }

    [HarmonyPatch(typeof(NMerchantInventory), "Close")]
    private static class NMerchantInventoryClosePatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            QueueExport();
        }
    }

    [HarmonyPatch(typeof(NMerchantInventory), "OnPurchaseCompleted")]
    private static class NMerchantInventoryOnPurchaseCompletedPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            QueueExport();
        }
    }

    [HarmonyPatch(typeof(NMerchantInventory), "OnCardRemovalUsed")]
    private static class NMerchantInventoryOnCardRemovalUsedPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            QueueExport();
        }
    }

    [HarmonyPatch(typeof(NMerchantSlot), "OnSelected")]
    private static class NMerchantSlotOnSelectedPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            QueueExport();
        }
    }


    [HarmonyPatch(typeof(NRestSiteRoom), "UpdateRestSiteOptions")]
    private static class NRestSiteRoomUpdateRestSiteOptionsPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            Trace("rest_site.update_options");
            QueueExport();
        }
    }

    [HarmonyPatch(typeof(NRestSiteButton), "OnRelease")]
    private static class NRestSiteButtonOnReleasePatch
    {
        [HarmonyPostfix]
        private static void Postfix(NRestSiteButton __instance)
        {
            Trace("rest_site.option_released", ("option", __instance.Option?.OptionId));
            QueueExport();
        }
    }

    [HarmonyPatch(typeof(NRestSiteRoom), "OnAfterPlayerSelectedRestSiteOption")]
    private static class NRestSiteRoomOnAfterPlayerSelectedRestSiteOptionPatch
    {
        [HarmonyPostfix]
        private static void Postfix(RestSiteOption option, bool success, int playerId)
        {
            Trace("rest_site.option_selected", ("option", option.OptionId), ("success", success), ("player_id", playerId));
            QueueExport();
        }
    }

    [HarmonyPatch(typeof(NRestSiteRoom), "OnProceedButtonReleased")]
    private static class NRestSiteRoomOnProceedButtonReleasedPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            Trace("rest_site.proceed");
            QueueExport();
        }
    }

    [HarmonyPatch(typeof(NTreasureRoom), "OpenChest")]
    private static class NTreasureRoomOpenChestPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            Trace("treasure.open_chest");
            QueueExport();
        }
    }

    [HarmonyPatch(typeof(NTreasureRoom), "OnChestButtonReleased")]
    private static class NTreasureRoomOnChestButtonReleasedPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            Trace("treasure.chest_button_released");
            QueueExport();
        }
    }

    [HarmonyPatch(typeof(NTreasureRoomRelicHolder), "OnRelease")]
    private static class NTreasureRoomRelicHolderOnReleasePatch
    {
        [HarmonyPostfix]
        private static void Postfix(NTreasureRoomRelicHolder __instance)
        {
            Trace("treasure.relic_released", ("relic", __instance.Relic));
            QueueExport();
        }
    }

    [HarmonyPatch(typeof(NTreasureRoom), "OnProceedButtonReleased")]
    private static class NTreasureRoomOnProceedButtonReleasedPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            Trace("treasure.proceed");
            QueueExport();
        }
    }

    [HarmonyPatch(typeof(NChooseARelicSelection), "ShowScreen")]
    private static class NChooseARelicSelectionShowScreenPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            Trace("relic_selection.show");
            QueueExport();
        }
    }

    [HarmonyPatch(typeof(NChooseARelicSelection), "SelectHolder")]
    private static class NChooseARelicSelectionSelectHolderPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            Trace("relic_selection.select_holder");
            QueueExport();
        }
    }

    [HarmonyPatch(typeof(NChooseARelicSelection), "RelicsSelected")]
    private static class NChooseARelicSelectionRelicsSelectedPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            Trace("relic_selection.completed");
            QueueExport();
        }
    }


    [HarmonyPatch(typeof(NRewardButton), "OnRelease")]
    private static class NRewardButtonOnReleasePatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            QueueExport();
        }
    }

    [HarmonyPatch(typeof(CombatState), "set_RoundNumber")]
    private static class CombatStateRoundNumberPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            QueueExport();
        }
    }

    [HarmonyPatch(typeof(CombatState), "set_CurrentSide")]
    private static class CombatStateCurrentSidePatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            QueueExport();
        }
    }

    [HarmonyPatch(typeof(Creature), "set_CurrentHp")]
    private static class CreatureCurrentHpPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            QueueExportIfCombat();
        }
    }

    [HarmonyPatch(typeof(Creature), "set_Block")]
    private static class CreatureBlockPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            QueueExportIfCombat();
        }
    }

    [HarmonyPatch(typeof(CombatManager), "set_PlayerActionsDisabled")]
    private static class CombatManagerPlayerActionsDisabledPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            QueueExport();
        }
    }

    [HarmonyPatch(typeof(PlayerCombatState), "set_Energy")]
    private static class PlayerCombatStateEnergyPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            QueueExport();
        }
    }

    [HarmonyPatch(typeof(PlayerCombatState), "set_Phase")]
    private static class PlayerCombatStatePhasePatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            QueueExport();
        }
    }

    [HarmonyPatch(typeof(CardPile), nameof(CardPile.InvokeContentsChanged))]
    private static class CardPileInvokeContentsChangedPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            QueueExport();
        }
    }

    [HarmonyPatch(typeof(PotionModel), "OnUseWrapper")]
    private static class PotionModelOnUseWrapperPatch
    {
        [HarmonyPostfix]
        private static void Postfix(PotionModel __instance)
        {
            Trace("potion.used", ("title", __instance.Title));
            QueueExport();
        }
    }

    [HarmonyPatch(typeof(PotionModel), "RemoveBeforeUse")]
    private static class PotionModelRemoveBeforeUsePatch
    {
        [HarmonyPostfix]
        private static void Postfix(PotionModel __instance)
        {
            Trace("potion.remove_before_use", ("title", __instance.Title));
            QueueExport();
        }
    }

    [HarmonyPatch(typeof(PotionModel), nameof(PotionModel.Discard))]
    private static class PotionModelDiscardPatch
    {
        [HarmonyPostfix]
        private static void Postfix(PotionModel __instance)
        {
            Trace("potion.discarded", ("model_id", __instance.Id.Entry));
            QueueExport();
        }
    }

    [HarmonyPatch(typeof(CombatState), nameof(CombatState.AddCreature), new[] { typeof(Creature) })]
    private static class CombatStateAddCreaturePatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            QueueExport();
        }
    }

    [HarmonyPatch(typeof(CombatState), nameof(CombatState.RemoveCreature), new[] { typeof(Creature), typeof(bool) })]
    private static class CombatStateRemoveCreaturePatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            QueueExport();
        }
    }
}
