using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private static void AssertMultiplayerPowerLifecycle(CombatState source)
    {
        AssertMultiplayerDamagePowerLifecycle(source);
        AssertMultiplayerReactivePowerLifecycle(source);
        AssertMultiplayerCardHookLifecycle(source);
        AssertMultiplayerTurnAndDeathPowerLifecycle(source);
    }

    private static void AssertMultiplayerCardHookLifecycle(CombatState source)
    {
        OfflineJointCombat native = CreateOfflineJointCombat(source, actorCount: 2);
        Player nativeSecond = native.Players[1];
        nativeSecond.PlayerCombatState!.Energy = 99;
        CardModel strike = native.State.CreateCard(ModelDb.Card<StrikeIronclad>(), nativeSecond);
        nativeSecond.PlayerCombatState.Hand.AddInternal(strike, silent: true);
        CombatRootSnapshot root = CombatRootSnapshot.Capture(native.State);
        CombatPredictionSimulator simulator = root.ForkSimulator();
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        Player first = simulator.State.Players[0];
        Player second = simulator.State.Players[1];
        var enemy = combat.Enemies.Single();

        combat.AddPowerInstance<SneakyPower>(first.Creature, 3, first.Creature);
        combat.AddPowerInstance<TagTeamPower>(enemy, 1, first.Creature);
        _ = JointActionTransition.Apply(
            simulator,
            JointTurnState.Start(2, root.StartTurnNumber),
            new PlanAction(
                PlanActionKind.PlayCard,
                root.StartTurnNumber,
                CardId: strike.Id.Entry,
                TargetCombatId: enemy.CombatId,
                Actor: new CombatActorId(1)),
            JointActionTransition.CaptureProcessedEnemyDeaths(root, simulator));
        if (simulator.State.GetCreature(first.Creature).Block < 3)
            throw new InvalidOperationException("Sneaky did not react to another player's Attack.");
        AssertEqual(0, combat.GetAmount<TagTeamPower>(enemy), "Tag Team consumption");

        PredictedCard midnight = PredictedCard.Create(CanonicalModels.Card<Midnight>(), first);
        PredictedCard exhaust = PredictedCard.Create(CanonicalModels.Card<DefendIronclad>(), first);
        simulator.AddToPile(midnight, PileType.Hand);
        simulator.AddToPile(exhaust, PileType.Hand);
        int costBefore = midnight.Preview.EnergyCost.GetWithModifiers(CostModifiers.All);
        simulator.Exhaust(exhaust);
        int costAfter = midnight.Preview.EnergyCost.GetWithModifiers(CostModifiers.All);
        AssertEqual(costBefore - 1, costAfter, "Midnight exhaust cost reduction");

        combat.ApplyTemporaryStrengthGain<CoordinatePower>(first.Creature, 2, first.Creature);
        combat.ApplyTemporaryDexterity<FadePower>(first.Creature, 2, first.Creature);
        if (combat.GetAmount<StrengthPower>(first.Creature) != 2
            || combat.GetAmount<DexterityPower>(first.Creature) != 2)
        {
            throw new InvalidOperationException("Coordinate/Fade did not establish temporary stats.");
        }
        if (!CorePowerSupport.TriggerPlayerRegularSideTurnEndEffects(
                simulator, combat, [first.Creature, second.Creature]))
        {
            throw new InvalidOperationException("Player side lifecycle unexpectedly suspended.");
        }
        AssertEqual(0, combat.GetAmount<StrengthPower>(first.Creature), "Coordinate restoration");
        AssertEqual(0, combat.GetAmount<DexterityPower>(first.Creature), "Fade restoration");
    }

    private static void AssertMultiplayerDamagePowerLifecycle(CombatState source)
    {
        (CombatPredictionSimulator simulator, SimulatedCombatState combat, Player first, Player second,
            _) = CreateMultiplayerPowerFixture(source);
        var enemy = combat.Enemies.Single();

        combat.AddPowerInstance<KnockdownPower>(enemy, 2, first.Creature);
        int before = simulator.State.GetCreature(enemy).CurrentHp;
        simulator.Damage(enemy, 3, ValueProp.Move, second.Creature);
        AssertEqual(before - 6, simulator.State.GetCreature(enemy).CurrentHp, "Knockdown teammate multiplier");

        combat.SetPowerAmount(combat.EffectivePowers().OfType<KnockdownPower>().Single(), 0);
        combat.AddPowerInstance<FlankingPower>(enemy, 2, first.Creature);
        before = simulator.State.GetCreature(enemy).CurrentHp;
        simulator.Damage(enemy, 3, ValueProp.Move, first.Creature);
        AssertEqual(before - 3, simulator.State.GetCreature(enemy).CurrentHp, "Flanking applier exclusion");
        simulator.Damage(enemy, 3, ValueProp.Move, second.Creature);
        AssertEqual(before - 9, simulator.State.GetCreature(enemy).CurrentHp, "Flanking teammate multiplier");

        combat.AddPowerInstance<TankPower>(first.Creature, 1, first.Creature);
        combat.AddPowerInstance<GuardedPower>(second.Creature, 1, first.Creature);
        int firstHp = simulator.State.GetCreature(first.Creature).CurrentHp;
        int secondHp = simulator.State.GetCreature(second.Creature).CurrentHp;
        simulator.Damage(first.Creature, 10, ValueProp.Move, enemy);
        simulator.Damage(second.Creature, 10, ValueProp.Move, enemy);
        AssertEqual(firstHp - 15, simulator.State.GetCreature(first.Creature).CurrentHp, "Tank damage increase");
        AssertEqual(secondHp - 5, simulator.State.GetCreature(second.Creature).CurrentHp, "Guarded damage decrease");

        combat.AddPowerInstance<InterceptPower>(first.Creature, 1, first.Creature);
        InterceptPower intercept = combat.EffectivePowers().OfType<InterceptPower>().Single();
        simulator.StateStore.Get(intercept, () => new InterceptPredictionState(intercept))
            .CoveredCreatures.Add(second.Creature);
        combat.AddPowerInstance<CoveredPower>(second.Creature, 1, first.Creature);
        firstHp = simulator.State.GetCreature(first.Creature).CurrentHp;
        secondHp = simulator.State.GetCreature(second.Creature).CurrentHp;
        simulator.Damage(first.Creature, 2, ValueProp.Move, enemy);
        simulator.Damage(second.Creature, 2, ValueProp.Move, enemy);
        AssertEqual(firstHp - 6, simulator.State.GetCreature(first.Creature).CurrentHp,
            "Intercept covered-count multiplier after Tank");
        AssertEqual(secondHp, simulator.State.GetCreature(second.Creature).CurrentHp, "Covered immunity");
    }

    private static void AssertMultiplayerReactivePowerLifecycle(CombatState source)
    {
        (CombatPredictionSimulator simulator, SimulatedCombatState combat, Player first, Player second,
            _) = CreateMultiplayerPowerFixture(source);
        var enemy = combat.Enemies.Single();

        combat.AddPowerInstance<BeaconOfHopePower>(first.Creature, 1, first.Creature);
        simulator.GainBlock(first.Creature, 10, ValueProp.Unpowered);
        AssertEqual(5, simulator.State.GetCreature(second.Creature).Block, "Beacon teammate block");

        combat.AddPowerInstance<ConcoctPower>(first.Creature, 2, first.Creature);
        simulator.Damage(enemy, 3, ValueProp.Move, first.Creature);
        AssertEqual(2, combat.GetAmount<PoisonPower>(enemy), "Concoct poison");

        combat.AddPowerInstance<UnderworldPower>(first.Creature, 2, first.Creature);
        simulator.Damage(enemy, 3, ValueProp.Move, second.Creature);
        AssertEqual(6, combat.GetAmount<DoomPower>(enemy), "Underworld teammate doom");

        combat.AddPowerInstance<CacophonyPower>(first.Creature, 4, first.Creature);
        CacophonyPower cacophony = combat.EffectivePowers().OfType<CacophonyPower>().Single();
        simulator.StateStore.Get(cacophony, () => new CacophonyPredictionState(cacophony)).CardsDrawn = 1;
        simulator.AddToPile(
            PredictedCard.Create(CanonicalModels.Card<StrikeIronclad>(), first),
            PileType.Draw,
            CardPilePosition.Bottom);
        int before = simulator.State.GetCreature(enemy).CurrentHp;
        simulator.Draw(first, 1);
        AssertEqual(before - 4, simulator.State.GetCreature(enemy).CurrentHp, "Cacophony draw trigger");

        combat.AddPowerInstance<SoulboundPower>(second.Creature, 2, first.Creature);
        int soulsBefore = simulator.State.GetPlayerCombatState(second).DrawPile.Cards
            .Count(card => card.Preview is Soul);
        simulator.CreateAndAddGeneratedCardsToCombat<Soul>(
            first, PileType.Draw, 1, first, CardPilePosition.Bottom);
        int soulsAfter = simulator.State.GetPlayerCombatState(second).DrawPile.Cards
            .Count(card => card.Preview is Soul);
        AssertEqual(soulsBefore + 2, soulsAfter, "Soulbound generated souls");

        combat.AddPowerInstance<HammerTimePower>(first.Creature, 1, first.Creature);
        PersistentPowerSupport.Forge(simulator, first, 2);
        SovereignBlade firstBlade = (SovereignBlade)simulator.State.GetPlayerCombatState(first).AllCards
            .Single(card => card.Preview is SovereignBlade).Preview;
        SovereignBlade secondBlade = (SovereignBlade)simulator.State.GetPlayerCombatState(second).AllCards
            .Single(card => card.Preview is SovereignBlade).Preview;
        AssertEqual(firstBlade.DynamicVars.Damage.IntValue, secondBlade.DynamicVars.Damage.IntValue,
            "Hammer Time propagated forge");
    }

    private static void AssertMultiplayerTurnAndDeathPowerLifecycle(CombatState source)
    {
        (CombatPredictionSimulator simulator, SimulatedCombatState combat, Player first, Player second,
            _) = CreateMultiplayerPowerFixture(source);
        var enemy = combat.Enemies.Single();
        combat.AddPowerInstance<CoveredPower>(second.Creature, 1, first.Creature);
        combat.AddPowerInstance<GuardedPower>(second.Creature, 1, first.Creature);
        simulator.Kill(first.Creature);
        if (combat.EffectivePowers().Any(power =>
                power.Owner == second.Creature
                && power.Amount > 0
                && power is CoveredPower or GuardedPower))
        {
            throw new InvalidOperationException("Covered/Guarded survived applier death.");
        }

        (simulator, combat, first, second, _) = CreateMultiplayerPowerFixture(source);
        enemy = combat.Enemies.Single();
        combat.AddPowerInstance<InterceptPower>(first.Creature, 1, first.Creature);
        combat.AddPowerInstance<CoveredPower>(second.Creature, 1, first.Creature);
        combat.AddPowerInstance<ConcoctPower>(first.Creature, 1, first.Creature);
        combat.AddPowerInstance<UnderworldPower>(first.Creature, 1, first.Creature);
        combat.AddPowerInstance<KnockdownPower>(enemy, 2, first.Creature);
        combat.AddPowerInstance<FlankingPower>(enemy, 2, first.Creature);
        if (!CorePowerSupport.TriggerEnemySideTurnEndEffects(simulator, combat, [enemy]))
            throw new InvalidOperationException("Enemy side lifecycle unexpectedly suspended.");
        AssertEqual(0, combat.GetAmount<InterceptPower>(first.Creature), "Intercept enemy-side removal");
        AssertEqual(0, combat.GetAmount<CoveredPower>(second.Creature), "Covered enemy-side removal");
        AssertEqual(0, combat.GetAmount<ConcoctPower>(first.Creature), "Concoct enemy-side removal");
        AssertEqual(0, combat.GetAmount<UnderworldPower>(first.Creature), "Underworld enemy-side removal");
        AssertEqual(0, combat.GetAmount<KnockdownPower>(enemy), "Knockdown participant removal");
        AssertEqual(0, combat.GetAmount<FlankingPower>(enemy), "Flanking participant removal");
    }

    private static (CombatPredictionSimulator Simulator, SimulatedCombatState Combat, Player First,
        Player Second, CombatRootSnapshot Root) CreateMultiplayerPowerFixture(CombatState source)
    {
        OfflineJointCombat native = CreateOfflineJointCombat(source, actorCount: 2);
        foreach (Player player in native.Players)
        {
            player.Creature.SetCurrentHpInternal(100);
            player.Creature.SetMaxHpInternal(100);
        }
        native.Enemy.SetCurrentHpInternal(500);
        native.Enemy.SetMaxHpInternal(500);
        CombatRootSnapshot root = CombatRootSnapshot.Capture(native.State);
        CombatPredictionSimulator simulator = root.ForkSimulator();
        return (simulator, (SimulatedCombatState)simulator.State.CombatState,
            simulator.State.Players[0], simulator.State.Players[1], root);
    }

    private static void AssertEqual(int expected, int actual, string field)
    {
        if (actual != expected)
            throw new InvalidOperationException($"{field}: expected={expected}, actual={actual}.");
    }
}
