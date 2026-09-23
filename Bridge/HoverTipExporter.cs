using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.HoverTips;

namespace Sts2Bridge.Bridge;

internal static class HoverTipExporter
{
    // Export only the tooltip list supplied for the observed option/model. The
    // game includes relevant keyword tips here; don't traverse the model catalog
    // or generate unseen event options to invent a more complete explanation.
    public static List<HoverTipSnapshot> Build(IEnumerable<IHoverTip> tips) => tips.Select(BuildTip).ToList();

    private static HoverTipSnapshot BuildTip(IHoverTip tip)
    {
        if (tip is HoverTip text)
        {
            return new HoverTipSnapshot(tip.Id, "text", text.Title, text.Description);
        }
        if (tip is CardHoverTip preview)
        {
            var card = preview.Card;
            // These can be fresh preview objects on every poll, not cards the
            // player owns. Never assign an action ID or calculate play targets.
            return new HoverTipSnapshot(tip.Id, "card", card.Title, card.GetDescriptionForPile(PileType.None))
            {
                Card = new ReferencedCardSnapshot(card.Id.Entry,
                    card.EnergyCost.CostsX ? "X" : card.EnergyCost.GetWithModifiers(CostModifiers.All).ToString(CultureInfo.InvariantCulture),
                    card.GetStarCostWithModifiers(), card.HasStarCostX, card.Type.ToString(),
                    card.TargetType.ToString(), card.Rarity.ToString(), card.CurrentUpgradeLevel),
            };
        }
        return new HoverTipSnapshot(tip.Id, "unsupported", null, null)
        {
            UnsupportedType = tip.GetType().FullName,
        };
    }
}

internal sealed record HoverTipSnapshot(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("description")] string? Description)
{
    [JsonPropertyName("card")]
    public ReferencedCardSnapshot? Card { get; init; }

    [JsonPropertyName("unsupported_type")]
    public string? UnsupportedType { get; init; }
}

internal sealed record ReferencedCardSnapshot(
    [property: JsonPropertyName("model_id")] string ModelId,
    [property: JsonPropertyName("cost")] string Cost,
    [property: JsonPropertyName("star_cost")] int StarCost,
    [property: JsonPropertyName("costs_stars_x")] bool CostsStarsX,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("target_type")] string TargetType,
    [property: JsonPropertyName("rarity")] string Rarity,
    [property: JsonPropertyName("upgrade_level")] int UpgradeLevel);
