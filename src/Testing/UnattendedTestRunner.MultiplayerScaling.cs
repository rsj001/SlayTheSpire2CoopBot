using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Singleton;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.InCombat.Extensions;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private static void AssertMultiplayerPowerScaling(CombatState source)
    {
        AssertMultiplayerHpRoots(source);
        AssertMultiplayerGenerationConstraints();
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

    private static void AssertMultiplayerHpRoots(CombatState source)
    {
        EncounterModel[] encounters = new[] { RoomType.Monster, RoomType.Elite, RoomType.Boss }
            .Select(roomType => ModelDb.All.OfType<EncounterModel>()
                .First(encounter => encounter.RoomType == roomType
                    && encounter.GetType().Namespace == "MegaCrit.Sts2.Core.Models.Encounters"))
            .ToArray();
        foreach (EncounterModel encounter in encounters)
        {
            foreach (int actorCount in new[] { 2, 3, 4 })
            {
                OfflineJointCombat native = CreateOfflineJointCombat(
                    source,
                    actorCount,
                    encounterModel: encounter);
                int baseHp = native.Enemy.MonsterMaxHpBeforeModification
                    ?? throw new InvalidOperationException("多人 HP 根缺少缩放前生命。 ");
                int expected = (int)Creature.ScaleHpForMultiplayer(
                    baseHp,
                    native.State.Encounter,
                    actorCount,
                    native.State.RunState.CurrentActIndex);
                if (native.Enemy.MaxHp != expected || native.Enemy.CurrentHp != expected)
                {
                    throw new InvalidOperationException(
                        $"{encounter.RoomType}/{actorCount} Actor 敌人根 HP 缩放错误：base={baseHp}, expected={expected}, max={native.Enemy.MaxHp}, current={native.Enemy.CurrentHp}。 ");
                }
            }
        }

        OfflineJointCombat spawnNative = CreateOfflineJointCombat(source, actorCount: 4);
        CombatPredictionSimulator simulator = CombatRootSnapshot.Capture(spawnNative.State).ForkSimulator();
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        MonsterModel monster = ModelDb.GetById<MonsterModel>(spawnNative.Enemy.Monster!.Id).ToMutable();
        Creature spawned = combat.CreatePredictedMonster(simulator, monster, CombatSide.Enemy, slot: null);
        int spawnedBase = spawned.MonsterMaxHpBeforeModification
            ?? throw new InvalidOperationException("预测召唤怪物缺少缩放前生命。 ");
        int spawnedExpected = (int)Creature.ScaleHpForMultiplayer(
            spawnedBase,
            combat.Encounter,
            combat.Players.Count,
            combat.CurrentActIndex);
        if (spawned.MaxHp != spawnedExpected || spawned.CurrentHp != spawnedExpected)
            throw new InvalidOperationException("预测召唤怪物没有走统一多人 HP 缩放。 ");
    }

    private static void AssertMultiplayerGenerationConstraints()
    {
        CardModel[] nativeCards = ModelDb.All
            .OfType<CardModel>()
            .Where(static card => card.GetType().Namespace == "MegaCrit.Sts2.Core.Models.Cards")
            .ToArray();
        CardModel[] multiplayerOnly = nativeCards
            .Where(static card => card.MultiplayerConstraint == CardMultiplayerConstraint.MultiplayerOnly)
            .ToArray();
        CardModel[] singleplayerOnly = nativeCards
            .Where(static card => card.MultiplayerConstraint == CardMultiplayerConstraint.SingleplayerOnly)
            .ToArray();
        HashSet<CardModel> multiplayerPool = new(
            nativeCards.FilterForCombatAndPlayerCount(CardMultiplayerConstraint.MultiplayerOnly),
            ReferenceEqualityComparer.Instance);
        HashSet<CardModel> singleplayerPool = new(
            nativeCards.FilterForCombatAndPlayerCount(CardMultiplayerConstraint.SingleplayerOnly),
            ReferenceEqualityComparer.Instance);
        if (multiplayerOnly.Length != 37
            || multiplayerOnly.Any(card => !multiplayerPool.Contains(card))
            || multiplayerOnly.Any(singleplayerPool.Contains)
            || singleplayerOnly.Any(multiplayerPool.Contains))
        {
            throw new InvalidOperationException(
                $"生成池多人约束错误：multiplayerOnly={multiplayerOnly.Length}, singleplayerOnly={singleplayerOnly.Length}。 ");
        }
        if (!multiplayerOnly.Any(static card => card.Type == CardType.Power)
            || !multiplayerOnly.Any(static card => card.Type == CardType.Attack)
            || !multiplayerOnly.Any(static card => card.Type == CardType.Skill))
        {
            throw new InvalidOperationException("多人生成池证据没有覆盖攻击、技能和能力牌。 ");
        }
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
            MonsterModel primaryModel = ModelDb.GetById<MonsterModel>(native.Enemy.Monster!.Id).ToMutable();
            Creature primary = native.State.CreateCreature(primaryModel, CombatSide.Enemy, slot: null);
            native.State.AddCreature(primary);
            primaryModel.SetUpForCombat();
            primaryModel.RollMove(native.Players.Select(static player => player.Creature));
            if (!native.Enemy.IsSecondaryEnemy)
                throw new InvalidOperationException("多人缩放 fixture 未能建立次要敌人。 ");
        }

        CombatRootSnapshot root = CombatRootSnapshot.Capture(native.State);
        CombatPredictionSimulator simulator = root.ForkSimulator();
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        Creature applier = simulator.State.Players[0].Creature;
        decimal block = simulator.GainBlock(native.Enemy, 10m, ValueProp.Move);
        decimal expectedBlockMultiplier = actorCount <= 2
            ? actorCount
            : actorCount * MultiplayerScalingModel.GetMultiplayerScaling(
                combat.Encounter,
                combat.CurrentActIndex);
        if (block != 10m * expectedBlockMultiplier)
            throw new InvalidOperationException(
                $"主/次敌人格挡没有按玩家人数缩放：players={actorCount}, secondary={secondary}, actual={block}, expected={10m * expectedBlockMultiplier}, primaryFlag={native.Enemy.IsPrimaryEnemy}, secondaryFlag={native.Enemy.IsSecondaryEnemy}。 ");
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
