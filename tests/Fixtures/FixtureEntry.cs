using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Platform.Steam;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace BridgeFixtures;

// This assembly is mounted ONLY by the offline test launcher. It is not part of
// sts2-bridge.dll and the normal installer never copies it into the game.
[ModInitializer(nameof(Initialize))]
public static class FixtureEntry
{
    public static void Initialize()
    {
        if (SteamInitializer.Initialized || CommandLineHelper.GetValue("force-steam") != "off"
            || !ProjectSettings.GlobalizePath("user://").StartsWith("/test/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("BridgeFixtures may only run in the offline /test sandbox.");
        }
        Callable.From(() => { _ = Start(); }).CallDeferred();
    }

    private static async Task Start()
    {
        try
        {
            NGame game = NGame.Instance!;
            while (game.MainMenu is null || game.LogoAnimation is not null)
            {
                await game.ToSignal(game.GetTree(), SceneTree.SignalName.ProcessFrame);
            }
            // Complete ordinary asset loading; do not use AutoSlay or replace
            // the card selector, game actions, rewards, or combat resolution.
            await PreloadManager.LoadCommonAndMainMenuAssets();
            SaveManager.Instance.MarkFtueAsComplete("accept_tutorials_ftue");
            SaveManager.Instance.SetFtuesEnabled(enabled: false);
            string eventId = CommandLineHelper.GetValue("bridge-fixture-event")
                ?? throw new InvalidOperationException("Missing fixture event ID");
            File.WriteAllText("/test/event-catalog.json", JsonSerializer.Serialize(
                ModelDb.AllEvents.Concat(ModelDb.AllAncients).Select(e => new
                {
                    id = e.Id.Entry, model = e.GetType().Name, deterministic = e.IsDeterministic,
                })));
            CharacterModel character = ModelDb.Character<Ironclad>();
            Player player = Player.CreateForNewRun(character, SaveManager.Instance.GenerateUnlockStateFromProgress(), 1);
            RunState state = RunState.CreateForNewRun([player],
                ActModel.GetDefaultList().Select(a => a.ToMutable()).ToList(), [], GameMode.Standard, 0, "BRIDGEFIXTURE");
            RunManager manager = RunManager.Instance;
            manager.SetUpNewSingleplayer(state, shouldSave: false);
            await PreloadManager.LoadRunAssets([character]);
            manager.Launch();
            game.RootSceneContainer.SetCurrentScene(NRun.Create(state));
            await manager.SetActInternal(0);
            manager.RunLocationTargetedBuffer.OnLocationChanged(state.RunLocation);
            manager.MapSelectionSynchronizer.OnLocationChanged(state.MapLocation);
            if (eventId == "COMBAT_CONTEXT")
            {
                await CombatFixture.Run(game, manager, state, player);
                game.GetTree().Quit();
                return;
            }
            if (eventId == "COMBAT_PILE_SELECTION")
            {
                await PileSelectionFixture.Run(game, manager, player);
                game.GetTree().Quit();
                return;
            }
            if (eventId == "COMBAT_HAND_SELECTION")
            {
                await HandSelectionFixture.Run(game, manager, player);
                game.GetTree().Quit();
                return;
            }
            if (eventId == "POTION_REWARDS")
            {
                await PotionRewardFixture.Run(game, manager, state, player);
                game.GetTree().Quit();
                return;
            }
            if (eventId == "CARD_REWARDS")
            {
                await CardRewardFixture.Run(game, manager, state, player);
                game.GetTree().Quit();
                return;
            }
            if (eventId == "DECK_UPGRADE")
            {
                await UpgradeFixture.Run(game, manager, state, player);
                game.GetTree().Quit();
                return;
            }
            if (eventId == "DECK_SELECTION")
            {
                await DeckSelectionFixture.Run(game, manager, state, player);
                game.GetTree().Quit();
                return;
            }
            if (eventId == "SHOP_REST_CONTEXT")
            {
                await ShopRestFixture.Run(game, manager, state, player);
                game.GetTree().Quit();
                return;
            }
            if (eventId == "FAKE_MERCHANT")
            {
                await FakeMerchantFixture.Run(game, manager, state, player);
                game.GetTree().Quit();
                return;
            }
            if (eventId == "CRYSTAL_SPHERE")
            {
                await CrystalSphereFixture.Run(game, manager, state, player);
                game.GetTree().Quit();
                return;
            }
            if (eventId is "TREASURE_CONTEXT" or "TREASURE_EMPTY")
            {
                await TreasureFixture.Run(game, manager, player, empty: eventId == "TREASURE_EMPTY");
                game.GetTree().Quit();
                return;
            }
            if (eventId is "MAP_NORMAL" or "MAP_BOSS")
            {
                await MapFixture.Run(game, manager, state, boss: eventId == "MAP_BOSS");
                game.GetTree().Quit();
                return;
            }
            // The post-boss Architect is not in the random event/ancient pools.
            EventModel model = eventId == "THE_ARCHITECT" ? ModelDb.Event<TheArchitect>()
                : ModelDb.AllEvents.Concat(ModelDb.AllAncients).Single(e => e.Id.Entry == eventId);
            if (eventId == "THE_ARCHITECT")
            {
                // Reproduce the A1 playtest's second-visit dialogue, including
                // Continue. This progress belongs only to the disposable account.
                SaveManager.Instance.Progress.GetOrCreateCharacterStats(character.Id).TotalWins = 1;
            }
            await manager.EnterRoomDebug(RoomType.Event, MapPointType.Unknown, model, showTransition: false);
            if (eventId == "NEOW")
            {
                // Force these real offers only in the disposable fixture. This
                // avoids depending on unlocks/RNG while retaining their normal
                // descriptions, tooltips, selection UI, and on-pickup effects.
                Neow neow = (Neow)((EventRoom)state.CurrentRoom!).LocalMutableEvent;
                string[] relics = ["SCROLL_BOXES", "NEOWS_TORMENT", "SILKEN_TRESS"];
                var options = neow.AllPossibleOptions
                    .Where(option => relics.Contains(option.Relic?.Id.Entry)).ToArray();
                if (options.Length != 3)
                {
                    throw new InvalidOperationException("Expected three Neow mechanics fixture offers.");
                }
                typeof(EventModel).GetMethod("SetEventState", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(neow, [neow.InitialDescription, options]);
            }
            File.WriteAllText("/test/fixture-ready.json", JsonSerializer.Serialize(new { event_id = eventId, seed = state.Rng.StringSeed }));
            while (!File.Exists("/test/fixture-stop"))
            {
                await game.ToSignal(game.GetTree(), SceneTree.SignalName.ProcessFrame);
            }
            game.GetTree().Quit();
        }
        catch (Exception exception)
        {
            File.WriteAllText("/test/fixture-error.txt", exception.ToString());
            MegaCrit.Sts2.Core.Logging.Log.Error($"Bridge fixture failed: {exception}");
        }
    }
}
