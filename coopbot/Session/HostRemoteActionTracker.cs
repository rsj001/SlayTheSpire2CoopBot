namespace CoopBot.Session;

public enum RemoteActionState
{
    Preparing,
    Prepared,
    Committed,
    Completed,
    Rejected,
    Failed,
    TimedOut,
}

public sealed record RemoteActionStatus(
    string ActionId,
    int ActorId,
    ulong OwnerNetworkPlayerId,
    RemoteActionState State,
    string Detail);

public sealed class HostRemoteActionTracker
{
    private RemoteActionStatus? _current;

    public RemoteActionStatus? Current => _current;

    public RemoteActionStatus Begin(
        string actionId,
        int actorId,
        ulong ownerNetworkPlayerId)
    {
        if (_current is { State: not (RemoteActionState.Completed or RemoteActionState.Rejected
            or RemoteActionState.Failed or RemoteActionState.TimedOut) })
        {
            throw new InvalidOperationException($"Remote action {_current.ActionId} is still active.");
        }
        _current = new RemoteActionStatus(
            actionId,
            actorId,
            ownerNetworkPlayerId,
            RemoteActionState.Preparing,
            string.Empty);
        return _current;
    }

    public RemoteActionStatus Prepared(string actionId, ulong senderNetworkPlayerId)
        => Transition(actionId, senderNetworkPlayerId, RemoteActionState.Preparing,
            RemoteActionState.Prepared, "prepared");

    public RemoteActionStatus Commit(string actionId)
    {
        RequireCurrent(actionId);
        if (_current!.State != RemoteActionState.Prepared)
            throw new InvalidOperationException($"Cannot commit {_current.ActionId} from {_current.State}.");
        return _current = _current with { State = RemoteActionState.Committed, Detail = "committed" };
    }

    public RemoteActionStatus Acknowledge(string actionId, ulong senderNetworkPlayerId)
        => Transition(actionId, senderNetworkPlayerId, RemoteActionState.Committed,
            RemoteActionState.Completed, "acknowledged");

    public RemoteActionStatus Reject(string actionId, ulong senderNetworkPlayerId, string detail)
        => Terminal(actionId, senderNetworkPlayerId, RemoteActionState.Rejected, detail);

    public RemoteActionStatus Fail(string actionId, ulong senderNetworkPlayerId, string detail)
        => Terminal(actionId, senderNetworkPlayerId, RemoteActionState.Failed, detail);

    public RemoteActionStatus Timeout(string actionId, string detail)
    {
        RequireCurrent(actionId);
        if (_current!.State is RemoteActionState.Completed or RemoteActionState.Rejected
            or RemoteActionState.Failed or RemoteActionState.TimedOut)
        {
            throw new InvalidOperationException($"Cannot timeout terminal action {_current.ActionId}.");
        }
        return _current = _current with { State = RemoteActionState.TimedOut, Detail = detail };
    }

    private RemoteActionStatus Transition(
        string actionId,
        ulong senderNetworkPlayerId,
        RemoteActionState expected,
        RemoteActionState next,
        string detail)
    {
        RequireOwner(actionId, senderNetworkPlayerId);
        if (_current!.State != expected)
            throw new InvalidOperationException(
                $"Cannot move {_current.ActionId} from {_current.State}; expected {expected}.");
        return _current = _current with { State = next, Detail = detail };
    }

    private RemoteActionStatus Terminal(
        string actionId,
        ulong senderNetworkPlayerId,
        RemoteActionState terminal,
        string detail)
    {
        RequireOwner(actionId, senderNetworkPlayerId);
        if (_current!.State is RemoteActionState.Completed or RemoteActionState.Rejected
            or RemoteActionState.Failed or RemoteActionState.TimedOut)
        {
            throw new InvalidOperationException($"Action {_current.ActionId} is already terminal.");
        }
        return _current = _current with { State = terminal, Detail = detail };
    }

    private void RequireOwner(string actionId, ulong senderNetworkPlayerId)
    {
        RequireCurrent(actionId);
        if (_current!.OwnerNetworkPlayerId != senderNetworkPlayerId)
            throw new InvalidOperationException(
                $"Action {_current.ActionId} owner {_current.OwnerNetworkPlayerId} != sender {senderNetworkPlayerId}.");
    }

    private void RequireCurrent(string actionId)
    {
        if (_current is null || !string.Equals(_current.ActionId, actionId, StringComparison.Ordinal))
            throw new InvalidOperationException($"Unknown remote action {actionId}.");
    }
}
