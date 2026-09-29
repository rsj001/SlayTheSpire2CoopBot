using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private static void AssertMultiplayerPowerScaling(CombatState source)
    {
        foreach (int actorCount in new[] { 2, 3, 4 })
        {
            AssertMultiplayerPowerScalingForTarget(source, actorCount, secondary: false);
            AssertMultiplayerPowerScalingForTarget(source, actorCount, secondary: true);
        }

        OfflineJointCombat native = CreateOfflineJointCombat(source, actorCount: 4);
        CombatRootSnapshot root = CombatRootSnapshot.Capture(native.State);
        CombatPredictionSimulator simulator = root.ForkSimulator();
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        Creature playerTarget = simulator.State.Players[1].Creature;
        combat.Apply<ArtifactPower>(playerTarget, 2, simulator.State.Players[0].Creature);
        if (combat.GetAmount<ArtifactPower>(playerTarget) != 2)
            throw new InvalidOperationException("玩家目标错误地经过了敌方 Power 多人缩放。");

        combat.Apply<ArtifactPower>(native.Enemy, 1, simulator.State.Players[0].Creature);
        int first = combat.GetAmount<ArtifactPower>(native.Enemy);
        combat.Apply<ArtifactPower>(native.Enemy, 1, simulator.State.Players[0].Creature);
        if (combat.GetAmount<ArtifactPower>(native.Enemy) != first + 1)
            throw new InvalidOperationException("已存在 Power 的叠加错误地再次经过多人缩放。");

        AssertScaledPower<HardenedShellPower>(combat, native.Enemy, simulator.State.Players[0].Creature, 0);
        AssertScaledPower<SlipperyPower>(combat, native.Enemy, simulator.State.Players[0].Creature, -1);
    }

    private static void AssertMultiplayerPowerScalingForTarget(
        CombatState source,
        int actorCount,
        bool secondary)
    {
        OfflineJointCombat native = CreateOfflineJointCombat(source, actorCount);
        if (secondary)
        {
            PowerModel minion = ModelDb.Power<MinionPower>().ToMutable();
            minion.ApplyInternal(native.Enemy, 1);
            if (!native.Enemy.IsSecondaryEnemy)
                throw new InvalidOperationException("多人缩放 fixture 未能建立次要敌人。 ");
        }

        CombatRootSnapshot root = CombatRootSnapshot.Capture(native.State);
        CombatPredictionSimulator simulator = root.ForkSimulator();
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        Creature applier = simulator.State.Players[0].Creature;
        AssertScaledPower<ArtifactPower>(combat, native.Enemy, applier, 1);
        AssertScaledPower<PlatingPower>(combat, native.Enemy, applier, 2);
        AssertScaledPower<SkittishPower>(combat, native.Enemy, applier, 3);
        AssertScaledPower<SlipperyPower>(combat, native.Enemy, applier, 2);
        AssertScaledPower<CurlUpPower>(combat, native.Enemy, applier, 2);
        AssertScaledPower<FlutterPower>(combat, native.Enemy, applier, 2);
    }

    private static void AssertScaledPower<T>(
        SimulatedCombatState combat,
        Creature target,
        Creature applier,
        int amount)
        where T : PowerModel
    {
        T canonical = ModelDb.Power<T>();
        int expected = amount == 0
            ? 0
            : (int)canonical.GetScaledAmountForMultiplayer(combat, applier, amount, target, null);
        combat.Apply<T>(target, amount, applier);
        int actual = combat.GetAmount<T>(target);
        if (actual != expected)
        {
            throw new InvalidOperationException(
                $"{typeof(T).Name} 多人缩放不一致：expected={expected}, actual={actual}, players={combat.Players.Count}, secondary={target.IsSecondaryEnemy}。");
        }
    }
}
