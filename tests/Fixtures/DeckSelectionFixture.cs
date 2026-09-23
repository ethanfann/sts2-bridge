using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace BridgeFixtures;

internal static class DeckSelectionFixture
{
    public static async Task Run(NGame game, RunManager manager, RunState run, Player player)
    {
        // Force only the Trial branch; execute its real curse, selector, and
        // transformations through an ordinary event option in the sandbox.
        await manager.EnterRoomDebug(RoomType.Event, model: ModelDb.Event<Trial>(), showTransition: false);
        Trial trial = (Trial)((EventRoom)run.CurrentRoom!).LocalMutableEvent;
        var innocent = (Func<Task>)typeof(Trial).GetMethod("NondescriptInnocent", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate(typeof(Func<Task>), trial);
        typeof(EventModel).GetMethod("SetEventState", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(trial, [trial.Description, new EventOption[] {
                new(trial, innocent, "TRIAL.pages.NONDESCRIPT.options.INNOCENT"),
            }]);
        File.WriteAllText("/test/fixture-ready.json", "{\"event_id\":\"DECK_SELECTION\"}");

        await Wait(game, "remove");
        var removal = NDeckCardSelectScreen.Create(player.Deck.Cards.ToArray(),
            new CardSelectorPrefs(CardSelectorPrefs.RemoveSelectionPrompt, 1, 2));
        NOverlayStack.Instance!.Push(removal);
        await Wait(game, "cover");
        NMapScreen.Instance!.Open(isOpenedFromTopBar: true);
        await Wait(game, "uncover");
        NMapScreen.Instance!.Close(animateOut: false);
        CardModel[] selected = (await removal.CardsSelected()).ToArray();
        if (selected.Length != 1 || selected[0] is not Bash)
            throw new InvalidOperationException("Expected precisely Bash from the range selection.");
        await CardPileCmd.RemoveFromDeck(selected);
        Done("remove");

        await Wait(game, "upgrade");
        selected = (await CardSelectCmd.FromDeckForUpgrade(player,
            new CardSelectorPrefs(CardSelectorPrefs.UpgradeSelectionPrompt, 2))).ToArray();
        if (selected.Length != 2 || selected.Any(c => c is not StrikeIronclad))
            throw new InvalidOperationException("Expected two Strikes to upgrade.");
        foreach (CardModel card in selected) CardCmd.Upgrade(card);
        Done("upgrade");

        await Wait(game, "enchant");
        selected = (await CardSelectCmd.FromDeckForEnchantment(player, ModelDb.Enchantment<Swift>(), 2,
            new CardSelectorPrefs(CardSelectorPrefs.UpgradeSelectionPrompt, 1))).ToArray();
        if (selected.Length != 1 || selected[0] is not StrikeIronclad)
            throw new InvalidOperationException("Expected one Strike to enchant.");
        CardCmd.Enchant<Swift>(selected[0], 2);
        Done("enchant");

        await Wait(game, "simple");
        var simple = NSimpleCardSelectScreen.Create(player.Deck.Cards.ToArray(),
            new CardSelectorPrefs(CardSelectorPrefs.RemoveSelectionPrompt, 0, 2) { RequireManualConfirmation = true });
        NOverlayStack.Instance.Push(simple);
        if ((await simple.CardsSelected()).Any())
            throw new InvalidOperationException("Expected optional selection to return zero cards.");
        Done("simple");

        await Wait(game, "automatic");
        simple = NSimpleCardSelectScreen.Create(player.Deck.Cards.ToArray(),
            new CardSelectorPrefs(CardSelectorPrefs.RemoveSelectionPrompt, 1));
        NOverlayStack.Instance.Push(simple);
        if ((await simple.CardsSelected()).Single() is not Doubt)
            throw new InvalidOperationException("Automatic selection returned the wrong card.");
        Done("automatic");
        while (!File.Exists("/test/fixture-stop"))
            await game.ToSignal(game.GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private static void Done(string step) => File.WriteAllText($"/test/fixture-deck-{step}-done", "");

    private static async Task Wait(NGame game, string step)
    {
        while (!File.Exists($"/test/fixture-deck-{step}"))
            await game.ToSignal(game.GetTree(), SceneTree.SignalName.ProcessFrame);
    }
}
