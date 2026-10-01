using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Encounters;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.ValueProps;

namespace BridgeFixtures;

internal static class CharacterFixture
{
    public static async Task Run(NGame game, RunManager manager, Player player, string eventId)
    {
        await EnterCombat();
        PlayerCombatState pcs = player.PlayerCombatState!;
        foreach (CardModel card in pcs.Hand.Cards.ToArray())
            await CardPileCmd.Add(card, PileType.Draw, skipVisuals: true);
        await PlayerCmd.GainEnergy(7, player);
        var enemy = player.Creature.CombatState!.Enemies.Single();
        await CreatureCmd.SetMaxHp(enemy, 300);
        await CreatureCmd.Heal(enemy, 300);
        ThrowingPlayerChoiceContext context = new();
        int startingStars = pcs.Stars;

        if (player.Character is Regent)
        {
            pcs.LoseStars(2); // One below Falling Star's cost, despite ample energy.
            await Add<Venerate>();
            await Add<FallingStar>();
            await Add<RefineBlade>();
            await Add<RefineBlade>(upgrade: true);
        }
        else if (player.Character is Necrobinder)
        {
            await PowerCmd.Apply<StrengthPower>(context, player.Osty!, 2, player.Creature, null);
            await Add<Bodyguard>();
            await Add<Unleash>();
        }
        else if (player.Character is Defect)
        {
            // Keep the starter Lightning, then fill the queue in a deliberately
            // nonalphabetic order. Dark has accumulated damage, not just a base value.
            await OrbCmd.Channel<FrostOrb>(context, player);
            await OrbCmd.Channel<DarkOrb>(context, player);
            await pcs.OrbQueue.Orbs.OfType<DarkOrb>().Single().Passive(context, null);
            await Add<Defragment>(upgrade: true);
            await Add<Zap>();
            await Add<Dualcast>();
            await Add<Capacitor>();
        }
        File.WriteAllText("/test/fixture-ready.json", JsonSerializer.Serialize(new
        {
            event_id = eventId, starting_stars = startingStars,
        }));

        await Wait("audit");
        // Read through the actual bridge repeatedly and ensure observations do
        // not channel/evoke, grow Dark, spend stars, or change Osty/card variables.
        Type exporter = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "sts2-bridge")
            .GetType("Sts2Bridge.Bridge.StateExporter")!;
        MethodInfo snapshot = exporter.GetMethod("BuildStableSnapshot")!;
        MethodInfo matches = exporter.GetMethod("MatchesCurrentObservation")!;
        string before = NativeState();
        for (int i = 0; i < 5; i++) snapshot.Invoke(null, null);
        if (before != NativeState()) throw new InvalidOperationException("Export mutated character state.");
        using (JsonDocument state = JsonDocument.Parse(File.ReadAllText(ProjectSettings.GlobalizePath("user://sts2-bridge/state.json"))))
        {
            string id = state.RootElement.GetProperty("state_id").GetString()!;
            CheckMatch(true);
            if (player.Character is Defect)
            {
                pcs.OrbQueue.AddCapacity(1);
                CheckMatch(false);
                pcs.OrbQueue.RemoveCapacity(1);
                CheckMatch(true);
                OrbModel front = pcs.OrbQueue.Orbs[0];
                pcs.OrbQueue.Remove(front);
                pcs.OrbQueue.Insert(1, front);
                CheckMatch(false);
                pcs.OrbQueue.Remove(front);
                pcs.OrbQueue.Insert(0, front);
            }
            else if (player.Character is Necrobinder)
            {
                player.Osty!.GainBlockInternal(1);
                CheckMatch(false);
                player.Osty.LoseBlockInternal(1);
            }
            else
            {
                pcs.GainStars(1);
                CheckMatch(false);
                pcs.LoseStars(1);
            }
            CheckMatch(true);
            void CheckMatch(bool expected)
            {
                if (!Equals(matches.Invoke(null, [id]), expected))
                    throw new InvalidOperationException($"Character guard: expected match={expected}.");
            }
        }
        Done("audit");

        if (player.Character is Necrobinder)
        {
            await Wait("kill-osty");
            await CreatureCmd.Damage(context, player.Osty!, 50, ValueProp.Unpowered, enemy);
            await Add<Bodyguard>();
            Done("kill-osty");
        }
        else if (player.Character is Defect)
        {
            await Wait("negative-focus");
            await OrbCmd.Channel<PlasmaOrb>(context, player);
            await PowerCmd.Apply<FocusPower>(context, player.Creature, -7, player.Creature, null);
            Done("negative-focus");
        }

        await Wait("reset");
        await EnterCombat();
        Done("reset");
        await Wait("stop");

        async Task Add<T>(bool upgrade = false) where T : CardModel
        {
            CardModel card = player.Creature.CombatState!.CreateCard<T>(player);
            if (upgrade)
            {
                card.UpgradeInternal();
                card.FinalizeUpgradeInternal();
            }
            await CardPileCmd.AddGeneratedCardToCombat(card, PileType.Hand, player);
        }

        async Task EnterCombat()
        {
            await manager.EnterRoomDebug(RoomType.Monster,
                model: ModelDb.Encounter<FuzzyWurmCrawlerWeak>().ToMutable(), showTransition: false);
            while (player.PlayerCombatState?.Phase != PlayerTurnPhase.Play
                || CombatManager.Instance.PlayerActionsDisabled || manager.ActionExecutor.IsRunning)
                await game.ToSignal(game.GetTree(), SceneTree.SignalName.ProcessFrame);
        }

        async Task Wait(string step)
        {
            string path = step == "stop" ? "/test/fixture-stop" : $"/test/fixture-character-{step}";
            while (!File.Exists(path))
                await game.ToSignal(game.GetTree(), SceneTree.SignalName.ProcessFrame);
        }

        void Done(string step) => File.WriteAllText($"/test/fixture-character-{step}-done", "");

        string NativeState() => JsonSerializer.Serialize(new
        {
            pcs.Energy, pcs.Stars, pcs.OrbQueue.Capacity,
            orbs = pcs.OrbQueue.Orbs.Select(o => new { o.Id, o.PassiveVal, o.EvokeVal }),
            pets = pcs.Pets.Select(p => new { p.CurrentHp, p.MaxHp, p.Block }),
            cards = pcs.AllCards.Select(c => new { c.Id, values = c.DynamicVars.Select(v => v.Value.BaseValue).ToArray() }),
        });
    }
}
