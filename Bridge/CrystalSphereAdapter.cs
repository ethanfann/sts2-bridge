using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;

namespace Sts2Bridge.Bridge;

internal static class CrystalSphereAdapter
{
    private sealed class Interaction
    {
        public Task? Click;
        public bool Leaving;
    }

    private static readonly ConditionalWeakTable<NCrystalSphereScreen, Interaction> Interactions = new();
    private static readonly AccessTools.FieldRef<NCrystalSphereScreen, CrystalSphereMinigame> Entity =
        AccessTools.FieldRefAccess<NCrystalSphereScreen, CrystalSphereMinigame>("_entity");
    private static readonly AccessTools.FieldRef<NCrystalSphereScreen, Tween?> Fade =
        AccessTools.FieldRefAccess<NCrystalSphereScreen, Tween?>("_fadeTween");
    private static readonly AccessTools.FieldRef<NCrystalSphereItem, Tween?> ItemTween =
        AccessTools.FieldRefAccess<NCrystalSphereItem, Tween?>("_tween");
    private static readonly AccessTools.FieldRef<NCrystalSphereItem, CrystalSphereItem> Item =
        AccessTools.FieldRefAccess<NCrystalSphereItem, CrystalSphereItem>("_item");

    private static NCrystalSphereScreen? ActiveScreen =>
        ActiveScreenContext.Instance.GetCurrentScreen() is NCrystalSphereScreen screen
        && screen.IsNodeReady() && screen.IsVisibleInTree() && !screen.IsQueuedForDeletion() ? screen : null;

    private static string Id(Node node, string kind) =>
        $"crystal_sphere_{kind}_{node.GetInstanceId().ToString(CultureInfo.InvariantCulture)}";

    private static bool Running(Tween? tween) => GodotObject.IsInstanceValid(tween) && tween!.IsRunning();

    private static IEnumerable<NCrystalSphereCell> Cells(NCrystalSphereScreen screen) =>
        screen.GetNode<Control>("%Cells").GetChildren().OfType<NCrystalSphereCell>();

    private static IEnumerable<NCrystalSphereItem> Items(NCrystalSphereScreen screen) =>
        screen.GetNode<Control>("%Items").GetChildren().OfType<NCrystalSphereItem>();

    private static bool Busy(NCrystalSphereScreen screen) =>
        Interactions.GetOrCreateValue(screen) is { Click.IsCompleted: false } or { Leaving: true }
        || Running(Fade(screen)) || Items(screen).Any(node => Running(ItemTween(node)));

    private static bool CanReveal(NCrystalSphereScreen screen, NCrystalSphereCell cell) =>
        !Busy(screen) && Entity(screen).DivinationCount > 0
        && cell.IsEnabled && cell.IsVisibleInTree() && !cell.IsQueuedForDeletion()
        && cell.MouseFilter != Control.MouseFilterEnum.Ignore && cell.Entity.IsHidden;

    private static bool CanSelectTool(NCrystalSphereScreen screen, NDivinationButton button) =>
        !Busy(screen) && Entity(screen).DivinationCount > 0
        && button.IsEnabled && button.IsVisibleInTree() && !button.IsQueuedForDeletion();

    private static bool CanProceed(NCrystalSphereScreen screen) =>
        !Busy(screen) && Entity(screen).IsFinished
        && screen.GetNode<NProceedButton>("%ProceedButton") is { IsEnabled: true } button
        && button.IsVisibleInTree() && !button.IsQueuedForDeletion();

    // Called for native UI clicks as well as bridge clicks. An asynchronous curse
    // pickup must finish before another command can spend a divination.
    public static void TrackClick(NCrystalSphereScreen screen, Task task)
    {
        Interactions.GetOrCreateValue(screen).Click = task;
        BridgeRuntime.RequestExport();
    }

    public static void TrackProceed(NCrystalSphereScreen screen)
    {
        Interactions.GetOrCreateValue(screen).Leaving = true;
        BridgeRuntime.RequestExport();
    }

    public static CrystalSphereContextSnapshot? Capture()
    {
        NCrystalSphereScreen? screen = ActiveScreen;
        if (screen is null)
            return null;
        CrystalSphereMinigame game = Entity(screen);
        var tools = new[]
        {
            (CrystalSphereMinigame.CrystalSphereToolType.Small, "%SmallDivinationButton"),
            (CrystalSphereMinigame.CrystalSphereToolType.Big, "%BigDivinationButton"),
        }.Select(entry =>
        {
            NDivinationButton button = screen.GetNode<NDivinationButton>(entry.Item2);
            return new CrystalSphereToolSnapshot(Id(button, "tool"), entry.Item1.ToString(),
                button.GetNode<Label>("%Label").Text, game.CrystalSphereTool == entry.Item1,
                CanSelectTool(screen, button));
        }).ToList();
        List<CrystalSphereItemSnapshot> revealed = [];
        foreach (NCrystalSphereItem node in Items(screen))
        {
            CrystalSphereItem item = Item(node);
            // The node exists behind a shader mask even when its item is hidden.
            // Never export its identity, bounds, rarity, or even presence until
            // its WHOLE footprint is clear. Partial artwork stays unclassified.
            bool fullyVisible = node.IsVisibleInTree() && !Running(ItemTween(node));
            for (int x = 0; x < item.Size.X && fullyVisible; x++)
                for (int y = 0; y < item.Size.Y && fullyVisible; y++)
                    fullyVisible &= !game.cells[item.Position.X + x, item.Position.Y + y].IsHidden;
            if (!fullyVisible)
                continue;
            SerializableCrystalSphereItem visible = item.ToSerializable();
            revealed.Add(new CrystalSphereItemSnapshot(visible.type.ToString(),
                item.Position.X, item.Position.Y, item.Size.X, item.Size.Y,
                visible.type == CrystalSphereItemType.Potion ? visible.potionRarity.ToString()
                    : visible.type == CrystalSphereItemType.CardReward ? visible.cardRarity.ToString() : null,
                visible.type == CrystalSphereItemType.Gold ? visible.isBigGold : null));
        }
        return new CrystalSphereContextSnapshot(game.GridSize.X, game.GridSize.Y,
            game.DivinationCount, game.CrystalSphereTool.ToString(), Busy(screen), CanProceed(screen),
            tools, Cells(screen).Select(cell => new CrystalSphereCellSnapshot(Id(cell, "cell"),
                cell.Entity.X, cell.Entity.Y, cell.Entity.IsHidden, CanReveal(screen, cell))).ToList(), revealed);
    }

    public static bool TrySelectTool(string toolId)
    {
        NCrystalSphereScreen? screen = ActiveScreen;
        if (screen is null)
            return false;
        foreach (string name in new[] { "%SmallDivinationButton", "%BigDivinationButton" })
        {
            NDivinationButton button = screen.GetNode<NDivinationButton>(name);
            if (Id(button, "tool") == toolId && CanSelectTool(screen, button))
            {
                button.EmitSignal(NClickableControl.SignalName.Released, button);
                return true;
            }
        }
        return false;
    }

    public static bool TryRevealCell(string cellId)
    {
        NCrystalSphereScreen? screen = ActiveScreen;
        if (screen is null)
            return false;
        NCrystalSphereCell? cell = Cells(screen).SingleOrDefault(cell => Id(cell, "cell") == cellId);
        if (cell is null || !CanReveal(screen, cell))
            return false;
        cell.EmitSignal(NClickableControl.SignalName.Released, cell);
        return true;
    }

    public static bool TryProceed()
    {
        NCrystalSphereScreen? screen = ActiveScreen;
        if (screen is null || !CanProceed(screen))
            return false;
        NProceedButton button = screen.GetNode<NProceedButton>("%ProceedButton");
        button.EmitSignal(NClickableControl.SignalName.Released, button);
        return true;
    }
}

internal sealed record CrystalSphereContextSnapshot(
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height,
    [property: JsonPropertyName("divinations_remaining")] int DivinationsRemaining,
    [property: JsonPropertyName("selected_tool")] string SelectedTool,
    [property: JsonPropertyName("busy")] bool Busy,
    [property: JsonPropertyName("proceed_available")] bool ProceedAvailable,
    [property: JsonPropertyName("tools")] List<CrystalSphereToolSnapshot> Tools,
    [property: JsonPropertyName("cells")] List<CrystalSphereCellSnapshot> Cells,
    [property: JsonPropertyName("revealed_items")] List<CrystalSphereItemSnapshot> RevealedItems);

internal sealed record CrystalSphereToolSnapshot(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("selected")] bool Selected,
    [property: JsonPropertyName("selectable")] bool Selectable);

internal sealed record CrystalSphereCellSnapshot(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("x")] int X,
    [property: JsonPropertyName("y")] int Y,
    [property: JsonPropertyName("hidden")] bool Hidden,
    [property: JsonPropertyName("selectable")] bool Selectable);

internal sealed record CrystalSphereItemSnapshot(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("x")] int X,
    [property: JsonPropertyName("y")] int Y,
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height,
    [property: JsonPropertyName("rarity")] string? Rarity,
    [property: JsonPropertyName("big_gold")] bool? BigGold);
