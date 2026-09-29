using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed record JointReplayResult(
    JointCombatSnapshot Snapshot,
    IReadOnlyList<PlanAction> AppliedActions,
    IReadOnlyList<JointCombatSnapshot> ActionSnapshots,
    IReadOnlyList<JointStrictCheckpoint> Checkpoints);

internal static class JointPlanReplayer
{
    internal static JointReplayResult Replay(CombatRootSnapshot root, JointPlan plan)
    {
        plan.Validate();
        CombatPredictionSimulator simulator = root.ForkSimulator();
        ForkableSet<uint> processedEnemyDeaths =
            JointActionTransition.CaptureProcessedEnemyDeaths(root, simulator);
        JointTurnState turnState = JointTurnState.Start(root.Actors.Count, root.StartTurnNumber);
        List<PlanAction> applied = [];
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
            actionSnapshots.AsReadOnly(),
            checkpoints.AsReadOnly());
    }

}
