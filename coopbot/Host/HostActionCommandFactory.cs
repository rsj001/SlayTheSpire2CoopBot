using CombatSolver;
using CoopBot.Protocol;

namespace CoopBot.Host;

internal static class HostActionCommandFactory
{
    internal static ActionPreparePayload Create(HostSearchResult result, int actionIndex)
    {
        PlanAction action = result.Search.Actions[actionIndex];
        JointActionExpectation expectation = result.Replay.ActionExpectations[actionIndex];
        if (expectation.Actor != action.Actor || expectation.Turn != action.Turn)
            throw new InvalidOperationException("Replay action expectation is not aligned with the published route.");
        return new ActionPreparePayload(
            CoopPlanSnapshotFactory.CaptureAction(action, actionIndex),
            result.RecordedRoot.ActorFingerprints[action.Actor.Index],
            expectation.Turn,
            expectation.Phase.ToString(),
            expectation.EnergyCost,
            expectation.StarCost,
            IsIrreversible: true);
    }

    internal static ContinuationStamp ExpectedStableContinuation(
        HostSearchResult result,
        int actionIndex)
        => ExpectedStableContinuation(result.Replay, actionIndex);

    internal static ContinuationStamp ExpectedStableContinuation(
        JointReplayResult replay,
        int actionIndex)
    {
        JointCombatSnapshot action = replay.ActionSnapshots[actionIndex];
        if (!action.TurnState.IsBarrierReached)
            return action.Continuation;
        int appliedActionCount = actionIndex + 1;
        JointStrictCheckpoint[] barriers = replay.Checkpoints
            .Where(checkpoint => checkpoint.Stage == "barrier"
                && checkpoint.AppliedActionCount == appliedActionCount)
            .ToArray();
        if (barriers.Length != 1)
        {
            throw new InvalidOperationException(
                $"Action {actionIndex} reaches the readiness barrier but has {barriers.Length} stable checkpoints.");
        }
        return barriers[0].Continuation;
    }
}
