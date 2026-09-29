namespace CoopBot.Session;

public sealed class PeerLivenessTracker
{
    private readonly Dictionary<ulong, long> _lastObserved = [];
    private long _lastHeartbeatSent;

    public IReadOnlyCollection<ulong> Peers => _lastObserved.Keys;

    public void Configure(IEnumerable<ulong> peers, long nowMilliseconds)
    {
        _lastObserved.Clear();
        foreach (ulong peer in peers.Distinct())
            _lastObserved.Add(peer, nowMilliseconds);
        _lastHeartbeatSent = nowMilliseconds;
    }

    public void Observe(ulong peer, long nowMilliseconds)
    {
        if (!_lastObserved.ContainsKey(peer))
            throw new InvalidOperationException($"Unknown liveness peer {peer}.");
        _lastObserved[peer] = nowMilliseconds;
    }

    public bool HeartbeatDue(long nowMilliseconds, int intervalMilliseconds)
        => _lastObserved.Count > 0
           && nowMilliseconds - _lastHeartbeatSent >= intervalMilliseconds;

    public void MarkHeartbeatSent(long nowMilliseconds)
        => _lastHeartbeatSent = nowMilliseconds;

    public IReadOnlyList<ulong> Expired(long nowMilliseconds, int timeoutMilliseconds)
        => _lastObserved
            .Where(pair => nowMilliseconds - pair.Value >= timeoutMilliseconds)
            .Select(pair => pair.Key)
            .Order()
            .ToArray();

    public void Clear()
    {
        _lastObserved.Clear();
        _lastHeartbeatSent = 0;
    }
}
