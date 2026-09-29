using CoopBot.Protocol;

namespace CoopBot.Session;

public enum CoopInboundDisposition
{
    Accepted,
    Duplicate,
    Rejected,
}

public sealed record CoopInboundResult(
    CoopInboundDisposition Disposition,
    string Code,
    string Detail)
{
    public static readonly CoopInboundResult Accepted = new(
        CoopInboundDisposition.Accepted,
        "accepted",
        "Message accepted.");
}

public sealed class CoopInboundGate
{
    private readonly Dictionary<ulong, long> _lastSequenceBySender = [];

    public CoopInboundGate(
        string combatSessionId,
        ulong? expectedHostNetworkPlayerId = null)
    {
        if (string.IsNullOrWhiteSpace(combatSessionId))
            throw new ArgumentException("Combat session ID is required.", nameof(combatSessionId));
        CombatSessionId = combatSessionId;
        ExpectedHostNetworkPlayerId = expectedHostNetworkPlayerId;
    }

    public string CombatSessionId { get; private set; }
    public ulong? ExpectedHostNetworkPlayerId { get; private set; }
    public string? ExpectedSessionNonce { get; private set; }

    public void BindHost(ulong hostNetworkPlayerId)
    {
        if (ExpectedHostNetworkPlayerId is ulong current && current != hostNetworkPlayerId)
            throw new InvalidOperationException(
                $"Host is already bound to {current}; cannot bind {hostNetworkPlayerId}.");
        ExpectedHostNetworkPlayerId = hostNetworkPlayerId;
    }

    public void BindCombatSessionId(string combatSessionId)
    {
        if (string.IsNullOrWhiteSpace(combatSessionId))
            throw new ArgumentException("Combat session ID is required.", nameof(combatSessionId));
        if (ExpectedSessionNonce is not null
            && !string.Equals(CombatSessionId, combatSessionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Cannot rebind an already negotiated combat session ID.");
        }
        CombatSessionId = combatSessionId;
    }

    public void BindSessionNonce(string sessionNonce)
    {
        if (string.IsNullOrWhiteSpace(sessionNonce))
            throw new ArgumentException("Session nonce is required.", nameof(sessionNonce));
        if (ExpectedSessionNonce is string current
            && !string.Equals(current, sessionNonce, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Session nonce is already bound to a different value.");
        }
        ExpectedSessionNonce = sessionNonce;
    }

    public CoopInboundResult Inspect(
        CoopWireEnvelope envelope,
        ulong transportSenderNetworkPlayerId,
        bool mustComeFromHost)
    {
        CoopMessageHeader header = envelope.Header;
        if (header.ProtocolVersion != CoopProtocol.Version)
            return Reject("protocol_mismatch", $"Expected {CoopProtocol.Version}, got {header.ProtocolVersion}.");
        bool mayNegotiateDifferentSession = ExpectedSessionNonce is null
            && (envelope.Kind == CoopMessageKind.Hello && !mustComeFromHost
                || envelope.Kind == CoopMessageKind.SessionAccepted && mustComeFromHost);
        if (!string.Equals(header.CombatSessionId, CombatSessionId, StringComparison.Ordinal)
            && !mayNegotiateDifferentSession)
            return Reject("session_mismatch", $"Expected {CombatSessionId}, got {header.CombatSessionId}.");
        if (header.SenderNetworkPlayerId != transportSenderNetworkPlayerId)
            return Reject(
                "sender_mismatch",
                $"Transport sender {transportSenderNetworkPlayerId} != claimed sender {header.SenderNetworkPlayerId}.");
        if (mustComeFromHost
            && ExpectedHostNetworkPlayerId is ulong expectedHost
            && transportSenderNetworkPlayerId != expectedHost)
        {
            return Reject(
                "host_mismatch",
                $"Expected host {expectedHost}, got sender {transportSenderNetworkPlayerId}.");
        }
        bool negotiationMessage = envelope.Kind is CoopMessageKind.Hello or CoopMessageKind.SessionAccepted;
        if (!negotiationMessage)
        {
            if (ExpectedSessionNonce is null)
                return Reject("session_nonce_unbound", "Session negotiation has not bound a nonce.");
            if (!string.Equals(header.SessionNonce, ExpectedSessionNonce, StringComparison.Ordinal))
                return Reject("session_nonce_mismatch", "Message session nonce does not match the negotiated session.");
        }
        else if (header.SessionNonce is not null)
        {
            return Reject("unexpected_session_nonce", "Negotiation messages must not claim a session nonce.");
        }
        if (header.MessageSequence <= 0)
            return Reject("invalid_sequence", $"Sequence {header.MessageSequence} must be positive.");

        long last = _lastSequenceBySender.GetValueOrDefault(transportSenderNetworkPlayerId);
        if (header.MessageSequence <= last)
            return new CoopInboundResult(
                CoopInboundDisposition.Duplicate,
                "duplicate_sequence",
                $"Sequence {header.MessageSequence} was already observed; last is {last}.");
        if (header.MessageSequence != last + 1)
            return Reject(
                "out_of_order_sequence",
                $"Expected sequence {last + 1}, got {header.MessageSequence}.");

        _lastSequenceBySender[transportSenderNetworkPlayerId] = header.MessageSequence;
        return CoopInboundResult.Accepted;
    }

    public long LastAcceptedSequence(ulong senderNetworkPlayerId)
        => _lastSequenceBySender.GetValueOrDefault(senderNetworkPlayerId);

    private static CoopInboundResult Reject(string code, string detail)
        => new(CoopInboundDisposition.Rejected, code, detail);
}
