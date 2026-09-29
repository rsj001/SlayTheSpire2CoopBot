namespace CoopBot.Session;

public sealed class HostObserverBarrier
{
    private readonly HashSet<ulong> _expected = [];
    private readonly HashSet<ulong> _pending = [];

    public string? ActionId { get; private set; }
    public IReadOnlyCollection<ulong> Pending => _pending;
    public bool IsReady => ActionId is not null && _pending.Count == 0;

    public bool Begin(string actionId, IEnumerable<ulong> observers)
    {
        if (ActionId is not null)
            throw new InvalidOperationException($"Observer barrier {ActionId} is already active.");
        ActionId = string.IsNullOrWhiteSpace(actionId)
            ? throw new ArgumentException("ActionId is required.", nameof(actionId))
            : actionId;
        foreach (ulong observer in observers)
        {
            if (!_expected.Add(observer))
                throw new InvalidOperationException($"Duplicate observer {observer} for {actionId}.");
            _pending.Add(observer);
        }
        return IsReady;
    }

    public bool Acknowledge(string actionId, ulong observer)
    {
        if (!string.Equals(ActionId, actionId, StringComparison.Ordinal))
            throw new InvalidOperationException($"Observer ACK {actionId} does not match barrier {ActionId ?? "-"}.");
        if (!_expected.Contains(observer))
            throw new InvalidOperationException($"Network player {observer} is not pending for {actionId}.");
        if (!_pending.Remove(observer))
            return IsReady;
        return IsReady;
    }

    public void Clear(string actionId)
    {
        if (ActionId is null)
            return;
        if (!string.Equals(ActionId, actionId, StringComparison.Ordinal))
            throw new InvalidOperationException($"Cannot clear observer barrier {ActionId} with {actionId}.");
        ActionId = null;
        _expected.Clear();
        _pending.Clear();
    }
}
