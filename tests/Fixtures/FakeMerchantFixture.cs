using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Events.Custom;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace BridgeFixtures;

internal static class FakeMerchantFixture
{
    public static async Task Run(NGame game, RunManager manager, RunState run, Player player)
    {
        await manager.SetActInternal(1);
        player.Gold = 120;
        player.Creature.SetCurrentHpInternal(31);
        await manager.EnterRoomDebug(RoomType.Event, MapPointType.Unknown, ModelDb.Event<FakeMerchant>(), showTransition: false);
        var model = (FakeMerchant)((EventRoom)run.CurrentRoom!).LocalMutableEvent;
        var room = (NFakeMerchant)model.Node!;
        // Force one pickup-effect offer only in the disposable fixture; retain
        // native entries, pricing, UI callbacks, payment, and relic acquisition.
        MerchantRelicEntry? waffle = model.Inventory.RelicEntries.FirstOrDefault(e => e.Model is FakeLeesWaffle);
        if (waffle is null)
        {
            waffle = model.Inventory.RelicEntries.First();
            typeof(MerchantRelicEntry).GetProperty("Model")!.SetValue(waffle, ModelDb.Relic<FakeLeesWaffle>().ToMutable());
            waffle.CalcCost();
            var slot = room.Inventory.GetAllSlots().OfType<NMerchantRelic>().Single(s => s.Entry == waffle);
            typeof(NMerchantRelic).GetField("_relic", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(slot, waffle.Model);
            typeof(NMerchantRelic).GetMethod("UpdateVisual", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(slot, null);
        }
        // Exactly affordable initially; the purchase leaves zero gold.
        player.Gold = waffle.Cost;
        string rng = JsonSerializer.Serialize(player.PlayerRng.ToSerializable().Counters);
        File.WriteAllText("/test/fixture-ready.json", JsonSerializer.Serialize(new
        {
            event_id = "FAKE_MERCHANT",
            offers = model.Inventory.RelicEntries.Select(e => new
            {
                model_id = e.Model!.Id.Entry, cost = e.Cost,
                description = e.Model.DynamicDescription.GetFormattedText(),
            }).ToArray(),
        }));

        await WaitForFile(game, "/test/fixture-room-block");
        room.BlockInput();
        await WaitForFile(game, "/test/fixture-room-unblock");
        room.UnblockInput();
        await WaitForFile(game, "/test/fixture-inventory-block");
        room.Inventory.BlockInput();
        await WaitForFile(game, "/test/fixture-inventory-unblock");
        room.Inventory.UnblockInput();
        await WaitForFile(game, "/test/fixture-map-cover");
        NMapScreen.Instance!.Open(isOpenedFromTopBar: true);
        await WaitForFile(game, "/test/fixture-map-uncover");
        NMapScreen.Instance.Close(animateOut: false);
        await WaitForFile(game, "/test/fixture-check-reads");
        if (JsonSerializer.Serialize(player.PlayerRng.ToSerializable().Counters) != rng
            || model.Inventory.RelicEntries.Any(e => !e.IsStocked)
            || player.Gold != waffle.Cost || player.Creature.CurrentHp != 31 || player.Relics.Count != 1)
            throw new InvalidOperationException("Observing/reopening/rejected commands mutated the fake shop or player.");
        File.WriteAllText("/test/fixture-reads-checked", "");
        while (!File.Exists("/test/fixture-stop"))
        {
            // Let the purchase test distinguish stale/sold-out rejection from
            // merely insufficient gold. Never replenish the real live run.
            if (File.Exists("/test/fixture-refill-gold") && !File.Exists("/test/fixture-gold-refilled"))
            {
                player.Gold = 200;
                File.WriteAllText("/test/fixture-gold-refilled", "");
            }
            await game.ToSignal(game.GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }

    private static async Task WaitForFile(NGame game, string path)
    {
        while (!File.Exists(path))
            await game.ToSignal(game.GetTree(), SceneTree.SignalName.ProcessFrame);
    }
}
