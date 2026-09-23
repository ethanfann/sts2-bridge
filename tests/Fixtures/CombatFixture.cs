using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Encounters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace BridgeFixtures;

internal static class CombatFixture
{
    public static async Task Run(NGame game, RunManager manager, RunState run, Player player)
    {
        // Set up asymmetric conditions in a disposable game, then let Python
        // drive ordinary bridge actions and compare previews with real results.
        CardModel upgraded = player.Deck.Cards.OfType<StrikeIronclad>().First();
        upgraded.UpgradeInternal();
        upgraded.FinalizeUpgradeInternal();
        await RelicCmd.Obtain<OrnamentalFan>(player);
        await manager.EnterRoomDebug(RoomType.Monster, model: ModelDb.Encounter<FuzzyWurmCrawlerWeak>().ToMutable(), showTransition: false);
        await WaitForTurn(game, player);
        PlayerCombatState pcs = player.PlayerCombatState!;
        foreach (CardModel card in pcs.Hand.Cards.ToArray())
        {
            await CardPileCmd.Add(card, PileType.Draw, skipVisuals: true);
        }
        CardModel[] hand = [
            pcs.DrawPile.Cards.OfType<StrikeIronclad>().First(card => !card.IsUpgraded),
            pcs.DrawPile.Cards.OfType<StrikeIronclad>().Single(card => card.IsUpgraded),
            pcs.DrawPile.Cards.OfType<DefendIronclad>().First(),
            pcs.DrawPile.Cards.OfType<Bash>().Single(),
        ];
        foreach (CardModel card in hand)
        {
            await CardPileCmd.Add(card, PileType.Hand);
        }
        await CardPileCmd.AddGeneratedCardToCombat(player.Creature.CombatState!.CreateCard<Slimed>(player), PileType.Hand, player);
        var enemy = player.Creature.CombatState!.Enemies.Single();
        ThrowingPlayerChoiceContext context = new();
        await PowerCmd.Apply<StrengthPower>(context, player.Creature, 3, player.Creature, null);
        await PowerCmd.Apply<WeakPower>(context, player.Creature, 2, enemy, null);
        await PowerCmd.Apply<DexterityPower>(context, player.Creature, 2, player.Creature, null);
        await PowerCmd.Apply<FrailPower>(context, player.Creature, 2, enemy, null);
        await PowerCmd.Apply<VulnerablePower>(context, enemy, 2, player.Creature, null);
        await PowerCmd.Apply<WeakPower>(context, enemy, 2, player.Creature, null);
        File.WriteAllText("/test/fixture-ready.json", "{\"event_id\":\"COMBAT_CONTEXT\"}");

        await WaitForFile(game, "/test/fixture-guard");
        // Same frame: mutate then restore a public gameplay value without any
        // export between. Checking only the last state ID would miss this.
        Type exporter = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "sts2-bridge")
            .GetType("Sts2Bridge.Bridge.StateExporter")!;
        MethodInfo matches = exporter.GetMethod("MatchesCurrentObservation", BindingFlags.Public | BindingFlags.Static)!;
        using (JsonDocument state = JsonDocument.Parse(File.ReadAllText(ProjectSettings.GlobalizePath("user://sts2-bridge/state.json"))))
        {
            string stateId = state.RootElement.GetProperty("state_id").GetString()!;
            if (matches.Invoke(null, [stateId]) is not true)
                throw new InvalidOperationException("Current gameplay state must match its observation.");
            int gold = player.Gold;
            try
            {
                player.Gold = gold + 1;
                if (matches.Invoke(null, [stateId]) is not false)
                    throw new InvalidOperationException("Guard accepted gameplay changed before export.");
            }
            finally { player.Gold = gold; }
            if (matches.Invoke(null, [stateId]) is not true)
                throw new InvalidOperationException("Restored gameplay should match, regardless of animated labels.");
        }
        File.WriteAllText("/test/fixture-guard-checked", "");

        await WaitForFile(game, "/test/fixture-reverse-draw");
        foreach (CardModel card in pcs.DrawPile.Cards.Reverse().ToArray())
        {
            pcs.DrawPile.RemoveInternal(card);
            pcs.DrawPile.AddInternal(card);
        }
        File.WriteAllText("/test/fixture-reversed", "");

        await WaitForFile(game, "/test/fixture-intents");
        // Export-only edge cases. No action is taken while these synthetic
        // intents are installed; the preceding turns use the real move logic.
        enemy.Monster!.SetMoveImmediate(new MoveState("BRIDGE_INTENT_TEST", _ => Task.CompletedTask,
            new MultiAttackIntent(5, 3), new HiddenIntent(), new UnknownIntent()), forceTransition: true);

        await WaitForFile(game, "/test/fixture-next-combat");
        await manager.EnterRoomDebug(RoomType.Monster, model: ModelDb.Encounter<FuzzyWurmCrawlerWeak>().ToMutable(), showTransition: false);
        await WaitForTurn(game, player);
        await PowerCmd.Apply<DexterityPower>(context, player.Creature, 2, player.Creature, null);
        await PowerCmd.Apply<FrailPower>(context, player.Creature, 2, player.Creature.CombatState!.Enemies.Single(), null);
        for (int i = 0; i < 3; i++)
        {
            await CardPileCmd.AddGeneratedCardToCombat(player.Creature.CombatState!.CreateCard<Anger>(player), PileType.Hand, player);
        }
        // Finish this combat through a normal bridge play after checking the
        // Fan trigger, then exercise the real terminal-reward lifecycle.
        CardModel finisher = player.Creature.CombatState!.CreateCard<Bludgeon>(player);
        finisher.UpgradeInternal();
        finisher.FinalizeUpgradeInternal();
        await CardPileCmd.AddGeneratedCardToCombat(finisher, PileType.Hand, player);
        File.WriteAllText("/test/fixture-reset", "");
        await WaitForFile(game, "/test/fixture-stop");
    }

    private static async Task WaitForTurn(NGame game, Player player)
    {
        while (player.PlayerCombatState?.Phase != PlayerTurnPhase.Play
            || CombatManager.Instance.PlayerActionsDisabled || RunManager.Instance.ActionExecutor.IsRunning)
        {
            await game.ToSignal(game.GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }

    private static async Task WaitForFile(NGame game, string path)
    {
        while (!File.Exists(path))
        {
            await game.ToSignal(game.GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }
}
