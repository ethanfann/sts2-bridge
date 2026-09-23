using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace BridgeFixtures;

internal static class TreasureFixture
{
    public static async Task Run(NGame game, RunManager manager, Player player, bool empty)
    {
        if (empty)
        {
            // Its first chest is genuinely empty. Keep the native empty-chest
            // path rather than replacing reward resolution with a test stub.
            await RelicCmd.Obtain<SilverCrucible>(player);
        }
        await manager.EnterRoomDebug(RoomType.Treasure, showTransition: false);
        if (empty)
        {
            // BeginRelicPicking ends voting immediately for an empty chest,
            // clearing CurrentRelics to null rather than leaving an empty list.
            if (manager.TreasureRoomRelicSynchronizer.CurrentRelics is not null)
                throw new InvalidOperationException("Expected Silver Crucible to suppress this chest.");
        }
        else
        {
            // Only the disposable fixture fixes the random offer. All opening,
            // selection, gold, award, and screen-transition logic stays native.
            var offers = (List<RelicModel>)manager.TreasureRoomRelicSynchronizer.CurrentRelics!;
            offers.Clear();
            offers.Add(ModelDb.Relic<BloodVial>());
        }
        File.WriteAllText("/test/fixture-ready.json", JsonSerializer.Serialize(new
        {
            event_id = empty ? "TREASURE_EMPTY" : "TREASURE_CONTEXT",
        }));
        if (!empty)
        {
            await WaitForFile(game, "/test/fixture-treasure-check");
            var room = (NTreasureRoom)ActiveScreenContext.Instance.GetCurrentScreen()!;
            var collection = room.GetNode<NTreasureRoomRelicCollection>("%RelicCollection");
            var holders = collection.GetNode<Control>("Container").GetChildren().OfType<NTreasureRoomRelicHolder>().ToArray();
            var visible = holders.Where(h => h.IsVisibleInTree()).ToArray();
            if (visible.Length != 1 || visible[0] != collection.SingleplayerRelicHolder
                || visible[0].Relic.Model.Id != ModelDb.Relic<BloodVial>().Id)
                throw new InvalidOperationException("Expected exactly the native singleplayer Blood Vial holder.");
            var unused = holders.Where(h => h != collection.SingleplayerRelicHolder).ToArray();
            int uninitialized = 0;
            foreach (var holder in unused)
            {
                try { _ = holder.Relic.Model; }
                catch (InvalidOperationException) { uninitialized++; }
            }
            if (uninitialized < 2)
                throw new InvalidOperationException("Fixture must include multiple unset hidden models.");
            // Also test a hidden holder with a valid model: checking for null
            // alone must not reveal or make this phantom reward selectable.
            unused[0].Relic.Model = ModelDb.Relic<RegalPillow>();
            File.WriteAllText("/test/fixture-treasure-native.json", JsonSerializer.Serialize(new
            {
                hidden_uninitialized = uninitialized,
                description = visible[0].Relic.Model.DynamicDescription.GetFormattedText(),
            }));
        }
        await WaitForFile(game, "/test/fixture-stop");
    }

    private static async Task WaitForFile(NGame game, string path)
    {
        while (!File.Exists(path))
            await game.ToSignal(game.GetTree(), SceneTree.SignalName.ProcessFrame);
    }
}
