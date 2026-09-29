using MegaCrit.Sts2.Core.Commands.Builders;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal static class MonsterMoveSemantics
{
    internal static void ApplyBasicForecastMoveToPlayers(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        IReadOnlyList<Creature> players,
        ISet<uint> processedEnemyDeaths)
    {
        MonsterModel monster = move.Owner.Monster
            ?? throw new InvalidOperationException("预测行动所有者不是怪物。");
        if (IsJointOwnerOnlyMove(monster, move.Move.Id))
        {
            Creature? representative = players.FirstOrDefault(player =>
                simulator.State.GetCreature(player).IsAlive);
            if (representative != null)
                _ = ApplyForecastMove(
                    simulator,
                    combat,
                    move,
                    representative,
                    processedEnemyDeaths);
            return;
        }
        if (move.AttackHits.Count == 0 || MonsterMoveEffects.Supports(monster, move.Move.Id))
        {
            throw new PredictionUnsupportedException(
                $"联合敌方轮尚未登记 move={move.Owner.Monster?.Id.Entry}/{move.Move.Id} 的多人后效。");
        }
        foreach (Creature player in players)
        {
            if (simulator.State.GetCreature(player).IsAlive)
                _ = ApplyForecastMove(simulator, combat, move, player, processedEnemyDeaths);
            if (simulator.HasPendingChoice || simulator.State.GetCreature(move.Owner).IsDead)
                return;
        }
    }

    private static bool IsJointOwnerOnlyMove(MonsterModel monster, string moveId)
        => (monster.GetType().Name, moveId) is ("FuzzyWurmCrawler", "INHALE");

    public static bool ApplyForecastMove(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        ISet<uint> processedEnemyDeaths,
        IReadOnlyList<PlanCardChoice>? plannedChoices = null)
    {
        SimCreatureState simulatedPlayer = simulator.State.GetCreature(player);
        MonsterMoveEffects.ApplyBeforeAttack(simulator, combat, move, player);
        if (simulator.HasPendingChoice)
            return simulatedPlayer.IsDead;
        bool fullyBlockedAttack = false;
        bool playerDied = false;
        AttackCommand? attackContext = move.AttackHits.Count > 0
            ? simulator.BeginAttackContext(
                new AttackCommand(0m)
                    .FromMonster(move.Owner.Monster
                        ?? throw new InvalidOperationException("预测攻击的所有者不是怪物。"))
                    .WithHitCount(0))
            : null;
        bool attackCompleted = attackContext == null;
        try
        {
            if (simulator.HasPendingChoice)
                return simulatedPlayer.IsDead;

            foreach (ForecastAttackHit hit in move.AttackHits)
            {
                int baseDamage = combat.AdjustMonsterMoveDamage(move.Owner, move.Move.Id, hit.BaseDamage);
                IReadOnlyList<DamageResult> results = DamagePlayer(
                    simulator,
                    combat,
                    move.Owner,
                    player,
                    baseDamage);
                if (simulator.HasPendingChoice)
                    return simulatedPlayer.IsDead;
                simulator.AddAttackContextHit(attackContext!, results);
                foreach (DamageResult result in results)
                {
                    if (ReferenceEquals(result.Receiver, player) && result.WasFullyBlocked)
                        fullyBlockedAttack = true;
                }
                CorePowerSupport.ApplyEnemyDeathPowers(
                    simulator,
                    combat,
                    combat.KnownEnemies,
                    processedEnemyDeaths);
                if (simulator.HasPendingChoice)
                    return simulatedPlayer.IsDead;
                if (simulatedPlayer.IsDead)
                {
                    playerDied = true;
                    break;
                }
                if (simulator.State.GetCreature(move.Owner).IsDead)
                    break;
            }

            attackCompleted = true;
        }
        finally
        {
            if (attackContext != null)
                simulator.EndAttackContext(attackContext, attackCompleted);
        }

        if (simulator.HasPendingChoice)
            return simulatedPlayer.IsDead;
        if (playerDied)
            return true;
        if (fullyBlockedAttack && combat.GetAmount<ImbalancedPower>(move.Owner) > 0)
        {
            if (move.Owner.Monster is BowlbugRock)
                combat.ForceStunnedMove(move.Owner, "HEADBUTT_MOVE");
            combat.StunNextMove(move.Owner);
        }
        MonsterMoveEffects.Apply(
            simulator,
            combat,
            move,
            player,
            out bool killedOwner,
            plannedChoices);
        if (simulator.HasPendingChoice)
            return simulatedPlayer.IsDead;
        if (killedOwner
            && move.Owner.CombatId is uint moveOwnerCombatId
            && !processedEnemyDeaths.Contains(moveOwnerCombatId))
        {
            CorePowerSupport.ApplyEnemyDeathPowers(
                simulator,
                combat,
                combat.KnownEnemies,
                processedEnemyDeaths);
            if (simulator.HasPendingChoice)
                return simulatedPlayer.IsDead;
        }
        simulator.SynchronizePowerAmountPredictionStates();
        PowerLifecycleSupport.ResolvePowerAmountChanges(simulator, combat);
        combat.NormalizeAeonglassWithers(simulator);
        combat.NormalizeCardAfflictions(simulator);
        return simulatedPlayer.IsDead;
    }

    public static IReadOnlyList<DamageResult> DamagePlayer(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        Creature attacker,
        Creature player,
        int baseDamage)
    {
        Creature? osty = player.Player is { } owner ? simulator.State.GetOsty(owner) : null;
        int? suppressedDieForYou = null;
        if (osty != null
            && simulator.State.GetCreature(osty).IsDead
            && combat.GetAmount<DieForYouPower>(osty) is > 0 and var amount)
        {
            suppressedDieForYou = amount;
            combat.SetAmount<DieForYouPower>(osty, 0);
        }

        try
        {
            using (simulator.PushDamageSource(
                CombatDamageSource.For(CombatDamageSourceKind.MonsterMove, attacker.Monster?.Id.Entry)))
            {
                return simulator.Damage(player, baseDamage, ValueProp.Move, attacker);
            }
        }
        finally
        {
            if (suppressedDieForYou is { } restoredAmount)
                combat.SetAmount<DieForYouPower>(osty!, restoredAmount);
        }
    }
}
