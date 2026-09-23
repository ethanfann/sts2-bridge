using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Encounters;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace BridgeFixtures;

internal static class CardRewardFixture
{
    public static async Task Run(NGame game, RunManager manager, RunState run, Player player)
    {
        await manager.EnterRoomDebug(RoomType.Shop, showTransition: false);
        var history = run.CurrentMapPointHistoryEntry!.GetEntry(player.NetId);
        RelicModel scales = ModelDb.Relic<BronzeScales>().ToMutable();
        scales.DynamicVars["ThornsPower"].BaseValue = 7; // Must preview the offered instance, not a canonical copy.
        var randomRelic = new RelicReward(player);
        // Offer Headbutt before any combat: its on-play selection prompt must
        // never become the reward screen's prompt, even without a prior play.
        var cards = new CardReward([
            run.CreateCard<StrikeIronclad>(player), run.CreateCard<Headbutt>(player), run.CreateCard<Bash>(player),
        ], CardCreationSource.Encounter, player, CardCreationOptions.ForRoom(player, RoomType.Monster));
        Task first = new RewardsSet(player).WithCustomRewards([
            new GoldReward(17, player), new RelicReward(ModelDb.Relic<LetterOpener>().ToMutable(), player),
            new RelicReward(scales, player), randomRelic, cards,
        ]).Offer();
        string rng = JsonSerializer.Serialize(player.PlayerRng.ToSerializable().Counters);
        int gold = player.Gold, deckCount = player.Deck.Cards.Count, relicCount = player.Relics.Count();
        File.WriteAllText("/test/fixture-ready.json", "{\"event_id\":\"CARD_REWARDS\"}");
        await Wait("reads");
        if (rng != JsonSerializer.Serialize(player.PlayerRng.ToSerializable().Counters)
            || player.Gold != gold || player.Deck.Cards.Count != deckCount || player.Relics.Count() != relicCount
            || history.CardChoices.Count != 0 || history.RelicChoices.Count != 0)
            throw new InvalidOperationException("Reward previews changed the run.");
        Write("reads", new { model_id = randomRelic.Relic!.Id.Entry,
            description = randomRelic.Relic.DynamicDescription.GetFormattedText() });

        await Wait("disable");
        var screen = (NCardRewardSelectionScreen)ActiveScreenContext.Instance.GetCurrentScreen()!;
        var button = screen.GetNode<Control>("UI/RewardAlternatives").GetChildren().OfType<NCardRewardAlternativeButton>().Single();
        button.Disable();
        await Wait("hide");
        button.Enable();
        button.Hide();
        await Wait("cover");
        button.Show();
        var cover = NSimpleCardSelectScreen.Create(player.Deck.Cards.Take(2).ToArray(),
            new CardSelectorPrefs(new LocString("cards", "NEOWS_FURY.selectionScreenPrompt"), 0, 1));
        NOverlayStack.Instance!.Push(cover);
        await Wait("uncover");
        NOverlayStack.Instance.Remove(cover);
        await Wait("skip-check");
        Write("skip", new { cards = history.CardChoices.Count, gold = player.Gold,
            deck = player.Deck.Cards.Count, relics = player.Relics.Count() });
        await first;
        Write("first", new { picked = history.CardChoices.Count(c => c.wasPicked),
            declined = history.CardChoices.Count(c => !c.wasPicked) });

        await Wait("required");
        await new RewardsSet(player).WithCustomRewards([
            new CardReward(CardCreationOptions.ForRoom(player, RoomType.Monster), 3, player) { CanSkip = false },
        ]).WithSkippingDisallowed().Offer();
        await Wait("reroll");
        await new RewardsSet(player).WithCustomRewards([NewCards(reroll: true)]).Offer();

        await Wait("sacrifice");
        var wing = (PaelsWing)await RelicCmd.Obtain<PaelsWing>(player);
        await new RewardsSet(player).WithCustomRewards([NewCards(), NewCards(), new GoldReward(23, player)]).Offer();
        Write("sacrifice", new { sacrifices = wing.RewardsSacrificed });

        // Actual terminal combat rewards must still lead to the map after an
        // opened card offer is skipped. Don't substitute a shop reward overlay.
        await Wait("combat");
        await manager.EnterRoomDebug(RoomType.Monster, model: ModelDb.Encounter<FuzzyWurmCrawlerWeak>().ToMutable(), showTransition: false);
        while (player.PlayerCombatState?.Phase != PlayerTurnPhase.Play
            || CombatManager.Instance.PlayerActionsDisabled || manager.ActionExecutor.IsRunning)
            await game.ToSignal(game.GetTree(), SceneTree.SignalName.ProcessFrame);
        var terminalHistory = run.CurrentMapPointHistoryEntry!.GetEntry(player.NetId);
        // EnterRoomDebug appends the next history entry before exiting the old
        // room. Earlier fixture offers can therefore already have declined
        // entries here. Audit only this combat's choices, without clearing any.
        int terminalHistoryStart = terminalHistory.CardChoices.Count;
        CardModel finisher = player.Creature.CombatState!.CreateCard<Bludgeon>(player);
        finisher.DynamicVars.Damage.BaseValue = 999;
        await CardPileCmd.AddGeneratedCardToCombat(finisher, PileType.Hand, player);
        await Wait("terminal-map-check");
        Write("terminal-map", new { new_choices = terminalHistory.CardChoices.Count - terminalHistoryStart });
        await Wait("terminal-check");
        var terminalChoices = terminalHistory.CardChoices.Skip(terminalHistoryStart).ToArray();
        Write("terminal", new { picked = terminalChoices.Count(c => c.wasPicked),
            declined = terminalChoices.Count(c => !c.wasPicked), sacrifices = wing.RewardsSacrificed,
            models = terminalChoices.Select(c => c.Card.Id!.Entry).Order(StringComparer.Ordinal) });
        await WaitForFile(game, "/test/fixture-stop");

        CardReward NewCards(bool reroll = false) => new(CardCreationOptions.ForRoom(player, RoomType.Monster), 3, player) { CanReroll = reroll };
        Task Wait(string name) => WaitForFile(game, $"/test/fixture-card-{name}");
        void Write(string name, object value) => File.WriteAllText($"/test/fixture-card-{name}.json", JsonSerializer.Serialize(value));
    }

    private static async Task WaitForFile(NGame game, string path)
    {
        while (!File.Exists(path))
            await game.ToSignal(game.GetTree(), SceneTree.SignalName.ProcessFrame);
    }
}
