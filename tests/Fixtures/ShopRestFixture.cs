using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace BridgeFixtures;

internal static class ShopRestFixture
{
    public static async Task Run(NGame game, RunManager manager, RunState run, Player player)
    {
        await manager.EnterRoomDebug(RoomType.Shop, showTransition: false);
        var inventory = ((MerchantRoom)run.CurrentRoom!).GetLocalInventory();
        // Force the offers from the live regression in this disposable game.
        // Keep the real entries, pricing, purchase callbacks, and UI lifecycle.
        CardModel[] cards = [ModelDb.Card<Headbutt>(), ModelDb.Card<SetupStrike>(),
            ModelDb.Card<Dominate>(), ModelDb.Card<Bloodletting>(), ModelDb.Card<Stampede>(),
            ModelDb.Card<MindBlast>(), ModelDb.Card<Mayhem>()];
        foreach (var (entry, canonical) in inventory.CardEntries.Zip(cards))
        {
            CardModel card = run.CreateCard(canonical, player);
            if (card is Headbutt)
            {
                card.UpgradeInternal();
                card.FinalizeUpgradeInternal();
                card.DynamicVars.Damage.BaseValue = 17;
            }
            entry.CreationResult!.ModifyCard(card);
            entry.CalcCost();
        }
        RelicModel[] relics = [ModelDb.Relic<Pendulum>(), ModelDb.Relic<Kunai>(), ModelDb.Relic<MysticLighter>()];
        foreach (var (entry, relic) in inventory.RelicEntries.Zip(relics))
        {
            typeof(MerchantRelicEntry).GetProperty("Model")!.SetValue(entry, relic.ToMutable());
            entry.CalcCost();
        }
        PotionModel[] potions = [ModelDb.Potion<SkillPotion>(), ModelDb.Potion<FlexPotion>(), ModelDb.Potion<LuckyTonic>()];
        foreach (var (entry, potion) in inventory.PotionEntries.Zip(potions))
        {
            typeof(MerchantPotionEntry).GetProperty("Model")!.SetValue(entry, potion.ToMutable());
            entry.CalcCost();
        }
        foreach (NMerchantSlot slot in NMerchantRoom.Instance!.Inventory.GetAllSlots())
        {
            // Refresh existing nodes without re-subscribing purchase handlers.
            if (slot is NMerchantRelic)
                typeof(NMerchantRelic).GetField("_relic", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(slot, ((MerchantRelicEntry)slot.Entry).Model);
            if (slot is NMerchantPotion)
                typeof(NMerchantPotion).GetField("_potion", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(slot, ((MerchantPotionEntry)slot.Entry).Model);
            slot.GetType().GetMethod("UpdateVisual", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(slot, null);
        }
        string shopsRng = JsonSerializer.Serialize(player.PlayerRng.ToSerializable().Counters);
        IList allCards = (IList)typeof(RunState).GetField("_allCards", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(run)!;
        int registeredCards = allCards.Count;
        File.WriteAllText("/test/fixture-ready.json", "{\"event_id\":\"SHOP_REST_CONTEXT\"}");
        await WaitForFile(game, "/test/fixture-check-shop");
        if (JsonSerializer.Serialize(player.PlayerRng.ToSerializable().Counters) != shopsRng || allCards.Count != registeredCards)
            throw new InvalidOperationException("Observing the shop consumed RNG or registered cards.");
        File.WriteAllText("/test/fixture-shop-checked", "");

        await WaitForFile(game, "/test/fixture-rest");
        player.Creature.SetCurrentHpInternal(31);
        await manager.EnterRoomDebug(RoomType.RestSite, showTransition: false);
        await WaitForFile(game, "/test/fixture-rest-modified");
        await RelicCmd.Obtain<RegalPillow>(player);
        ((RestSiteRoom)run.CurrentRoom!).Options.OfType<SmithRestSiteOption>().Single().SmithCount = 2;
        await WaitForFile(game, "/test/fixture-stop");
        if (player.Creature.CurrentHp != 70)
            throw new InvalidOperationException($"Expected 31 + 24 + 15 HP after resting, got {player.Creature.CurrentHp}.");
    }

    private static async Task WaitForFile(NGame game, string path)
    {
        while (!File.Exists(path))
            await game.ToSignal(game.GetTree(), SceneTree.SignalName.ProcessFrame);
    }
}
