using System;
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
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Encounters;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace BridgeFixtures;

internal static class HandSelectionFixture
{
    public static async Task Run(NGame game, RunManager manager, Player player)
    {
        await StartCombat(game, manager, player);
        await PotionCmd.TryToProcure<BlockPotion>(player);
        await Add<ThinkingAhead>(player);
        CardModel topdeck = await Add<StrikeIronclad>(player, upgraded: true);
        await Add<DefendIronclad>(player);
        File.WriteAllText("/test/fixture-ready.json", "{\"event_id\":\"COMBAT_HAND_SELECTION\"}");

        await WaitForFile(game, "/test/fixture-hand-redraw");
        await CardPileCmd.Draw(new ThrowingPlayerChoiceContext(), 1, player);
        if (topdeck.Pile?.Type != PileType.Hand)
            throw new InvalidOperationException("Thinking Ahead did not put the selected card on top.");
        File.WriteAllText("/test/fixture-hand-redrawn", "");

        await WaitForFile(game, "/test/fixture-hand-effects");
        await StartCombat(game, manager, player);
        await Add<TrueGrit>(player, upgraded: true);
        await Add<Survivor>(player);
        await Add<Armaments>(player);
        await Add<StrikeIronclad>(player);
        await Add<StrikeIronclad>(player, upgraded: true);
        await Add<DefendIronclad>(player);
        await Add<Bash>(player);
        await Add<ShrugItOff>(player);
        File.WriteAllText("/test/fixture-hand-effects-ready", "");

        await WaitForFile(game, "/test/fixture-hand-contract");
        await StartCombat(game, manager, player);
        CardModel changing = await Add<Bash>(player);
        await Add<StrikeIronclad>(player);
        await Add<StrikeIronclad>(player);
        await Add<DefendIronclad>(player);
        NPlayerHand hand = NPlayerHand.Instance!;
        // Exercise the actual native selector contract, not an invented card.
        Task<System.Collections.Generic.IEnumerable<CardModel>> selection = hand.SelectCards(
            new CardSelectorPrefs(CardSelectorPrefs.DiscardSelectionPrompt, 1, 2),
            card => card.Type == CardType.Attack && !card.IsUpgraded, null);

        await WaitForFile(game, "/test/fixture-hand-peek");
        hand.PeekButton.SetPeeking(true);
        await WaitForFile(game, "/test/fixture-hand-unpeek");
        hand.PeekButton.SetPeeking(false);
        await WaitForFile(game, "/test/fixture-hand-cover");
        NMapScreen.Instance!.Open(isOpenedFromTopBar: true);
        await WaitForFile(game, "/test/fixture-hand-uncover");
        NMapScreen.Instance!.Close(animateOut: false);
        await WaitForFile(game, "/test/fixture-hand-invalidate");
        CardCmd.Upgrade(changing);
        await PowerCmd.Apply<StrengthPower>(new ThrowingPlayerChoiceContext(), player.Creature, 1, player.Creature, null);
        File.WriteAllText("/test/fixture-hand-invalidated", "");
        CardModel[] result = (await selection).ToArray();
        await CardPileCmd.Add(result, PileType.Discard);
        File.WriteAllText("/test/fixture-hand-contract-result", result.Length.ToString());

        await WaitForFile(game, "/test/fixture-hand-optional");
        CardModel[] before = player.PlayerCombatState!.Hand.Cards.ToArray();
        result = (await hand.SelectCards(new CardSelectorPrefs(CardSelectorPrefs.DiscardSelectionPrompt, 0, 2), null, null)).ToArray();
        if (result.Length != 0 || !before.SequenceEqual(player.PlayerCombatState.Hand.Cards))
            throw new InvalidOperationException("Confirming zero should not move any cards.");
        File.WriteAllText("/test/fixture-hand-optional-result", "0");
        await WaitForFile(game, "/test/fixture-stop");
    }

    private static async Task StartCombat(NGame game, RunManager manager, Player player)
    {
        await manager.EnterRoomDebug(RoomType.Monster,
            model: ModelDb.Encounter<FuzzyWurmCrawlerWeak>().ToMutable(), showTransition: false);
        while (player.PlayerCombatState?.Phase != PlayerTurnPhase.Play
            || CombatManager.Instance.PlayerActionsDisabled || manager.ActionExecutor.IsRunning)
            await game.ToSignal(game.GetTree(), SceneTree.SignalName.ProcessFrame);
        foreach (CardModel card in player.PlayerCombatState.Hand.Cards.ToArray())
            await CardPileCmd.Add(card, PileType.Draw);
    }

    private static async Task<CardModel> Add<T>(Player player, bool upgraded = false) where T : CardModel, new()
    {
        CardModel card = player.Creature.CombatState!.CreateCard<T>(player);
        if (upgraded)
        {
            card.UpgradeInternal();
            card.FinalizeUpgradeInternal();
        }
        await CardPileCmd.AddGeneratedCardToCombat(card, PileType.Hand, player);
        return card;
    }

    private static async Task WaitForFile(NGame game, string path)
    {
        while (!File.Exists(path))
            await game.ToSignal(game.GetTree(), SceneTree.SignalName.ProcessFrame);
    }
}
