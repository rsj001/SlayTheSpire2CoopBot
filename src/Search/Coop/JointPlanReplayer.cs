using CombatSolver.Engine.InCombat.Simulation;
using CombatSolver.Engine.Common;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CombatSolver;

internal sealed record JointReplayResult(
    JointCombatSnapshot Snapshot,
    IReadOnlyList<PlanAction> AppliedActions,
    IReadOnlyList<JointActionExpectation> ActionExpectations,
    IReadOnlyList<JointCombatSnapshot> ActionSnapshots,
    IReadOnlyList<JointStrictCheckpoint> Checkpoints);

internal sealed record JointActionExpectation(
    CombatActorId Actor,
    int Turn,
    JointActorTurnPhase Phase,
    int EnergyCost,
    int StarCost);

internal static class JointPlanReplayer
{
    internal static JointReplayResult Replay(CombatRootSnapshot root, JointPlan plan)
    {
        plan.Validate();
        CombatPredictionSimulator simulator = root.ForkSimulator();
        ForkableSet<uint> processedEnemyDeaths =
            JointActionTransition.CaptureProcessedEnemyDeaths(root, simulator);
        JointTurnState turnState = JointTurnState.FromRoot(root);
        List<PlanAction> applied = [];
        List<JointActionExpectation> expectations = [];
        List<JointCombatSnapshot> actionSnapshots = [];
        List<JointStrictCheckpoint> checkpoints = [];
        foreach (PlanAction action in plan.Actions)
        {
            while (action.Turn > turnState.Turn)
            {
                if (!turnState.IsBarrierReached)
                    throw new InvalidOperationException(
                        $"联合回放在回合 {turnState.Turn} 未完成全员屏障。");
                PlanAction carrier = applied.LastOrDefault(prior =>
                        prior.Turn == turnState.Turn && prior.Kind == PlanActionKind.EndTurn)
                    ?? throw new InvalidOperationException(
                        $"联合回放在回合 {turnState.Turn} 没有 EndTurn 承载过渡选择。");
                IReadOnlyList<PlanCardChoice> transitionChoices = carrier.TurnStartChoices ?? [];
                IReadOnlyList<CombatActorId> extraTurnActors = JointRoundTransition.CompletePlayerSide(
                    simulator,
                    turnState,
                    processedEnemyDeaths,
                    transitionChoices.Where(static choice =>
                        choice.Timing == PlanChoiceTiming.PlayerTurnEnd).ToArray());
                if (!simulator.TerminalStamp.HasValue && extraTurnActors.Count == 0)
                    JointRoundTransition.CompleteBasicEnemySide(simulator, processedEnemyDeaths);
                if (!simulator.TerminalStamp.HasValue)
                {
                    turnState = JointRoundTransition.StartBasicPlayerSide(
                        simulator,
                        turnState,
                        processedEnemyDeaths,
                        transitionChoices.Where(static choice =>
                            choice.Timing != PlanChoiceTiming.PlayerTurnEnd).ToArray(),
                        extraTurnActors);
                }
                JointCombatSnapshot barrierSnapshot = JointCombatSnapshot.Capture(
                    root,
                    simulator,
                    turnState);
                checkpoints.Add(JointStrictCheckpoint.Capture(
                    barrierSnapshot,
                    applied.Count,
                    "barrier"));
            }
            if (action.Turn < turnState.Turn)
                throw new InvalidOperationException($"联合回放回合倒退：{action.Turn} < {turnState.Turn}。");
            Player expectationPlayer = simulator.State.Players[action.Actor.Index];
            SimPlayerCombatState expectationPlayerState =
                simulator.State.GetPlayerCombatState(expectationPlayer);
            int energyCost = 0;
            int starCost = 0;
            if (action.Kind == PlanActionKind.PlayCard)
            {
                PredictedCard card = CombatBeamSolver.FindCardForReplay(
                        expectationPlayerState.Hand.Cards,
                        action)
                    ?? throw new InvalidOperationException(
                        $"联合回放无法为动作期待值找到 Actor {action.Actor} 的卡牌 {action.CardId}。");
                energyCost = card.GetEnergyCostWithModifiers(simulator, expectationPlayerState);
                starCost = card.GetStarCostWithModifiers(simulator, expectationPlayerState);
            }
            expectations.Add(new JointActionExpectation(
                action.Actor,
                action.Turn,
                turnState.Phases[action.Actor.Index],
                energyCost,
                starCost));
            turnState = JointActionTransition.Apply(
                simulator, turnState, action, processedEnemyDeaths);
            applied.Add(action);
            JointCombatSnapshot actionSnapshot = JointCombatSnapshot.Capture(root, simulator, turnState);
            actionSnapshots.Add(actionSnapshot);
            checkpoints.Add(JointStrictCheckpoint.Capture(
                actionSnapshot,
                applied.Count,
                "action"));
        }

        JointCombatSnapshot snapshot = JointCombatSnapshot.Capture(
            root,
            simulator,
            turnState);
        return new JointReplayResult(
            snapshot,
            applied.AsReadOnly(),
            expectations.AsReadOnly(),
            actionSnapshots.AsReadOnly(),
            checkpoints.AsReadOnly());
    }

}
