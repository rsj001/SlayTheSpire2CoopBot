using System.Text.Json;
using CoopBot.Protocol;
using CoopBot.Session;
using CoopBot.Capture;

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
const string nonce = "AABBCCDDEEFF00112233445566778899";
foreach (CoopSession instance in instances)
{
    if (instance.IsHost)
        instance.AcceptAsHost(nonce, CoopProtocol.RequiredCapabilities);
    else
        instance.AcceptSession(new SessionAcceptedPayload(
            CoopProtocol.Version,
            "0.1.0.0",
            hostId,
            nonce,
            CoopProtocol.RequiredCapabilities));
    Check(instance.State == CoopSessionState.Accepted, $"instance {instance.LocalNetworkPlayerId} negotiated");
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

var nonceGate = new CoopInboundGate(sessionId, hostId);
nonceGate.BindSessionNonce(nonce);
var heartbeat = CoopWireEnvelope.Create(
    CoopMessageKind.Heartbeat,
    header with { SessionNonce = nonce },
    new HeartbeatPayload(0, CoopSessionState.Active.ToString()));
Check(nonceGate.Inspect(heartbeat, hostId, mustComeFromHost: true).Disposition
    == CoopInboundDisposition.Accepted, "negotiated nonce accepted");
Check(nonceGate.Inspect(
        heartbeat with { Header = heartbeat.Header with { MessageSequence = 2, SessionNonce = "wrong" } },
        hostId,
        mustComeFromHost: true).Code == "session_nonce_mismatch", "wrong nonce rejected");
var unboundGate = new CoopInboundGate(sessionId, hostId);
Check(unboundGate.Inspect(heartbeat, hostId, mustComeFromHost: true).Code == "session_nonce_unbound",
    "action traffic rejected before negotiation");

bool missingCapabilityRejected = false;
try
{
    var incompatible = new CoopSession(sessionId, 202, hostId);
    incompatible.AcceptSession(new SessionAcceptedPayload(
        CoopProtocol.Version,
        "0.1.0.0",
        hostId,
        nonce,
        ["actor-assignment-v1"]));
}
catch (InvalidOperationException)
{
    missingCapabilityRejected = true;
}
Check(missingCapabilityRejected, "missing required capability rejected");

const string clientLocalSessionId = "client-local-combat-id";
var clientNegotiation = new CoopSession(clientLocalSessionId, 202, hostId);
var acceptedHeader = new CoopMessageHeader(
    CoopProtocol.Version,
    sessionId,
    1,
    hostId,
    0);
var acceptedEnvelope = CoopWireEnvelope.Create(
    CoopMessageKind.SessionAccepted,
    acceptedHeader,
    new SessionAcceptedPayload(
        CoopProtocol.Version,
        "0.1.0.0",
        hostId,
        nonce,
        CoopProtocol.RequiredCapabilities));
Check(clientNegotiation.Inbound.Inspect(acceptedEnvelope, hostId, mustComeFromHost: true).Disposition
    == CoopInboundDisposition.Accepted, "Client accepts Host session ID during negotiation");
clientNegotiation.AdoptHostCombatSessionId(sessionId);
clientNegotiation.AcceptSession(acceptedEnvelope.ReadPayload<SessionAcceptedPayload>());
Check(clientNegotiation.CombatSessionId == sessionId
    && clientNegotiation.Inbound.CombatSessionId == sessionId,
    "Client binds authoritative Host session ID");
var postNegotiationWrongSession = heartbeat with
{
    Header = heartbeat.Header with
    {
        MessageSequence = 2,
        CombatSessionId = clientLocalSessionId,
    },
};
Check(clientNegotiation.Inbound.Inspect(
        postNegotiationWrongSession,
        hostId,
        mustComeFromHost: true).Code == "session_mismatch",
    "Client rejects old local session ID after negotiation");

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

var stableGate = new StableRootGate();
RootStabilitySample stableA = new(
    "combat-1", "fingerprint-a", true, true, true, true, true, true, true);
Check(stableGate.Observe(stableA).Disposition == StableRootGateDisposition.Candidate, "stable root needs consecutive observation");
StableRootGateResult firstPublished = stableGate.Observe(stableA);
Check(firstPublished.Disposition == StableRootGateDisposition.Publish && firstPublished.RootRevision == 1, "stable root publishes revision one");
Check(stableGate.Observe(stableA).Disposition == StableRootGateDisposition.Unchanged, "unchanged root does not increment revision");
RootStabilitySample busy = stableA with { NativeExecutorIdle = false };
Check(stableGate.Observe(busy).Reason == "native_executor_active", "active executor resets candidate");
RootStabilitySample stableB = stableA with { Fingerprint = "fingerprint-b" };
Check(stableGate.Observe(stableB).Disposition == StableRootGateDisposition.Candidate, "changed root starts new candidate");
StableRootGateResult secondPublished = stableGate.Observe(stableB);
Check(secondPublished.Disposition == StableRootGateDisposition.Publish && secondPublished.RootRevision == 2, "changed stable root increments revision");
RootStabilitySample choiceBusy = stableB with { ChoiceTransactionsIdle = false };
Check(stableGate.Observe(choiceBusy).Reason == "choice_transaction_active", "choice transaction blocks root");
RootStabilitySample rosterChanged = stableB with { RosterStable = false };
Check(stableGate.Observe(rosterChanged).Reason == "roster_changed", "roster change blocks root");

var remoteTracker = new HostRemoteActionTracker();
foreach (ActorBinding remote in assignment.Actors.Where(actor => !actor.IsHost))
{
    string actionId = $"remote-{remote.ActorId}";
    Check(remoteTracker.Begin(actionId, remote.ActorId, remote.NetworkPlayerId).State
        == RemoteActionState.Preparing, $"remote actor {remote.ActorId} preparing");
    Check(remoteTracker.Prepared(actionId, remote.NetworkPlayerId).State
        == RemoteActionState.Prepared, $"remote actor {remote.ActorId} prepared");
    Check(remoteTracker.Commit(actionId).State
        == RemoteActionState.Committed, $"remote actor {remote.ActorId} committed");
    Check(remoteTracker.Acknowledge(actionId, remote.NetworkPlayerId).State
        == RemoteActionState.Completed, $"remote actor {remote.ActorId} acknowledged");
}
bool wrongRemoteOwnerRejected = false;
remoteTracker.Begin("wrong-owner", actorId: 1, ownerNetworkPlayerId: 202);
try
{
    remoteTracker.Prepared("wrong-owner", senderNetworkPlayerId: 303);
}
catch (InvalidOperationException)
{
    wrongRemoteOwnerRejected = true;
}
Check(wrongRemoteOwnerRejected, "remote prepared sender must own actor");
Check(remoteTracker.Timeout("wrong-owner", "prepare timeout").State == RemoteActionState.TimedOut,
    "remote prepare timeout terminal");
remoteTracker.Begin("remote-reject", actorId: 2, ownerNetworkPlayerId: 303);
Check(remoteTracker.Reject("remote-reject", 303, "stale root").State == RemoteActionState.Rejected,
    "remote rejection terminal");
bool duplicateAckRejected = false;
remoteTracker.Begin("duplicate-ack", actorId: 3, ownerNetworkPlayerId: 404);
remoteTracker.Prepared("duplicate-ack", 404);
remoteTracker.Commit("duplicate-ack");
remoteTracker.Acknowledge("duplicate-ack", 404);
try
{
    remoteTracker.Acknowledge("duplicate-ack", 404);
}
catch (InvalidOperationException)
{
    duplicateAckRejected = true;
}
Check(duplicateAckRejected, "duplicate remote ack rejected");

var observerBarrier = new HostObserverBarrier();
Check(!observerBarrier.Begin("choice-action", [303, 404]), "choice observers create pending barrier");
Check(!observerBarrier.Acknowledge("choice-action", 303), "first choice observer does not release barrier");
Check(observerBarrier.Acknowledge("choice-action", 404), "all choice observers release barrier");
Check(observerBarrier.Acknowledge("choice-action", 404), "duplicate choice observer ACK is idempotent");
bool unknownObserverRejected = false;
try
{
    observerBarrier.Acknowledge("choice-action", 202);
}
catch (InvalidOperationException)
{
    unknownObserverRejected = true;
}
Check(unknownObserverRejected, "unknown choice observer rejected");
observerBarrier.Clear("choice-action");
Check(observerBarrier.ActionId is null && observerBarrier.Pending.Count == 0, "choice observer barrier clears");

var planLease = new HostPlanLease();
planLease.Publish("plan-manual", 10);
HostRootChange manual = planLease.ObserveRoot(11);
Check(manual.Kind == HostRootChangeKind.ManualInsertion
    && manual.PlanId == "plan-manual"
    && manual.ActionId is null, "uncommanded new root is a manual insertion");
planLease.Publish("plan-commanded", 11);
planLease.BeginAction("plan-commanded", "action-0");
Check(planLease.ObserveRoot(11).Kind == HostRootChangeKind.Ignored, "same root does not consume action lease");
HostRootChange expectedRoot = planLease.ObserveRoot(12);
Check(expectedRoot.Kind == HostRootChangeKind.ExpectedActionResult
    && expectedRoot.ActionId == "action-0", "commanded new root is an expected action result");
planLease.Publish("plan-cancel", 12);
planLease.BeginAction("plan-cancel", "action-cancel");
planLease.Cancel("action-cancel");
Check(planLease.PlanId is null && planLease.ActionId is null, "cancel clears plan lease");

var liveness = new PeerLivenessTracker();
liveness.Configure([202, 303, 404], nowMilliseconds: 1_000);
Check(!liveness.HeartbeatDue(2_999, CoopProtocol.HeartbeatIntervalMilliseconds),
    "heartbeat waits for interval");
Check(liveness.HeartbeatDue(3_000, CoopProtocol.HeartbeatIntervalMilliseconds),
    "heartbeat becomes due at interval");
liveness.MarkHeartbeatSent(3_000);
liveness.Observe(202, 9_000);
Check(liveness.Expired(11_000, CoopProtocol.PeerTimeoutMilliseconds).SequenceEqual([303UL, 404UL]),
    "silent peers expire deterministically");
liveness.Clear();
Check(liveness.Peers.Count == 0, "liveness cleanup clears peers");

var breaker = new HostFailureCircuitBreaker();
Check(!breaker.Record(7, "stale_root").Tripped, "first same-root failure does not trip");
Check(!breaker.Record(7, "stale_root").Tripped, "second same-root failure does not trip");
HostFailureObservation tripped = breaker.Record(7, "stale_root");
Check(tripped.Tripped && tripped.ConsecutiveCount == HostFailureCircuitBreaker.TripThreshold,
    "third same-root failure trips auto circuit");
Check(!breaker.Record(8, "stale_root").Tripped, "new root resets failure circuit");
Check(!breaker.Record(8, "invalid_target").Tripped, "different failure resets failure circuit");
breaker.Reset();
Check(!breaker.Record(8, "invalid_target").Tripped, "successful verification reset clears failure circuit");

Console.WriteLine($"Passed {checks} CoopBot protocol and session contracts.");
