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
}
