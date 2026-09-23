using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;

namespace Sts2Bridge.Bridge;

internal static class CardStateExporter
{
    // Instance identity, not card type or pile position. Random IDs avoid leaking
    // draw order through the order in which previously unseen cards are exported.
    // The weak table follows game object lifetimes and never touches the game's RNG.
    private sealed record Identity(string Id);
    private static readonly ConditionalWeakTable<CardModel, Identity> Identities = new();

    public static string Id(CardModel card) => Identities.GetValue(card,
        _ => new Identity($"card_{Guid.NewGuid():N}")).Id;

    public static List<CardSnapshot> BuildPile(CardPile? pile)
    {
        IEnumerable<CardModel> cards = pile?.Cards ?? [];
        if (pile?.Type == PileType.Draw)
        {
            // Humans can inspect membership, not the secret draw sequence.
            cards = cards.OrderBy(card => card.Id.Entry, StringComparer.Ordinal)
                .ThenBy(Id, StringComparer.Ordinal);
        }
        return cards.Select(BuildCard).ToList();
    }

    public static CardPilesSnapshot? BuildPiles(PlayerCombatState? state) => state is null ? null : new(
        BuildPile(state.DrawPile), BuildPile(state.DiscardPile), BuildPile(state.ExhaustPile), BuildPile(state.PlayPile));

    public static CardSnapshot BuildCard(CardModel card)
    {
        bool inHand = card.Pile?.Type == PileType.Hand;
        bool playable = inHand && !BridgeIntrospection.HasPendingHandSelection && card.CanPlay();
        List<CardTargetPreview> targets = [];
        if (playable)
        {
            if (card.IsValidTarget(null))
            {
                targets.Add(new CardTargetPreview(null, PreviewValues(card, null)));
            }
            if (card.CombatState is ICombatState combat)
            {
                for (int i = 0; i < combat.Enemies.Count; i++)
                {
                    Creature target = combat.Enemies[i];
                    if (target.IsHittable && card.IsValidTarget(target))
                    {
                        targets.Add(new CardTargetPreview(BridgeIntrospection.BuildCreatureId(target, i), PreviewValues(card, target)));
                    }
                }
            }
        }
        return new CardSnapshot
        {
            Id = Id(card),
            ModelId = card.Id.Entry,
            DeckCardId = card.DeckVersion is CardModel deckCard ? Id(deckCard) : null,
            Name = card.Title,
            Description = card.GetDescriptionForPile(card.Pile?.Type ?? PileType.None),
            HoverTips = HoverTipExporter.Build(card.HoverTips),
            Cost = card.EnergyCost.CostsX ? "X" : card.EnergyCost.GetWithModifiers(CostModifiers.All).ToString(CultureInfo.InvariantCulture),
            EnergyCost = card.EnergyCost.GetWithModifiers(CostModifiers.All),
            CostsX = card.EnergyCost.CostsX,
            StarCost = card.GetStarCostWithModifiers(),
            CostsStarsX = card.HasStarCostX,
            Rarity = card.Rarity.ToString(),
            Type = card.Type.ToString(),
            TargetType = card.TargetType.ToString(),
            UpgradeLevel = card.CurrentUpgradeLevel,
            Keywords = card.Keywords.Select(k => k.ToString()).Order(StringComparer.Ordinal).ToList(),
            RetainThisTurn = card.ShouldRetainThisTurn,
            ExhaustOnNextPlay = card.ExhaustOnNextPlay,
            Enchantment = card.Enchantment?.Id.Entry,
            Affliction = card.Affliction?.Id.Entry,
            BaseValues = card.DynamicVars.ToDictionary(v => v.Key, v => v.Value.BaseValue),
            Playable = playable,
            PlayTargets = targets,
        };
    }

    public static CardUpgradePreviewSnapshot? BuildUpgradePreview(CardModel card)
    {
        if (!card.IsUpgradable)
        {
            return null;
        }

        // Match NGridCardHolder's "View Upgrades" path, including instance
        // modifications. Do not register the copy with RunState.CloneCard,
        // upgrade the owned card, or assign the preview an action ID.
        CardModel preview = (CardModel)card.MutableClone();
        preview.UpgradeInternal();
        preview.DynamicVars.ClearPreview();
        preview.UpdateDynamicVarPreview(CardPreviewMode.Upgrade, null, preview.DynamicVars);
        if (preview.Enchantment is not null)
        {
            preview.Enchantment.DynamicVars.ClearPreview();
            preview.UpdateDynamicVarPreview(CardPreviewMode.Upgrade, null, preview.Enchantment.DynamicVars);
        }
        return new CardUpgradePreviewSnapshot(
            preview.Title, preview.GetDescriptionForUpgradePreview(),
            preview.EnergyCost.CostsX ? "X" : preview.EnergyCost.GetWithModifiers(CostModifiers.All).ToString(CultureInfo.InvariantCulture),
            preview.EnergyCost.GetWithModifiers(CostModifiers.All), preview.EnergyCost.CostsX,
            preview.GetStarCostWithModifiers(), preview.HasStarCostX, preview.CurrentUpgradeLevel,
            preview.Type.ToString(), preview.TargetType.ToString(),
            preview.Keywords.Select(k => k.ToString()).Order(StringComparer.Ordinal).ToList(),
            preview.DynamicVars.ToDictionary(v => v.Key, v => v.Value.BaseValue),
            HoverTipExporter.Build(preview.HoverTips));
    }

    private static Dictionary<string, decimal> PreviewValues(CardModel card, Creature? target)
    {
        // Use the game's UI-preview hooks, but on cloned variables. Exporting
        // must not alter the live card's hover preview or execute card effects.
        DynamicVarSet values = card.DynamicVars.Clone(card);
        card.UpdateDynamicVarPreview(CardPreviewMode.Normal, target, values);
        return values.ToDictionary(v => v.Key, v => v.Value.PreviewValue);
    }

    public static List<CardPlaySnapshot> BuildPlayedCards(CombatState? combat, Player? player)
    {
        if (combat is null || player is null)
        {
            return [];
        }
        return CombatManager.Instance.History.CardPlaysFinished
            .Where(entry => entry.CardPlay.Card.Owner == player)
            .Select((entry, index) =>
            {
                CardPlay play = entry.CardPlay;
                return new CardPlaySnapshot(index + 1, Id(play.Card), play.Card.Id.Entry,
                    TargetId(play.Target, combat), play.Resources.EnergySpent, play.Resources.StarsSpent,
                    play.IsAutoPlay, play.PlayIndex, play.PlayCount, play.ResultPile.ToString(),
                    entry.HappenedThisTurn(combat), entry.HappenedLastPlayerTurn(player));
            }).ToList();
    }

    public static string? TargetId(Creature? target, ICombatState combat)
    {
        if (target is null)
        {
            return null;
        }
        if (target.IsPlayer)
        {
            return "self";
        }
        return BridgeIntrospection.BuildCreatureId(target, combat.Enemies.ToList().IndexOf(target));
    }
}

internal sealed record CardPilesSnapshot(
    [property: JsonPropertyName("draw")] List<CardSnapshot> Draw,
    [property: JsonPropertyName("discard")] List<CardSnapshot> Discard,
    [property: JsonPropertyName("exhaust")] List<CardSnapshot> Exhaust,
    [property: JsonPropertyName("play")] List<CardSnapshot> Play)
{
    [JsonPropertyName("draw_order_known")]
    public bool DrawOrderKnown => false;
}

internal sealed record CardTargetPreview(
    [property: JsonPropertyName("target_id")] string? TargetId,
    [property: JsonPropertyName("values")] Dictionary<string, decimal> Values);

internal sealed record CardUpgradePreviewSnapshot(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("cost")] string Cost,
    [property: JsonPropertyName("energy_cost")] int EnergyCost,
    [property: JsonPropertyName("costs_x")] bool CostsX,
    [property: JsonPropertyName("star_cost")] int StarCost,
    [property: JsonPropertyName("costs_stars_x")] bool CostsStarsX,
    [property: JsonPropertyName("upgrade_level")] int UpgradeLevel,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("target_type")] string TargetType,
    [property: JsonPropertyName("keywords")] List<string> Keywords,
    [property: JsonPropertyName("base_values")] Dictionary<string, decimal> BaseValues,
    [property: JsonPropertyName("hover_tips")] List<HoverTipSnapshot> HoverTips);

internal sealed record CardPlaySnapshot(
    [property: JsonPropertyName("sequence")] int Sequence,
    [property: JsonPropertyName("card_id")] string CardId,
    [property: JsonPropertyName("model_id")] string ModelId,
    [property: JsonPropertyName("target_id")] string? TargetId,
    [property: JsonPropertyName("energy_spent")] int EnergySpent,
    [property: JsonPropertyName("stars_spent")] int StarsSpent,
    [property: JsonPropertyName("auto_play")] bool AutoPlay,
    [property: JsonPropertyName("play_index")] int PlayIndex,
    [property: JsonPropertyName("play_count")] int PlayCount,
    [property: JsonPropertyName("result_pile")] string ResultPile,
    [property: JsonPropertyName("this_turn")] bool ThisTurn,
    [property: JsonPropertyName("last_player_turn")] bool LastPlayerTurn);
