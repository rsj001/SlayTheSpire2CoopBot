using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal static class JointRoundTransition
{
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
}
