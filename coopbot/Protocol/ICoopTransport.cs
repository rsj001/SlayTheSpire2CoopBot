namespace CoopBot.Protocol;

internal interface ICoopTransport : IDisposable
{
    ulong LocalNetworkPlayerId { get; }
    bool IsHost { get; }
    event Action<CoopWireEnvelope, ulong>? Received;
    event Action? Disconnected;
    void SendToHost(CoopWireEnvelope envelope);
    void SendToClient(ulong networkPlayerId, CoopWireEnvelope envelope);
    void Broadcast(CoopWireEnvelope envelope);
}
