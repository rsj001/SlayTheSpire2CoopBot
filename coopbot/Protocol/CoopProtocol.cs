using System.Text.Json;

namespace CoopBot.Protocol;

public static class CoopProtocol
{
    public const int Version = 2;
    public const int MaximumPayloadBytes = 1_048_576;
    public const int HeartbeatIntervalMilliseconds = 2_000;
    public const int PeerTimeoutMilliseconds = 10_000;

    public static readonly IReadOnlyList<string> RequiredCapabilities =
    [
        "actor-assignment-v1",
        "single-action-commit-v1",
        "strict-checkpoint-v1",
        "client-consent-v1",
    ];

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
    };
}
public enum CoopMessageKind
{
    Hello = 1,
    SessionAccepted = 2,
    ActorAssignment = 3,
    RootPublished = 4,
    PlanPublished = 5,
    ActionPrepare = 6,
    ActionPrepared = 7,
    ActionRejected = 8,
    ActionCommit = 9,
    ActionAck = 10,
    ActionFailed = 11,
    PlanCancelled = 12,
    AutomationModeChanged = 13,
    Heartbeat = 14,
    ActionObserveCommit = 15,
    ActionObservePrepared = 16,
    ClientControlRequest = 17,
}

public readonly record struct CoopMessageHeader(
    int ProtocolVersion,
    string CombatSessionId,
    long MessageSequence,
    ulong SenderNetworkPlayerId,
    long RootRevision,
    string? PlanId = null,
    string? ActionId = null,
    string? SessionNonce = null);

public sealed record CoopWireEnvelope(
    CoopMessageKind Kind,
    CoopMessageHeader Header,
    string PayloadJson)
{
    public static CoopWireEnvelope Create<T>(
        CoopMessageKind kind,
        CoopMessageHeader header,
        T payload)
        => new(kind, header, JsonSerializer.Serialize(payload, CoopProtocol.JsonOptions));

    public T ReadPayload<T>()
        => JsonSerializer.Deserialize<T>(PayloadJson, CoopProtocol.JsonOptions)
           ?? throw new InvalidOperationException($"{Kind} payload deserialized to null.");
}

public sealed record HelloPayload(
    string ModVersion,
    ulong ClaimedHostNetworkPlayerId,
    IReadOnlyList<string> Capabilities);

public sealed record SessionAcceptedPayload(
    int ProtocolVersion,
    string ModVersion,
    ulong HostNetworkPlayerId,
    string SessionNonce,
    IReadOnlyList<string> Capabilities);

public sealed record ActorBinding(
    int ActorId,
    ulong NetworkPlayerId,
    string CharacterId,
    bool IsHost);

public sealed record ActorAssignmentPayload(IReadOnlyList<ActorBinding> Actors)
{
    public ActorBinding GetLocal(ulong localNetworkPlayerId)
        => Actors.Single(actor => actor.NetworkPlayerId == localNetworkPlayerId);
}

public sealed record RootPublishedPayload(
    long RootRevision,
    string RootFingerprint,
    IReadOnlyList<string> ActorFingerprints);

public sealed record HeartbeatPayload(
    long LastReceivedSequence,
    string SessionState);

public sealed record AutomationModeChangedPayload(CoopAutomationMode Mode);

public enum CoopClientControl
{
    PauseAutomation,
}

public sealed record ClientControlRequestPayload(CoopClientControl Control);

public enum CoopAutomationMode
{
    Observe,
    Suggest,
    ConfirmEach,
    Auto,
}

public enum CoopActionRejectionCode
{
    StaleRoot,
    NotLocalActor,
    MissingInstance,
    CostChanged,
    InvalidTarget,
    ChoiceChanged,
    NotPlayPhase,
    ActionInFlight,
    DuplicateAction,
    ProtocolMismatch,
    UserDeclined,
    UserPaused,
}
