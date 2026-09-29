using System.Text.Json;
using CoopBot.Protocol;
using CoopBot.Session;

int checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    checks++;
}

const string sessionId = "00112233445566778899aabbccddeeff";
const ulong hostId = 101;
var roster = new (ulong NetworkPlayerId, string CharacterId)[]
{
    (101, "Ironclad"),
    (202, "Silent"),
    (303, "Defect"),
    (404, "Necrobinder"),
};

ActorAssignmentPayload assignment = CoopSession.BuildActorAssignment(roster, hostId);
Check(assignment.Actors.Select(actor => actor.ActorId).SequenceEqual([0, 1, 2, 3]), "stable actor order");
Check(assignment.Actors.Single(actor => actor.IsHost).NetworkPlayerId == hostId, "one negotiated host");

CoopSession[] instances = roster
    .Select(player => new CoopSession(sessionId, player.NetworkPlayerId, hostId))
    .ToArray();
foreach (CoopSession instance in instances)
{
    instance.AcceptAssignment(assignment);
    Check(instance.State == CoopSessionState.Active, $"instance {instance.LocalNetworkPlayerId} active");
    Check(instance.LocalActor.NetworkPlayerId == instance.LocalNetworkPlayerId, "local actor derived per instance");
    Check(instance.Actors.OrderBy(actor => actor.ActorId).SequenceEqual(assignment.Actors), "all instances share assignment");
}

var header = new CoopMessageHeader(CoopProtocol.Version, sessionId, 1, hostId, 0);
var hello = CoopWireEnvelope.Create(
    CoopMessageKind.Hello,
    header,
    new HelloPayload("0.1.0", hostId, ["protocol-v1", "actor-assignment"]));
string serialized = JsonSerializer.Serialize(hello, CoopProtocol.JsonOptions);
string withUnknownField = serialized[..^1] + ",\"futureField\":42}";
CoopWireEnvelope roundTrip = JsonSerializer.Deserialize<CoopWireEnvelope>(withUnknownField, CoopProtocol.JsonOptions)
    ?? throw new InvalidOperationException("round trip null");
Check(roundTrip == hello, "wire DTO round trip tolerates unknown fields");
Check(roundTrip.ReadPayload<HelloPayload>().ClaimedHostNetworkPlayerId == hostId, "typed payload round trip");

var gate = new CoopInboundGate(sessionId, hostId);
Check(gate.Inspect(hello, hostId, mustComeFromHost: true).Disposition == CoopInboundDisposition.Accepted, "first sequence accepted");
Check(gate.Inspect(hello, hostId, mustComeFromHost: true).Disposition == CoopInboundDisposition.Duplicate, "duplicate sequence ignored");

CoopWireEnvelope outOfOrder = hello with { Header = header with { MessageSequence = 3 } };
CoopInboundResult outOfOrderResult = gate.Inspect(outOfOrder, hostId, mustComeFromHost: true);
Check(outOfOrderResult.Code == "out_of_order_sequence", "out of order rejected");
CoopWireEnvelope second = hello with { Header = header with { MessageSequence = 2 } };
Check(gate.Inspect(second, hostId, mustComeFromHost: true).Disposition == CoopInboundDisposition.Accepted, "missing sequence may arrive later");

CoopWireEnvelope badVersion = hello with
{
    Header = header with { ProtocolVersion = CoopProtocol.Version + 1, MessageSequence = 3 },
};
Check(gate.Inspect(badVersion, hostId, mustComeFromHost: true).Code == "protocol_mismatch", "version conflict rejected");
CoopWireEnvelope falseSender = hello with
{
    Header = header with { MessageSequence = 3, SenderNetworkPlayerId = 202 },
};
Check(gate.Inspect(falseSender, hostId, mustComeFromHost: true).Code == "sender_mismatch", "claimed sender rejected");
CoopWireEnvelope falseHost = hello with
{
    Header = header with { MessageSequence = 1, SenderNetworkPlayerId = 202 },
};
Check(gate.Inspect(falseHost, 202, mustComeFromHost: true).Code == "host_mismatch", "host conflict rejected");

var ledger = new ActionIdempotencyLedger();
Check(ledger.TryPrepare("action-1"), "action first prepare");
Check(!ledger.TryPrepare("action-1"), "duplicate prepare rejected");
Check(ledger.TryCommit("action-1"), "prepared action committed");
Check(!ledger.TryCommit("action-1"), "duplicate commit rejected");
Check(ledger.TryComplete("action-1"), "committed action completed");
Check(!ledger.TryComplete("action-1") && !ledger.TryFail("action-1"), "terminal action cannot repeat");

CoopSession stopped = instances[0];
stopped.BeginStopping();
stopped.FinishStopping();
Check(stopped.State == CoopSessionState.Stopped && stopped.Actors.Count == 0, "stop clears actor session");
var failed = new CoopSession(sessionId, 202, hostId);
failed.Fail("host_disconnected", "Host disconnected during negotiation.");
Check(failed.State == CoopSessionState.Failed && failed.FailureCode == "host_disconnected", "failure is stable and explicit");

bool illegalTransitionRejected = false;
try
{
    stopped.BeginStopping();
}
catch (InvalidOperationException)
{
    illegalTransitionRejected = true;
}
Check(illegalTransitionRejected, "illegal state transition rejected");

Console.WriteLine($"Passed {checks} CoopBot protocol and session contracts.");
