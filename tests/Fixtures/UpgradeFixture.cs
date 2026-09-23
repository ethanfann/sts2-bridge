using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace BridgeFixtures;

internal static class UpgradeFixture
{
    public static async Task Run(NGame game, RunManager manager, RunState run, Player player)
    {
        CardModel upgraded = player.Deck.Cards.First();
        upgraded.UpgradeInternal();
        upgraded.FinalizeUpgradeInternal();
        foreach (CardModel canonical in new CardModel[] {
            ModelDb.Card<Hemokinesis>(), ModelDb.Card<PommelStrike>(),
            ModelDb.Card<BodySlam>(), ModelDb.Card<TrueGrit>(), ModelDb.Card<Slimed>(),
        })
        {
            await CardPileCmd.Add(run.CreateCard(canonical, player), PileType.Deck);
        }
        // An instance modifier must survive preview cloning. A fresh canonical
        // Hemokinesis would incorrectly predict 20 instead of 23 damage.
        player.Deck.Cards.OfType<Hemokinesis>().Single().DynamicVars.Damage.BaseValue = 18;
        for (int i = 0; i < 25; i++)
        {
            await CardPileCmd.Add(run.CreateCard<StrikeIronclad>(player), PileType.Deck);
        }
        await CardPileCmd.Add(run.CreateCard<Hemokinesis>(player), PileType.Deck);
        int upgradeEvents = 0;
        foreach (CardModel card in player.Deck.Cards)
        {
            card.Upgraded += () => upgradeEvents++;
        }
        IList allCards = (IList)typeof(RunState).GetField("_allCards", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(run)!;
        int registeredCards = allCards.Count;
        await manager.EnterRoomDebug(RoomType.Event, model: ModelDb.Event<AromaOfChaos>(), showTransition: false);
        File.WriteAllText("/test/fixture-ready.json", "{\"event_id\":\"DECK_UPGRADE\"}");

        // Python opens the real event selector and polls it before allowing the
        // native checkbox parity check. No bridge method constructs expectations.
        await WaitForFile(game, "/test/fixture-upgrade-checkbox");
        Node screen = (Node)ActiveScreenContext.Instance.GetCurrentScreen()!;
        NCardGrid grid = screen.GetNode<NCardGrid>("%CardGrid");
        NTickbox checkbox = screen.GetNode<NTickbox>("%Upgrades");
        if (checkbox.IsTicked || grid.IsShowingUpgrades || allCards.Count != registeredCards || upgradeEvents != 0)
        {
            throw new InvalidOperationException("Reading previews changed native state.");
        }
        checkbox.IsTicked = true;
        checkbox.EmitSignal(NTickbox.SignalName.Toggled, checkbox);
        var shown = grid.CurrentlyDisplayedCardHolders.Where(h => h.Visible && h.CardModel.IsUpgradable).ToArray();
        File.WriteAllText("/test/fixture-upgrades-native.json", JsonSerializer.Serialize(shown.Select(h => new
        {
            model_id = h.CardModel.Id.Entry,
            before_damage = h.CardModel.DynamicVars.TryGetValue("Damage", out var damage) ? (decimal?)damage.BaseValue : null,
            name = h.CardNode!.Model!.Title,
            description = h.CardNode!.Model!.GetDescriptionForUpgradePreview(),
            energy_cost = h.CardNode!.Model!.EnergyCost.GetWithModifiers(CostModifiers.All),
            upgrade_level = h.CardNode!.Model!.CurrentUpgradeLevel,
        })));
        await WaitForFile(game, "/test/fixture-upgrade-uncheck");
        checkbox.IsTicked = false;
        checkbox.EmitSignal(NTickbox.SignalName.Toggled, checkbox);
        if (allCards.Count != registeredCards || upgradeEvents != 0
            || shown.Any(h => h.CardNode!.Model != h.CardModel))
        {
            throw new InvalidOperationException("Preview toggle changed the owned cards.");
        }
        File.WriteAllText("/test/fixture-upgrades-unchecked", "");
        await WaitForFile(game, "/test/fixture-stop");
        if (upgradeEvents != 1)
        {
            throw new InvalidOperationException($"Expected exactly one real upgrade, got {upgradeEvents}.");
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
