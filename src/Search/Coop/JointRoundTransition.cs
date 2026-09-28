using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.InCombat.Mirrors;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal static class JointRoundTransition
{
    internal static void CompleteBasicEnemySide(
        CombatPredictionSimulator simulator,
        ForkableSet<uint> processedEnemyDeaths)
    {
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        Creature[] players = simulator.State.Players
            .Select(static player => player.Creature)
            .Where(creature => simulator.State.GetCreature(creature).IsAlive)
            .ToArray();
        Creature[] actingEnemies = combat.Enemies.ToArray();
        combat.CurrentSide = CombatSide.Enemy;
        foreach (Creature enemy in combat.Enemies)
            combat.BeginSideTurn(enemy);
        combat.SnapshotPowerAmountsAtTurnStart(combat.Enemies);
        if (!HookMirrors.BeforeSideTurnStart(simulator, CombatSide.Enemy, combat.Enemies))
            throw PendingEnemyChoice(combat);
        foreach (Creature enemy in combat.Enemies)
        {
            SimCreatureState state = simulator.State.GetCreature(enemy);
            if (state.Block > 0)
            {
                if (combat.ShouldClearBlock(enemy, out AbstractModel? preventer))
                    state.DamageBlock(state.Block, ValueProp.Move);
                else
                    PersistentRelicSupport.TriggerAfterPreventingBlockClear(simulator, preventer, enemy);
            }
            if (!CorePowerSupport.TriggerAfterBlockCleared(simulator, combat, enemy))
                throw PendingEnemyChoice(combat);
        }
        if (!combat.TriggerSideTurnStart(
                simulator,
                CombatSide.Enemy,
                combat.Enemies,
                decrementPlating: combat.RoundNumber > 1))
        {
            throw PendingEnemyChoice(combat);
        }
        if (!CorePowerSupport.TriggerPoison(simulator, combat, combat.Enemies.ToArray())
            || !CorePowerSupport.ApplyEnemyDeathPowers(
                simulator, combat, combat.KnownEnemies, processedEnemyDeaths))
        {
            throw PendingEnemyChoice(combat);
        }

        Dictionary<Creature, MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine.MoveState>
            performedMoves = new(actingEnemies.Length);
        foreach (Creature enemy in actingEnemies)
        {
            if (!combat.CanPerformMonsterMove(simulator, enemy))
                continue;
            ForecastMove move = combat.CurrentMonsterMove(enemy);
            MonsterMoveSemantics.ApplyBasicForecastMoveToPlayers(
                simulator,
                combat,
                move,
                players,
                processedEnemyDeaths);
            performedMoves[enemy] = move.Move;
            if (combat.HasPendingChoice)
                throw PendingEnemyChoice(combat);
            if (simulator.CheckWinCondition(combat.GetPlayerTurnNumber(simulator.State.Players[0])))
                return;
        }
        if (!CorePowerSupport.TriggerEnemySideTurnEndEffects(
                simulator,
                combat,
                combat.Enemies.ToArray()))
        {
            throw PendingEnemyChoice(combat);
        }
        if (!CorePowerSupport.ApplyEnemyDeathPowers(
                simulator, combat, combat.KnownEnemies, processedEnemyDeaths)
            || !CorePowerSupport.TriggerPoison(simulator, combat, players))
        {
            throw PendingEnemyChoice(combat);
        }
        foreach (Creature player in players)
            combat.ClearNoDraw(player);
        combat.PrepareMonsterMovesForNextRound(simulator, performedMoves);
    }

    internal static void CompletePlayerSide(
        CombatPredictionSimulator simulator,
        JointTurnState turns,
        ForkableSet<uint> processedEnemyDeaths)
    {
        if (!turns.IsBarrierReached)
            throw new InvalidOperationException("联合玩家侧尚未到达全员结束屏障。");
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        Player[] players = simulator.State.Players
            .Where(player => simulator.State.GetCreature(player.Creature).IsAlive)
            .ToArray();
        Creature[] participants = players.Select(static player => player.Creature).ToArray();
        combat.BeginActionChoices((IReadOnlyList<PlanCardChoice>?)null);
        try
        {
            combat.SetActionChoiceTiming(PlanChoiceTiming.PlayerTurnEnd);
            int etherealExhaustCount = 0;
            foreach (Player player in players)
            {
                etherealExhaustCount += combat.CountEtherealCardsInHand(simulator, player);
                if (!PlayerTurnEndLifecycle.RunPhaseOne(
                        simulator,
                        combat,
                        player,
                        participants))
                {
                    throw PendingChoice(combat, player, turns);
                }
                combat.CommitHistoryCourseTurn(player);
                combat.NormalizeAeonglassWithers(simulator);
                combat.NormalizeCardAfflictions(simulator);
                if (!CorePowerSupport.ApplyEnemyDeathPowers(
                        simulator,
                        combat,
                        combat.KnownEnemies,
                        processedEnemyDeaths))
                {
                    throw PendingChoice(combat, player, turns);
                }
                if (!simulator.IsInProgress)
                    return;
                CorePowerSupport.FlushPlayerHandAtTurnEnd(simulator, combat, player);
            }
            if (!PlayerTurnEndLifecycle.RunPhaseTwo(
                    simulator,
                    combat,
                    participants,
                    etherealExhaustCount))
            {
                throw PendingChoice(combat, players[0], turns);
            }
            if (!CorePowerSupport.ApplyEnemyDeathPowers(
                    simulator,
                    combat,
                    combat.KnownEnemies,
                    processedEnemyDeaths))
            {
                throw PendingChoice(combat, players[0], turns);
            }
        }
        finally
        {
            combat.EndActionChoices();
        }
    }

    private static JointPendingActionChoiceException PendingChoice(
        SimulatedCombatState combat,
        Player owner,
        JointTurnState turns)
    {
        TurnStartChoiceRequest request = combat.PendingTurnStartChoice
            ?? throw new InvalidOperationException("联合回合尾结算挂起但没有选择请求。");
        int actorIndex = -1;
        for (int index = 0; index < combat.Players.Count; index++)
        {
            if (ReferenceEquals(combat.Players[index], owner))
            {
                actorIndex = index;
                break;
            }
        }
        if (actorIndex < 0)
            throw new InvalidOperationException("联合回合尾选择 owner 不在 Actor 目录中。");
        CombatActorId actor = new(actorIndex);
        PlanAction source = new(PlanActionKind.EndTurn, turns.Turn, Actor: actor);
        return new JointPendingActionChoiceException(new(
            actor,
            source,
            request.SourceId,
            request.Spec ?? throw new InvalidOperationException("联合回合尾选择缺少 spec。")));
    }

    private static InvalidOperationException PendingEnemyChoice(SimulatedCombatState combat)
        => new($"联合敌方轮产生待处理选择：{combat.PendingTurnStartChoice?.SourceId ?? "unknown"}。");
}
