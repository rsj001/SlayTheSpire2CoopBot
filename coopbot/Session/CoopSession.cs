using CoopBot.Protocol;

namespace CoopBot.Session;

public enum CoopSessionState
{
    Negotiating,
    Accepted,
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

    public string CombatSessionId { get; private set; }
    public ulong LocalNetworkPlayerId { get; }
    public ulong HostNetworkPlayerId { get; }
    public bool IsHost => LocalNetworkPlayerId == HostNetworkPlayerId;
    public CoopSessionState State { get; private set; } = CoopSessionState.Negotiating;
    public string? FailureCode { get; private set; }
    public string? FailureDetail { get; private set; }
    public string? SessionNonce { get; private set; }
    public IReadOnlyList<string> Capabilities { get; private set; } = Array.Empty<string>();
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

    public void AcceptAsHost(string sessionNonce, IReadOnlyList<string> capabilities)
    {
        RequireState(CoopSessionState.Negotiating);
        AcceptNegotiation(sessionNonce, capabilities);
    }

    public void AcceptSession(SessionAcceptedPayload accepted)
    {
        RequireState(CoopSessionState.Negotiating);
        if (accepted.ProtocolVersion != CoopProtocol.Version)
            throw new InvalidOperationException(
                $"Session protocol {accepted.ProtocolVersion} != local {CoopProtocol.Version}.");
        if (accepted.HostNetworkPlayerId != HostNetworkPlayerId)
            throw new InvalidOperationException(
                $"Accepted host {accepted.HostNetworkPlayerId} != expected {HostNetworkPlayerId}.");
        AcceptNegotiation(accepted.SessionNonce, accepted.Capabilities);
    }

    public void AdoptHostCombatSessionId(string combatSessionId)
    {
        RequireState(CoopSessionState.Negotiating);
        if (IsHost)
            throw new InvalidOperationException("Host cannot adopt a Client combat session ID.");
        CombatSessionId = string.IsNullOrWhiteSpace(combatSessionId)
            ? throw new ArgumentException("Combat session ID is required.", nameof(combatSessionId))
            : combatSessionId;
        Inbound.BindCombatSessionId(combatSessionId);
    }

    public void AcceptAssignment(ActorAssignmentPayload assignment)
    {
        RequireState(CoopSessionState.Accepted);
        ValidateAssignment(assignment);
        foreach (ActorBinding actor in assignment.Actors)
            _actorsByNetworkPlayerId.Add(actor.NetworkPlayerId, actor);
        State = CoopSessionState.Active;
    }

    public void BeginStopping()
    {
        if (State is not (CoopSessionState.Negotiating or CoopSessionState.Accepted
            or CoopSessionState.Active or CoopSessionState.Failed))
        {
            throw new InvalidOperationException($"Cannot stop session from state {State}.");
        }
        State = CoopSessionState.Stopping;
    }

    public void FinishStopping()
    {
        RequireState(CoopSessionState.Stopping);
        _actorsByNetworkPlayerId.Clear();
        SessionNonce = null;
        Capabilities = Array.Empty<string>();
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

    private void AcceptNegotiation(string sessionNonce, IReadOnlyList<string> capabilities)
    {
        if (string.IsNullOrWhiteSpace(sessionNonce))
            throw new InvalidOperationException("Accepted session has no nonce.");
        string[] normalized = capabilities
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] missing = CoopProtocol.RequiredCapabilities
            .Except(normalized, StringComparer.Ordinal)
            .ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException("Session lacks capabilities: " + string.Join(',', missing));
        SessionNonce = sessionNonce;
        Capabilities = Array.AsReadOnly(normalized);
        Inbound.BindSessionNonce(sessionNonce);
        State = CoopSessionState.Accepted;
    }

    private void RequireState(CoopSessionState expected)
    {
        if (State != expected)
            throw new InvalidOperationException($"Expected session state {expected}, got {State}.");
    }
}
