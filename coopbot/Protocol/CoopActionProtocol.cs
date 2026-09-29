namespace CoopBot.Protocol;

public sealed record ActionPreparePayload(
    CoopPlanActionSnapshot Action,
    string ExpectedRootFingerprint,
    int ExpectedTurn,
    string ExpectedPhase,
    int ExpectedEnergyCost,
    int ExpectedStarCost,
    bool IsIrreversible);

public sealed record ActionPreparedPayload(
    int ActorId,
    string ActionKind,
    string NativeInstanceIdentity);

public sealed record ActionRejectedPayload(
    CoopActionRejectionCode Code,
    string Detail);

public sealed record ActionCommitPayload(string PreparedActionId);

public sealed record ActionAckPayload(
    string NativeActionType,
    string CompletionState,
    string PostActionFingerprint);

public sealed record ActionFailedPayload(
    string FailureCode,
    string Detail);
