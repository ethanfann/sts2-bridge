using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Godot;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;

namespace Sts2Bridge.Bridge;

internal static class ScreenDiagnostics
{
    public static bool IsSupported(object? screen) => screen?.GetType().Name is
        null or "NMainMenu" or "NCombatRoom" or "NEventRoom" or "NMapScreen"
        or "NMerchantRoom" or "NMerchantInventory" or "NRestSiteRoom" or "NTreasureRoom"
        or "NFakeMerchant" or "NFakeMerchantInventory"
        or "NRewardsScreen" or "NChooseARelicSelection" or "NCardRewardSelectionScreen"
        or "NDeckTransformSelectScreen" or "NDeckCardSelectScreen" or "NChooseACardSelectionScreen"
        or "NSimpleCardSelectScreen" or "NDeckUpgradeSelectScreen" or "NDeckEnchantSelectScreen"
        or "NCombatPileCardSelectScreen";

    public static ScreenSnapshot Capture()
    {
        object? screen = ActiveScreenContext.Instance.GetCurrentScreen();
        List<ScreenControlSnapshot> controls = [];
        bool truncated = false;
        if (screen is Node root)
        {
            CaptureControls(root, root, controls, ref truncated);
        }
        return new ScreenSnapshot(screen?.GetType().Name, IsSupported(screen), controls, truncated);
    }

    public static object CaptureForError()
    {
        try
        {
            return Capture();
        }
        catch (Exception exception)
        {
            return new { diagnostic_error = exception.ToString() };
        }
    }

    private static void CaptureControls(Node root, Node node, List<ScreenControlSnapshot> controls, ref bool truncated)
    {
        if (node is CanvasItem item && !item.IsVisibleInTree())
        {
            return;
        }
        if (controls.Count >= 120)
        {
            truncated = true;
            return;
        }
        string? text = node switch
        {
            RichTextLabel label => label.GetParsedText(),
            Label label => label.Text,
            Button button => button.Text,
            _ => null,
        };
        if (node is NClickableControl or BaseButton || !string.IsNullOrWhiteSpace(text))
        {
            controls.Add(new ScreenControlSnapshot(
                root.GetPathTo(node).ToString(), node.GetType().Name,
                text is { Length: > 2000 } ? text[..2000] : text,
                node is BaseButton button ? button.Disabled : null));
        }
        foreach (Node child in node.GetChildren())
        {
            CaptureControls(root, child, controls, ref truncated);
        }
    }
}

internal sealed record ScreenSnapshot(
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("supported")] bool Supported,
    [property: JsonPropertyName("visible_controls")] List<ScreenControlSnapshot> VisibleControls,
    [property: JsonPropertyName("truncated")] bool Truncated);

internal sealed record ScreenControlSnapshot(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("disabled")] bool? Disabled);
