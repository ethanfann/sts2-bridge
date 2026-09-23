using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Encounters;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace BridgeFixtures;

internal static class PotionRewardFixture
{
    public static async Task Run(NGame game, RunManager manager, RunState run, Player player)
    {
        await manager.EnterRoomDebug(RoomType.Shop, showTransition: false);
        player.Creature.SetCurrentHpInternal(31);
        await PotionCmd.TryToProcure<FruitJuice>(player);
        await PotionCmd.TryToProcure<WeakPotion>(player);
        await PotionCmd.TryToProcure<FruitJuice>(player);
        var history = run.CurrentMapPointHistoryEntry!.GetEntry(player.NetId);
        var first = new RewardsSet(player).WithCustomRewards([
            new GoldReward(17, player),
            new PotionReward(ModelDb.Potion<Fortifier>().ToMutable(), player),
            new CardReward(CardCreationOptions.ForRoom(player, RoomType.Monster), 3, player),
        ]);
        Task firstOffer = first.Offer();
        string rng = JsonSerializer.Serialize(player.PlayerRng.ToSerializable().Counters);
        File.WriteAllText("/test/fixture-ready.json", "{\"event_id\":\"POTION_REWARDS\"}");

        await WaitForFile(game, "/test/fixture-potion-lock");
        player.CanRemovePotions = false;
        await WaitForFile(game, "/test/fixture-potion-unlock");
        player.CanRemovePotions = true;
        await WaitForFile(game, "/test/fixture-potion-read-check");
        if (JsonSerializer.Serialize(player.PlayerRng.ToSerializable().Counters) != rng
            || history.PotionChoices.Count != 3 || history.PotionDiscarded.Count != 0
            || player.Potions.Count() != 3 || player.Creature.CurrentHp != 31)
            throw new InvalidOperationException("Potion previews or rejected commands changed game state.");
        File.WriteAllText("/test/fixture-potion-reads-checked", "");
        await firstOffer;
        await WaitForFile(game, "/test/fixture-potion-skip-check");
        Audit("skip");

        // Required rewards must not be bypassed, even with a full potion belt.
        await WaitForFile(game, "/test/fixture-potion-replace");
        await new RewardsSet(player).WithCustomRewards([
            new PotionReward(ModelDb.Potion<Fortifier>().ToMutable(), player),
        ]).WithSkippingDisallowed().Offer();
        await WaitForFile(game, "/test/fixture-potion-replace-check");
        Audit("replace");

        // Refill the SAME slot with the SAME model as the discarded instance.
        // A title/slot ID would make the old discard command target this one.
        await WaitForFile(game, "/test/fixture-potion-refill");
        await new RewardsSet(player).WithCustomRewards([
            new PotionReward(ModelDb.Potion<FruitJuice>().ToMutable(), player),
        ]).Offer();
        await WaitForFile(game, "/test/fixture-potion-refill-check");
        Audit("refill");

        await WaitForFile(game, "/test/fixture-potion-capacity");
        player.AddToMaxPotionCount(2);
        await new RewardsSet(player).WithCustomRewards([
            new PotionReward(ModelDb.Potion<Fortifier>().ToMutable(), player),
        ]).Offer();
        await WaitForFile(game, "/test/fixture-potion-combat");
        await PotionCmd.TryToProcure<BlockPotion>(player);
        await manager.EnterRoomDebug(RoomType.Monster, model: ModelDb.Encounter<FuzzyWurmCrawlerWeak>().ToMutable(), showTransition: false);
        while (player.PlayerCombatState?.Phase != PlayerTurnPhase.Play
            || CombatManager.Instance.PlayerActionsDisabled || manager.ActionExecutor.IsRunning)
            await game.ToSignal(game.GetTree(), SceneTree.SignalName.ProcessFrame);
        File.WriteAllText("/test/fixture-potion-combat-ready", "");

        await WaitForFile(game, "/test/fixture-potion-terminal");
        while (manager.ActionExecutor.IsRunning)
            await game.ToSignal(game.GetTree(), SceneTree.SignalName.ProcessFrame);
        while (player.HasOpenPotionSlots)
            await PotionCmd.TryToProcure<FruitJuice>(player);
        var room = (CombatRoom)run.CurrentRoom!;
        room.AddExtraReward(player, new PotionReward(ModelDb.Potion<Fortifier>().ToMutable(), player));
        var terminalHistory = run.CurrentMapPointHistoryEntry!.GetEntry(player.NetId);
        CardModel finisher = player.Creature.CombatState!.CreateCard<Bludgeon>(player);
        finisher.DynamicVars.Damage.BaseValue = 999;
        await CardPileCmd.AddGeneratedCardToCombat(finisher, PileType.Hand, player);
        await WaitForFile(game, "/test/fixture-potion-terminal-check");
        File.WriteAllText("/test/fixture-potion-terminal-audit.json", JsonSerializer.Serialize(new
        {
            skipped = terminalHistory.PotionChoices.Count(c => c.choice == ModelDb.Potion<Fortifier>().Id && !c.wasPicked),
            picked = terminalHistory.PotionChoices.Count(c => c.choice == ModelDb.Potion<Fortifier>().Id && c.wasPicked),
        }));
        await WaitForFile(game, "/test/fixture-stop");

        void Audit(string name) => File.WriteAllText($"/test/fixture-potion-{name}-audit.json", JsonSerializer.Serialize(new
        {
            choices = history.PotionChoices.Select(c => new { model_id = c.choice.Entry, picked = c.wasPicked }),
            discarded = history.PotionDiscarded.Select(id => id.Entry),
        }));
    }

    private static async Task WaitForFile(NGame game, string path)
    {
        while (!File.Exists(path))
            await game.ToSignal(game.GetTree(), SceneTree.SignalName.ProcessFrame);
    }
}
