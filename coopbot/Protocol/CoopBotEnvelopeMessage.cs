using System.Text;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;

namespace CoopBot.Protocol;

public record struct CoopBotEnvelopeMessage : INetMessage
{
    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.VeryDebug;
    public bool ShouldBuffer => true;

    public CoopMessageKind Kind;
    public CoopMessageHeader Header;
    public string PayloadJson;

    public CoopBotEnvelopeMessage(CoopWireEnvelope envelope)
    {
        Kind = envelope.Kind;
        Header = envelope.Header;
        PayloadJson = envelope.PayloadJson;
    }

    public readonly CoopWireEnvelope ToEnvelope()
        => new(Kind, Header, PayloadJson);

    public readonly void Serialize(PacketWriter writer)
    {
        ValidatePayload(PayloadJson);
        writer.WriteInt((int)Kind);
        writer.WriteInt(Header.ProtocolVersion);
        writer.WriteString(Header.CombatSessionId);
        writer.WriteLong(Header.MessageSequence);
        writer.WriteULong(Header.SenderNetworkPlayerId);
        writer.WriteLong(Header.RootRevision);
        WriteNullableString(writer, Header.PlanId);
        WriteNullableString(writer, Header.ActionId);
        writer.WriteString(PayloadJson);
    }

    public void Deserialize(PacketReader reader)
    {
        Kind = (CoopMessageKind)reader.ReadInt();
        Header = new CoopMessageHeader(
            reader.ReadInt(),
            reader.ReadString(),
            reader.ReadLong(),
            reader.ReadULong(),
            reader.ReadLong(),
            ReadNullableString(reader),
            ReadNullableString(reader));
        PayloadJson = reader.ReadString();
        ValidatePayload(PayloadJson);
    }

    private static void WriteNullableString(PacketWriter writer, string? value)
    {
        writer.WriteBool(value is not null);
        if (value is not null)
            writer.WriteString(value);
    }

    private static string? ReadNullableString(PacketReader reader)
        => reader.ReadBool() ? reader.ReadString() : null;

    private static void ValidatePayload(string? payload)
    {
        if (payload is null)
            throw new InvalidOperationException("CoopBot envelope payload is null.");
        if (Encoding.UTF8.GetByteCount(payload) > CoopProtocol.MaximumPayloadBytes)
            throw new InvalidOperationException(
                $"CoopBot envelope exceeds {CoopProtocol.MaximumPayloadBytes} payload bytes.");
    }
}
