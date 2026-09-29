namespace CombatSolver;

internal static class JointStrictReplayVerifier
{
    internal static JointReplayResult Verify(
        CombatRootSnapshot root,
        JointOfflineSearchResult searched)
    {
        JointReplayResult replay = JointPlanReplayer.Replay(
            root,
            new JointPlan(root.Actors.Count, searched.Actions));
        string? difference = DescribeFirstDifference(searched.Snapshot, replay.Snapshot);
        if (difference != null)
        {
            int actionIndex = Math.Max(0, replay.AppliedActions.Count - 1);
            PlanAction? action = replay.AppliedActions.ElementAtOrDefault(actionIndex);
            throw new InvalidOperationException(
                $"联合 strict replay 在 action[{actionIndex}]={action} 首次不同：{difference}");
        }
        return replay;
    }

    internal static string? DescribeFirstDifference(
        JointCombatSnapshot expected,
        JointCombatSnapshot actual)
    {
        if (expected.Turn != actual.Turn)
            return $"turn expected={expected.Turn} actual={actual.Turn}";
        if (expected.TurnState.Turn != actual.TurnState.Turn
            || expected.TurnState.ActorCount != actual.TurnState.ActorCount
            || !expected.TurnState.Phases.SequenceEqual(actual.TurnState.Phases))
            return $"turnState expected={expected.TurnState} actual={actual.TurnState}";
        if (expected.Actors.Count != actual.Actors.Count)
            return $"actorCount expected={expected.Actors.Count} actual={actual.Actors.Count}";
        for (int index = 0; index < expected.Actors.Count; index++)
        {
            JointActorSnapshot left = expected.Actors[index];
            JointActorSnapshot right = actual.Actors[index];
            if (left != right)
                return $"actor[{index}] expected={left} actual={right}";
        }
        if (!string.Equals(
                expected.Continuation.StateText,
                actual.Continuation.StateText,
                StringComparison.Ordinal))
        {
            return "continuation."
                + expected.Continuation.DescribeFirstDifference(actual.Continuation);
        }
        if (expected.StateKey != actual.StateKey)
            return $"stateKey expected={expected.StateKey} actual={actual.StateKey}";
        return null;
    }
}
