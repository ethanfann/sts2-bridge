using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace BridgeFixtures;

internal static class CrystalSphereFixture
{
    public static async Task Run(NGame game, RunManager manager, RunState run, Player player)
    {
        await manager.SetActInternal(1);
        player.Gold = 200;
        await manager.EnterRoomDebug(RoomType.Event, MapPointType.Unknown, ModelDb.Event<CrystalSphere>(), showTransition: false);
        EventModel model = ((EventRoom)run.CurrentRoom!).LocalMutableEvent;
        File.WriteAllText("/test/fixture-ready.json", JsonSerializer.Serialize(new
        {
            event_id = "CRYSTAL_SPHERE", cost = model.DynamicVars["UncoverFutureCost"].BaseValue,
        }));
        while (ActiveScreenContext.Instance.GetCurrentScreen() is not NCrystalSphereScreen)
            await game.ToSignal(game.GetTree(), SceneTree.SignalName.ProcessFrame);
        var screen = (NCrystalSphereScreen)ActiveScreenContext.Instance.GetCurrentScreen()!;
        var entity = (CrystalSphereMinigame)typeof(NCrystalSphereScreen)
            .GetField("_entity", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(screen)!;
        // Test-only ground truth, never emitted by the bridge or mounted in a
        // live game. Python uses it to exercise partial reveals and the curse.
        File.WriteAllText("/test/fixture-sphere.json", JsonSerializer.Serialize(new
        {
            items = entity.Items.Select(item => new
            {
                kind = item.ToSerializable().type.ToString(), x = item.Position.X, y = item.Position.Y,
                width = item.Size.X, height = item.Size.Y,
            }),
            shader = ((ShaderMaterial)screen.GetNode<Control>("%ScryMask").Material).Shader.Code,
        }));
        string rng = JsonSerializer.Serialize(player.PlayerRng.ToSerializable().Counters);
        int eventRng = entity.Rng.Counter;
        int gold = player.Gold;
        int deck = player.Deck.Cards.Count;
        int divinations = entity.DivinationCount;
        var cells = screen.GetNode<Control>("%Cells").GetChildren().OfType<NCrystalSphereCell>().ToArray();
        bool[] hidden = cells.Select(c => c.Entity.IsHidden).ToArray();
        NDivinationButton small = screen.GetNode<NDivinationButton>("%SmallDivinationButton");
        NCrystalSphereCell cell = cells.Single(c => c.Entity.X == 5 && c.Entity.Y == 5);

        await Wait(game, "disable");
        small.Disable();
        cell.Disable();
        await Wait(game, "enable");
        small.Enable();
        cell.Enable();
        await Wait(game, "busy");
        // Hold the exact task gate used by asynchronous native clicks, without
        // relying on a race against a short curse acquisition animation.
        Type adapter = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "sts2-bridge")
            .GetType("Sts2Bridge.Bridge.CrystalSphereAdapter")!;
        var pending = new TaskCompletionSource();
        adapter.GetMethod("TrackClick")!.Invoke(null, [screen, pending.Task]);
        await Wait(game, "idle");
        pending.SetResult();
        await Wait(game, "cover");
        NMapScreen.Instance!.Open(isOpenedFromTopBar: true);
        await Wait(game, "uncover");
        NMapScreen.Instance.Close(animateOut: false);
        await Wait(game, "check");
        if (JsonSerializer.Serialize(player.PlayerRng.ToSerializable().Counters) != rng
            || entity.Rng.Counter != eventRng
            || player.Gold != gold || player.Deck.Cards.Count != deck || entity.DivinationCount != divinations
            || !cells.Select(c => c.Entity.IsHidden).SequenceEqual(hidden))
            throw new InvalidOperationException("Reads/rejected commands mutated Sphere, RNG, or player.");

        MethodInfo capture = adapter.GetMethod("Capture")!;
        string before = JsonSerializer.Serialize(capture.Invoke(null, null));
        var nodes = screen.GetNode<Control>("%Items").GetChildren().OfType<NCrystalSphereItem>().ToArray();
        FieldInfo itemField = typeof(NCrystalSphereItem).GetField("_item", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object first = itemField.GetValue(nodes[0])!;
        object second = itemField.GetValue(nodes[1])!;
        // Different hidden contents must produce identical observations. Swap
        // only within this frame and restore before native callbacks can run.
        try
        {
            itemField.SetValue(nodes[0], second);
            itemField.SetValue(nodes[1], first);
            if (JsonSerializer.Serialize(capture.Invoke(null, null)) != before)
                throw new InvalidOperationException("Hidden item identity affected the observation.");
        }
        finally
        {
            itemField.SetValue(nodes[0], first);
            itemField.SetValue(nodes[1], second);
        }
        File.WriteAllText("/test/fixture-sphere-checked", "");
        while (!File.Exists("/test/fixture-stop"))
            await game.ToSignal(game.GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private static async Task Wait(NGame game, string step)
    {
        while (!File.Exists($"/test/fixture-sphere-{step}"))
            await game.ToSignal(game.GetTree(), SceneTree.SignalName.ProcessFrame);
    }
}
