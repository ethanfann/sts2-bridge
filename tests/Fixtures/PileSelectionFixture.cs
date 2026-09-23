using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Encounters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace BridgeFixtures;

internal static class PileSelectionFixture
{
    public static async Task Run(NGame game, RunManager manager, Player player)
    {
        await StartCombat(game, manager, player);
        PlayerCombatState pcs = player.PlayerCombatState!;
        foreach (CardModel card in pcs.DrawPile.Cards.OfType<StrikeIronclad>().Take(2).ToArray())
        {
            await CardPileCmd.Add(card, PileType.Discard, skipVisuals: true);
        }
        await Add<Hemokinesis>(player, PileType.Hand);
        await Add<NeowsFury>(player, PileType.Hand);
        await PowerCmd.Apply<VulnerablePower>(new ThrowingPlayerChoiceContext(),
            player.Creature.CombatState!.Enemies.Single(), 1, player.Creature, null);
        File.WriteAllText("/test/fixture-ready.json", "{\"event_id\":\"COMBAT_PILE_SELECTION\"}");

        await WaitForFile(game, "/test/fixture-pile-next");
        await StartCombat(game, manager, player);
        pcs = player.PlayerCombatState!;
        foreach (CardModel card in pcs.DrawPile.Cards.OfType<StrikeIronclad>().Take(2).ToArray())
        {
            await CardPileCmd.Add(card, PileType.Discard, skipVisuals: true);
        }
        await Add<NeowsFury>(player, PileType.Hand);
        await Add<Hologram>(player, PileType.Hand);
        await Add<SecretTechnique>(player, PileType.Hand);
        await Add<SecretWeapon>(player, PileType.Hand);
        File.WriteAllText("/test/fixture-pile-reset", "");

        await WaitForFile(game, "/test/fixture-pile-reverse");
        foreach (CardModel card in pcs.DrawPile.Cards.Reverse().ToArray())
        {
            pcs.DrawPile.RemoveInternal(card);
            pcs.DrawPile.AddInternal(card);
        }
        File.WriteAllText("/test/fixture-pile-reversed", "");

        await WaitForFile(game, "/test/fixture-pile-exhaust");
        for (int i = 0; i < 3; i++)
        {
            CardModel card = player.Creature.CombatState!.CreateCard<StrikeIronclad>(player);
            if (i == 0)
            {
                card.UpgradeInternal();
                card.FinalizeUpgradeInternal();
            }
            await CardPileCmd.AddGeneratedCardToCombat(card, PileType.Exhaust, player);
        }
        // Exercise the same native UI on an exhaust pile, including live pile
        // mutation. This is a selector contract test, not an invented card effect.
        NCombatPileCardSelectScreen screen = NCombatPileCardSelectScreen.Create(pcs.ExhaustPile,
            new CardSelectorPrefs(new LocString("cards", "NEOWS_FURY.selectionScreenPrompt"), 1, 2),
            card => card is StrikeIronclad);
        NOverlayStack.Instance!.Push(screen);
        await WaitForFile(game, "/test/fixture-pile-remove-selected");
        CardModel removed = pcs.ExhaustPile.Cards.OfType<StrikeIronclad>().Single(card => card.IsUpgraded);
        // skipVisuals also suppresses the source pile's ContentsChanged event.
        // Use normal movement here so this exercises the real UI subscription.
        await CardPileCmd.Add(removed, PileType.Discard);
        File.WriteAllText("/test/fixture-pile-removed", "");
        CardModel[] result = (await screen.CardsSelected()).ToArray();
        await CardPileCmd.Add(result, PileType.Hand);
        File.WriteAllText("/test/fixture-pile-exhaust-result", result.Length.ToString());
        await WaitForFile(game, "/test/fixture-stop");
    }

    private static async Task StartCombat(NGame game, RunManager manager, Player player)
    {
        await manager.EnterRoomDebug(RoomType.Monster,
            model: ModelDb.Encounter<FuzzyWurmCrawlerWeak>().ToMutable(), showTransition: false);
        while (player.PlayerCombatState?.Phase != PlayerTurnPhase.Play
            || CombatManager.Instance.PlayerActionsDisabled || manager.ActionExecutor.IsRunning)
        {
            await game.ToSignal(game.GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        foreach (CardModel card in player.PlayerCombatState.Hand.Cards.ToArray())
        {
            await CardPileCmd.Add(card, PileType.Draw, skipVisuals: true);
        }
    }

    private static Task Add<T>(Player player, PileType pile) where T : CardModel, new()
    {
        return CardPileCmd.AddGeneratedCardToCombat(player.Creature.CombatState!.CreateCard<T>(player), pile, player);
    }

    private static async Task WaitForFile(NGame game, string path)
    {
        while (!File.Exists(path))
        {
            await game.ToSignal(game.GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }
}
