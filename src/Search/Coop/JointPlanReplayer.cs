using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed record JointReplayResult(
    JointCombatSnapshot Snapshot,
    IReadOnlyList<PlanAction> AppliedActions);

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
        foreach (PlanAction action in plan.Actions)
        {
            while (action.Turn > turnState.Turn)
            {
                if (!turnState.IsBarrierReached)
                    throw new InvalidOperationException(
                        $"联合回放在回合 {turnState.Turn} 未完成全员屏障。");
                turnState = turnState.AdvanceTurn();
            }
            if (action.Turn < turnState.Turn)
                throw new InvalidOperationException($"联合回放回合倒退：{action.Turn} < {turnState.Turn}。");
            turnState = JointActionTransition.Apply(
                simulator, turnState, action, processedEnemyDeaths);
            applied.Add(action);
        }

        JointCombatSnapshot snapshot = JointCombatSnapshot.Capture(
            root,
            simulator,
            turnState);
        return new JointReplayResult(snapshot, applied.AsReadOnly());
    }

}
