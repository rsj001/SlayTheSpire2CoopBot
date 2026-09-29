using CoopBot.Protocol;

namespace CoopBot.Diagnostics;

internal sealed class CoopLoopbackHub(ulong hostNetworkPlayerId)
{
    private readonly Dictionary<ulong, CoopLoopbackTransport> _peers = [];

    internal CoopLoopbackTransport Add(ulong networkPlayerId)
    {
        CoopLoopbackTransport transport = new(this, networkPlayerId, networkPlayerId == hostNetworkPlayerId);
        if (!_peers.TryAdd(networkPlayerId, transport))
            throw new InvalidOperationException($"Duplicate loopback peer {networkPlayerId}.");
        return transport;
    }

    internal void Send(ulong sender, ulong recipient, CoopWireEnvelope envelope)
    {
        if (!_peers.TryGetValue(recipient, out CoopLoopbackTransport? target))
            throw new InvalidOperationException($"Loopback recipient {recipient} is unavailable.");
        target.Deliver(envelope, sender);
    }

    internal void Broadcast(ulong sender, CoopWireEnvelope envelope)
    {
        foreach ((ulong recipient, CoopLoopbackTransport target) in _peers.OrderBy(pair => pair.Key))
        {
            if (recipient != sender)
                target.Deliver(envelope, sender);
        }
    }

    internal void Disconnect(ulong networkPlayerId)
    {
        if (!_peers.TryGetValue(networkPlayerId, out CoopLoopbackTransport? transport))
            throw new InvalidOperationException($"Loopback peer {networkPlayerId} is unavailable.");
        transport.RaiseDisconnected();
    }

    internal void Remove(ulong networkPlayerId)
        => _peers.Remove(networkPlayerId);

    internal ulong HostNetworkPlayerId => hostNetworkPlayerId;
}

internal sealed class CoopLoopbackTransport : ICoopTransport
{
    private readonly CoopLoopbackHub _hub;
    private bool _disposed;

    internal CoopLoopbackTransport(CoopLoopbackHub hub, ulong localNetworkPlayerId, bool isHost)
    {
        _hub = hub;
        LocalNetworkPlayerId = localNetworkPlayerId;
        IsHost = isHost;
    }

    public ulong LocalNetworkPlayerId { get; }
    public bool IsHost { get; }
    public event Action<CoopWireEnvelope, ulong>? Received;
    public event Action? Disconnected;

    public void SendToHost(CoopWireEnvelope envelope)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsHost)
            throw new InvalidOperationException("Loopback Host cannot send to itself as a Client.");
        _hub.Send(LocalNetworkPlayerId, _hub.HostNetworkPlayerId, envelope);
    }

    public void SendToClient(ulong networkPlayerId, CoopWireEnvelope envelope)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsHost)
            throw new InvalidOperationException("Only loopback Host may target a Client.");
        _hub.Send(LocalNetworkPlayerId, networkPlayerId, envelope);
    }

    public void Broadcast(CoopWireEnvelope envelope)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsHost)
            throw new InvalidOperationException("Only loopback Host may broadcast.");
        _hub.Broadcast(LocalNetworkPlayerId, envelope);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _hub.Remove(LocalNetworkPlayerId);
        Received = null;
        Disconnected = null;
    }

    internal void Deliver(CoopWireEnvelope envelope, ulong sender)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Received?.Invoke(envelope, sender);
    }

    internal void RaiseDisconnected()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Disconnected?.Invoke();
    }
}
