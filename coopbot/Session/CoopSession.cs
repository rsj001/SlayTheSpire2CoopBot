using CoopBot.Protocol;

namespace CoopBot.Session;

public enum CoopSessionState
{
    Negotiating,
    Active,
    Stopping,
    Stopped,
    Failed,
}

public sealed class CoopSession
{
    private readonly Dictionary<ulong, ActorBinding> _actorsByNetworkPlayerId = [];

    public CoopSession(
        string combatSessionId,
        ulong localNetworkPlayerId,
        ulong hostNetworkPlayerId)
    {
        CombatSessionId = combatSessionId;
        LocalNetworkPlayerId = localNetworkPlayerId;
        HostNetworkPlayerId = hostNetworkPlayerId;
        Inbound = new CoopInboundGate(combatSessionId, hostNetworkPlayerId);
    }

    public string CombatSessionId { get; }
    public ulong LocalNetworkPlayerId { get; }
    public ulong HostNetworkPlayerId { get; }
    public bool IsHost => LocalNetworkPlayerId == HostNetworkPlayerId;
    public CoopSessionState State { get; private set; } = CoopSessionState.Negotiating;
    public string? FailureCode { get; private set; }
    public string? FailureDetail { get; private set; }
    public CoopInboundGate Inbound { get; }
    public IReadOnlyCollection<ActorBinding> Actors => _actorsByNetworkPlayerId.Values;
    public ActorBinding LocalActor => _actorsByNetworkPlayerId[LocalNetworkPlayerId];

    public static ActorAssignmentPayload BuildActorAssignment(
        IReadOnlyList<(ulong NetworkPlayerId, string CharacterId)> stableRoster,
        ulong hostNetworkPlayerId)
    {
        if (stableRoster.Count is < 1 or > 4)
            throw new InvalidOperationException($"Expected 1..4 actors, got {stableRoster.Count}.");
        if (stableRoster.Select(player => player.NetworkPlayerId).Distinct().Count() != stableRoster.Count)
            throw new InvalidOperationException("Actor roster contains duplicate network player IDs.");
        if (!stableRoster.Any(player => player.NetworkPlayerId == hostNetworkPlayerId))
            throw new InvalidOperationException($"Host {hostNetworkPlayerId} is absent from actor roster.");

        ActorBinding[] actors = stableRoster
            .Select((player, actorId) => new ActorBinding(
                actorId,
                player.NetworkPlayerId,
                player.CharacterId,
                player.NetworkPlayerId == hostNetworkPlayerId))
            .ToArray();
        return new ActorAssignmentPayload(actors);
    }

    public void AcceptAssignment(ActorAssignmentPayload assignment)
    {
        RequireState(CoopSessionState.Negotiating);
        ValidateAssignment(assignment);
        foreach (ActorBinding actor in assignment.Actors)
            _actorsByNetworkPlayerId.Add(actor.NetworkPlayerId, actor);
        State = CoopSessionState.Active;
    }

    public void BeginStopping()
    {
        RequireState(CoopSessionState.Active);
        State = CoopSessionState.Stopping;
    }

    public void FinishStopping()
    {
        RequireState(CoopSessionState.Stopping);
        _actorsByNetworkPlayerId.Clear();
        State = CoopSessionState.Stopped;
    }

    public void Fail(string code, string detail)
    {
        if (State is CoopSessionState.Stopped or CoopSessionState.Failed)
            throw new InvalidOperationException($"Cannot fail session from state {State}.");
        FailureCode = string.IsNullOrWhiteSpace(code)
            ? throw new ArgumentException("Failure code is required.", nameof(code))
            : code;
        FailureDetail = detail;
        _actorsByNetworkPlayerId.Clear();
        State = CoopSessionState.Failed;
    }

    private void ValidateAssignment(ActorAssignmentPayload assignment)
    {
        if (assignment.Actors.Count is < 1 or > 4)
            throw new InvalidOperationException($"Expected 1..4 assigned actors, got {assignment.Actors.Count}.");
        if (assignment.Actors.Select(actor => actor.ActorId).Distinct().Count() != assignment.Actors.Count
            || !assignment.Actors.Select(actor => actor.ActorId).Order().SequenceEqual(
                Enumerable.Range(0, assignment.Actors.Count)))
        {
            throw new InvalidOperationException("Actor IDs must be unique and contiguous from zero.");
        }
        if (assignment.Actors.Select(actor => actor.NetworkPlayerId).Distinct().Count() != assignment.Actors.Count)
            throw new InvalidOperationException("Assignment contains duplicate network player IDs.");
        if (assignment.Actors.Count(actor => actor.IsHost) != 1
            || assignment.Actors.Single(actor => actor.IsHost).NetworkPlayerId != HostNetworkPlayerId)
        {
            throw new InvalidOperationException("Assignment host conflicts with negotiated host.");
        }
        if (!assignment.Actors.Any(actor => actor.NetworkPlayerId == LocalNetworkPlayerId))
            throw new InvalidOperationException($"Local player {LocalNetworkPlayerId} is absent from assignment.");
    }

    private void RequireState(CoopSessionState expected)
    {
        if (State != expected)
            throw new InvalidOperationException($"Expected session state {expected}, got {State}.");
    }
}
