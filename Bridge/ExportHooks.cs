using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Rooms;
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
