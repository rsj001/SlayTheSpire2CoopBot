namespace CoopBot.Session;

public enum CoopActionLedgerState
{
    Prepared,
    Committed,
    Completed,
    Failed,
}

public sealed class ActionIdempotencyLedger
{
    private readonly Dictionary<string, CoopActionLedgerState> _states = new(StringComparer.Ordinal);

    public bool TryPrepare(string actionId)
    {
        ValidateActionId(actionId);
        return _states.TryAdd(actionId, CoopActionLedgerState.Prepared);
    }

    public bool TryCommit(string actionId)
        => TryTransition(actionId, CoopActionLedgerState.Prepared, CoopActionLedgerState.Committed);

    public bool TryComplete(string actionId)
        => TryTransition(actionId, CoopActionLedgerState.Committed, CoopActionLedgerState.Completed);

    public bool TryFail(string actionId)
    {
        ValidateActionId(actionId);
        if (!_states.TryGetValue(actionId, out CoopActionLedgerState current)
            || current is CoopActionLedgerState.Completed or CoopActionLedgerState.Failed)
        {
            return false;
        }
        _states[actionId] = CoopActionLedgerState.Failed;
        return true;
    }

    public CoopActionLedgerState? GetState(string actionId)
        => _states.GetValueOrDefault(actionId);

    public void Clear() => _states.Clear();

    private bool TryTransition(
        string actionId,
        CoopActionLedgerState expected,
        CoopActionLedgerState next)
    {
        ValidateActionId(actionId);
        if (!_states.TryGetValue(actionId, out CoopActionLedgerState current) || current != expected)
            return false;
        _states[actionId] = next;
        return true;
    }

    private static void ValidateActionId(string actionId)
    {
        if (string.IsNullOrWhiteSpace(actionId))
            throw new ArgumentException("Action ID is required.", nameof(actionId));
    }
}
