using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Entities.Multiplayer;

namespace CoopBot.Protocol;

internal sealed class NativeCoopTransport : ICoopTransport
{
    private readonly INetGameService _service;
    private bool _disposed;

    internal NativeCoopTransport(INetGameService service)
    {
        _service = service;
        _service.RegisterMessageHandler<CoopBotEnvelopeMessage>(OnMessage);
        _service.Disconnected += OnDisconnected;
    }

    public ulong LocalNetworkPlayerId => _service.NetId;
    public bool IsHost => _service.Type == NetGameType.Host;
    public event Action<CoopWireEnvelope, ulong>? Received;
    public event Action? Disconnected;

    public void SendToHost(CoopWireEnvelope envelope)
    {
        if (IsHost)
            throw new InvalidOperationException("Host cannot send a CoopBot message to itself through native transport.");
        _service.SendMessage(new CoopBotEnvelopeMessage(envelope));
    }

    public void SendToClient(ulong networkPlayerId, CoopWireEnvelope envelope)
    {
        if (!IsHost)
            throw new InvalidOperationException("Only Host may send a targeted CoopBot client message.");
        _service.SendMessage(new CoopBotEnvelopeMessage(envelope), networkPlayerId);
    }

    public void Broadcast(CoopWireEnvelope envelope)
    {
        if (!IsHost)
            throw new InvalidOperationException("Only Host may broadcast a CoopBot message.");
        _service.SendMessage(new CoopBotEnvelopeMessage(envelope));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _service.UnregisterMessageHandler<CoopBotEnvelopeMessage>(OnMessage);
        _service.Disconnected -= OnDisconnected;
        Received = null;
        Disconnected = null;
    }

    private void OnMessage(CoopBotEnvelopeMessage message, ulong senderNetworkPlayerId)
    {
        if (_disposed) return;
        Received?.Invoke(message.ToEnvelope(), senderNetworkPlayerId);
    }

    private void OnDisconnected(NetErrorInfo error)
    {
        if (!_disposed)
            Disconnected?.Invoke();
    }
}
