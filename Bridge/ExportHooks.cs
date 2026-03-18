using HarmonyLib;
using System.Collections.Generic;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Runs;

namespace FirstMod.Bridge;

internal static class ExportHooks
{
    private static bool _applied;

    public static void Apply()
    {
        if (_applied)
        {
            return;
        }

        Harmony harmony = new("firstmod.bridge.exports");
        harmony.PatchAll(typeof(ExportHooks).Assembly);
        _applied = true;
        Log.Warn("FirstMod bridge export hooks applied.");
    }

    private static void QueueExport()
    {
        BridgeRuntime.RequestExport();
    }

    private static void QueueExportIfCombat()
    {
        if (CombatManager.Instance?.IsInProgress == true)
        {
            BridgeRuntime.RequestExport();
        }
    }

    [HarmonyPatch(typeof(RunManager), "AfterLocationChanged")]
    private static class RunManagerAfterLocationChangedPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            QueueExport();
        }
    }

    [HarmonyPatch(typeof(NCombatRoom), "OnCombatSetUp")]
    private static class NCombatRoomOnCombatSetUpPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            QueueExport();
        }
    }

    [HarmonyPatch(typeof(NEventRoom), "SetOptions", new[] { typeof(EventModel) })]
    private static class NEventRoomSetOptionsPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            QueueExport();
        }
    }

    [HarmonyPatch(typeof(NEventRoom), "RefreshEventState", new[] { typeof(EventModel) })]
    private static class NEventRoomRefreshEventStatePatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            QueueExport();
        }
    }

    [HarmonyPatch(typeof(NEventRoom), nameof(NEventRoom.OptionButtonClicked), new[] { typeof(EventOption), typeof(int) })]
    private static class NEventRoomOptionButtonClickedPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            QueueExport();
        }
    }

    [HarmonyPatch(typeof(NEventRoom), nameof(NEventRoom.Proceed))]
    private static class NEventRoomProceedPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            QueueExport();
        }
    }

    [HarmonyPatch(typeof(NDeckTransformSelectScreen), "ShowScreen")]
    private static class NDeckTransformSelectScreenShowScreenPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            QueueExport();
        }
    }

    [HarmonyPatch(typeof(NDeckCardSelectScreen), "Create", new[] { typeof(IReadOnlyList<CardModel>), typeof(CardSelectorPrefs) })]
    private static class NDeckCardSelectScreenCreatePatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            QueueExport();
        }
    }

    [HarmonyPatch(typeof(NChooseACardSelectionScreen), "ShowScreen")]
    private static class NChooseACardSelectionScreenShowScreenPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            QueueExport();
        }
    }

    [HarmonyPatch(typeof(NCardRewardSelectionScreen), "ShowScreen")]
    private static class NCardRewardSelectionScreenShowScreenPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            QueueExport();
        }
    }

    [HarmonyPatch(typeof(NCardRewardSelectionScreen), "RefreshOptions")]
    private static class NCardRewardSelectionScreenRefreshOptionsPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            QueueExport();
        }
    }

    [HarmonyPatch(typeof(NCardRewardSelectionScreen), "SelectCard")]
    private static class NCardRewardSelectionScreenSelectCardPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
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

    [HarmonyPatch(typeof(NRewardsScreen), "SetRewards")]
    private static class NRewardsScreenSetRewardsPatch
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

    [HarmonyPatch(typeof(CardPile), nameof(CardPile.InvokeContentsChanged))]
    private static class CardPileInvokeContentsChangedPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
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
