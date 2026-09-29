namespace CombatSolver;

internal sealed record JointStrictCheckpoint(
    int AppliedActionCount,
    string Stage,
    int Turn,
    IReadOnlyList<JointActorTurnPhase> Phases,
    ContinuationStamp Continuation,
    StateFingerprint StateKey)
{
    internal static JointStrictCheckpoint Capture(
        JointCombatSnapshot snapshot,
        int appliedActionCount,
        string stage)
        => new(
            appliedActionCount,
            stage,
            snapshot.Turn,
            Array.AsReadOnly(snapshot.TurnState.Phases.ToArray()),
            snapshot.Continuation,
            snapshot.StateKey);
}
