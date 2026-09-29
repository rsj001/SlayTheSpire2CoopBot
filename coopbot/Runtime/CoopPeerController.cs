using CoopBot.NativeAdapter;
using CoopBot.Protocol;
using CoopBot.Session;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using System.Security.Cryptography;

namespace CoopBot.Runtime;

internal sealed record CoopProtocolEvent(
    string Direction,
    CoopMessageKind Kind,
    long Sequence,
    ulong PeerNetworkPlayerId,
    long RootRevision,
    string? PlanId,
    string? ActionId,
    string Disposition);

internal enum CoopClientActivity
{
    Idle,
    PlanReady,
    AwaitingConsent,
    Prepared,
    Executing,
    Completed,
    Rejected,
    Failed,
    Cancelled,
}

internal sealed class CoopPeerController : IDisposable
{
    private sealed record PendingConsent(
        CoopWireEnvelope Envelope,
        ActionPreparedPayload Prepared);

    private readonly CombatState _combat;
    private readonly ICoopTransport _transport;
    private readonly LocalActorAgent _agent;
    private readonly PlannedChoiceDriver _choiceDriver;
    private readonly ActionIdempotencyLedger _ledger = new();
    private readonly Dictionary<string, (CoopMessageKind Kind, object Payload)> _terminalResponses =
        new(StringComparer.Ordinal);
    private readonly Dictionary<ulong, long> _outboundSequenceByRecipient = [];
    private readonly Dictionary<ulong, HelloPayload> _hostHellos = [];
    private readonly Dictionary<string, (long RootRevision, string PlanId)> _preparedAuthorizations =
        new(StringComparer.Ordinal);
    private readonly PeerLivenessTracker _liveness = new();
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private ActorAssignmentPayload? _pendingHostAssignment;
    private PendingConsent? _pendingConsent;
    private CoopAutomationMode _automationMode = CoopAutomationMode.ConfirmEach;
    private CoopClientActivity _clientActivity;
    private string _clientActivityDetail = "-";
    private long _publishedRootRevision = -1;
    private string? _publishedRootFingerprint;
    private string? _publishedPlanId;
    private bool _failureRaised;
    private bool _disposed;

    private static string ModVersion
        => typeof(CoopPeerController).Assembly.GetName().Version?.ToString()
           ?? throw new InvalidOperationException("CoopBot assembly has no version.");

    internal CoopPeerController(
        CombatState combat,
        ICoopTransport transport,
        string combatSessionId,
        ulong hostNetworkPlayerId,
        LocalActorAgent agent,
        PlannedChoiceDriver choiceDriver)
    {
        _combat = combat;
        _transport = transport;
        _agent = agent;
        _choiceDriver = choiceDriver;
        Session = new CoopSession(
            combatSessionId,
            transport.LocalNetworkPlayerId,
            hostNetworkPlayerId);
        _transport.Received += OnReceived;
        _transport.Disconnected += OnDisconnected;
    }

    internal CoopSession Session { get; }
    internal event Action<PlanPublishedPayload>? PlanReceived;
    internal event Action<RootPublishedPayload>? RootReceived;
    internal event Action<CoopWireEnvelope, ulong>? HostResponseReceived;
    internal event Action<string>? SessionFailed;
    internal event Action? SessionActivated;
    internal event Action? ClientStateChanged;
    internal event Action<ulong>? ClientPauseRequested;
    internal event Action<CoopProtocolEvent>? ProtocolEventObserved;
    internal event Action<string>? LocalExecutionStarted;
    internal CoopAutomationMode AutomationMode => _automationMode;
    internal CoopClientActivity ClientActivity => _clientActivity;
    internal string ClientActivityDetail => _clientActivityDetail;
    internal CoopPlanActionSnapshot? PendingClientAction
        => _pendingConsent?.Envelope.ReadPayload<ActionPreparePayload>().Action;
    internal bool HasResidualSessionState
        => Session.State != CoopSessionState.Stopped
           || _ledger.Count != 0
           || _terminalResponses.Count != 0
           || _pendingConsent is not null
           || _liveness.Peers.Count != 0;

    internal void StartHost(ActorAssignmentPayload assignment)
    {
        if (!Session.IsHost)
            throw new InvalidOperationException("Only Host may begin Host negotiation.");
        if (_pendingHostAssignment is not null || Session.State != CoopSessionState.Negotiating)
            throw new InvalidOperationException("Host negotiation has already started.");
        if (!assignment.Actors.Any(actor => actor.IsHost
                && actor.NetworkPlayerId == Session.LocalNetworkPlayerId))
            throw new InvalidOperationException("Host assignment does not contain the local Host.");
        _pendingHostAssignment = assignment;
        if (assignment.Actors.Count == 1)
            CompleteHostNegotiation();
    }

    internal void StartClient()
    {
        if (Session.IsHost)
            throw new InvalidOperationException("Host cannot begin Client negotiation.");
        if (Session.State != CoopSessionState.Negotiating)
            throw new InvalidOperationException($"Client session is {Session.State}, expected Negotiating.");
        SendToHost(
            CoopMessageKind.Hello,
            rootRevision: 0,
            new HelloPayload(ModVersion, Session.HostNetworkPlayerId, CoopProtocol.RequiredCapabilities),
            planId: null,
            actionId: null);
    }

    internal void Poll(long nowMilliseconds)
    {
        if (_disposed || Session.State != CoopSessionState.Active)
            return;
        ulong[] expired = _liveness.Expired(nowMilliseconds, CoopProtocol.PeerTimeoutMilliseconds).ToArray();
        if (expired.Length > 0)
        {
            FailSession("peer_timeout", "No message from peers: " + string.Join(',', expired));
            return;
        }
        if (!_liveness.HeartbeatDue(nowMilliseconds, CoopProtocol.HeartbeatIntervalMilliseconds))
            return;
        _liveness.MarkHeartbeatSent(nowMilliseconds);
        if (Session.IsHost)
        {
            foreach (ulong peer in _liveness.Peers.Order())
                SendHeartbeatToClient(peer);
        }
        else
        {
            SendToHost(
                CoopMessageKind.Heartbeat,
                rootRevision: 0,
                new HeartbeatPayload(
                    Session.Inbound.LastAcceptedSequence(Session.HostNetworkPlayerId),
                    Session.State.ToString()),
                planId: null,
                actionId: null);
        }
    }

    internal void PublishPlan(PlanPublishedPayload plan)
    {
        if (!Session.IsHost || Session.State != CoopSessionState.Active)
            throw new InvalidOperationException("Only an active Host session may publish a plan.");
        Broadcast(CoopMessageKind.PlanPublished, plan.RootRevision, plan, plan.PlanId);
    }

    internal void PublishRoot(RootPublishedPayload root)
    {
        if (!Session.IsHost || Session.State != CoopSessionState.Active)
            throw new InvalidOperationException("Only an active Host session may publish a root.");
        Broadcast(CoopMessageKind.RootPublished, root.RootRevision, root);
    }

    internal void PublishAutomationMode(CoopAutomationMode mode)
    {
        if (!Session.IsHost || Session.State != CoopSessionState.Active)
            throw new InvalidOperationException("Only an active Host session may publish automation mode.");
        Broadcast(
            CoopMessageKind.AutomationModeChanged,
            rootRevision: 0,
            new AutomationModeChangedPayload(mode));
    }

    internal void AllowPendingClientAction()
    {
        if (Session.IsHost)
            throw new InvalidOperationException("Host has no remote Client consent action.");
        PendingConsent pending = _pendingConsent
            ?? throw new InvalidOperationException("Client has no action awaiting consent.");
        _pendingConsent = null;
        SendPrepared(pending);
        _clientActivity = CoopClientActivity.Prepared;
        _clientActivityDetail = pending.Envelope.Header.ActionId ?? "-";
        ClientStateChanged?.Invoke();
    }

    internal void RejectPendingClientAction()
        => RejectPendingConsent(CoopActionRejectionCode.UserDeclined, "local player rejected action");

    internal void PauseClientAutomation()
    {
        if (Session.IsHost)
            throw new InvalidOperationException("Host must change automation mode directly.");
        if (_pendingConsent is not null)
            RejectPendingConsent(CoopActionRejectionCode.UserPaused, "local player paused automation");
        _automationMode = CoopAutomationMode.ConfirmEach;
        _clientActivity = CoopClientActivity.Cancelled;
        _clientActivityDetail = "local player paused automation";
        SendToHost(
            CoopMessageKind.ClientControlRequest,
            rootRevision: 0,
            new ClientControlRequestPayload(CoopClientControl.PauseAutomation),
            planId: null,
            actionId: null);
        ClientStateChanged?.Invoke();
    }

    internal void SendPrepare(
        ulong ownerNetworkPlayerId,
        long rootRevision,
        string planId,
        string actionId,
        ActionPreparePayload command)
        => SendToClient(
            ownerNetworkPlayerId,
            CoopMessageKind.ActionPrepare,
            rootRevision,
            command,
            planId,
            actionId);

    internal void SendCommit(
        ulong ownerNetworkPlayerId,
        long rootRevision,
        string planId,
        string actionId)
        => SendToClient(
            ownerNetworkPlayerId,
            CoopMessageKind.ActionCommit,
            rootRevision,
            new ActionCommitPayload(actionId),
            planId,
            actionId);

    internal IReadOnlyList<ulong> GetRemoteObservers(ulong ownerNetworkPlayerId)
        => Session.Actors
            .Where(actor => !actor.IsHost && actor.NetworkPlayerId != ownerNetworkPlayerId)
            .Select(actor => actor.NetworkPlayerId)
            .Order()
            .ToArray();

    internal void SendObserveCommit(
        IReadOnlyList<ulong> observers,
        long rootRevision,
        string planId,
        string actionId,
        CoopPlanActionSnapshot action)
    {
        foreach (ulong observer in observers)
        {
            SendToClient(
                observer,
                CoopMessageKind.ActionObserveCommit,
                rootRevision,
                new ActionObserveCommitPayload(action),
                planId,
                actionId);
        }
    }

    internal void PublishPlanCancelled(
        long rootRevision,
        string planId,
        string actionId,
        string reason)
        => Broadcast(
            CoopMessageKind.PlanCancelled,
            rootRevision,
            new PlanCancelledPayload(reason),
            planId,
            actionId);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetimeCancellation.Cancel();
        _transport.Received -= OnReceived;
        _transport.Disconnected -= OnDisconnected;
        if (_agent.State is LocalActorAgentState.Idle or LocalActorAgentState.Prepared
            or LocalActorAgentState.Reporting)
            _agent.Cancel();
        _choiceDriver.Dispose();
        if (Session.State != CoopSessionState.Stopped)
        {
            Session.BeginStopping();
            Session.FinishStopping();
        }
        _ledger.Clear();
        _terminalResponses.Clear();
        _outboundSequenceByRecipient.Clear();
        _hostHellos.Clear();
        _preparedAuthorizations.Clear();
        _pendingHostAssignment = null;
        _pendingConsent = null;
        _liveness.Clear();
        _transport.Dispose();
        _lifetimeCancellation.Dispose();
    }

    private void OnReceived(CoopWireEnvelope envelope, ulong transportSender)
    {
        if (_disposed) return;
        try
        {
            ProcessReceived(envelope, transportSender);
        }
        catch (Exception exception)
        {
            if (Session.State is not (CoopSessionState.Stopped or CoopSessionState.Failed))
                Session.Fail("message_processing_failed", exception.Message);
            SessionFailed?.Invoke($"message_processing_failed:{exception.Message}");
        }
    }

    private void ProcessReceived(CoopWireEnvelope envelope, ulong transportSender)
    {
        bool mustComeFromHost = !Session.IsHost;
        CoopInboundResult inspection = Session.Inbound.Inspect(
            envelope,
            transportSender,
            mustComeFromHost);
        ProtocolEventObserved?.Invoke(new CoopProtocolEvent(
            "inbound",
            envelope.Kind,
            envelope.Header.MessageSequence,
            transportSender,
            envelope.Header.RootRevision,
            envelope.Header.PlanId,
            envelope.Header.ActionId,
            inspection.Disposition.ToString()));
        if (inspection.Disposition == CoopInboundDisposition.Duplicate)
        {
            if (!Session.IsHost && envelope.Header.ActionId is string duplicateActionId
                && _terminalResponses.TryGetValue(duplicateActionId, out var response))
            {
                SendStoredToHost(response.Kind, envelope.Header.RootRevision, response.Payload,
                    envelope.Header.PlanId, duplicateActionId);
            }
            return;
        }
        if (inspection.Disposition == CoopInboundDisposition.Rejected)
        {
            SessionFailed?.Invoke($"{inspection.Code}:{inspection.Detail}");
            return;
        }
        if (Session.State == CoopSessionState.Active && _liveness.Peers.Contains(transportSender))
            _liveness.Observe(transportSender, Environment.TickCount64);
        if (Session.IsHost)
        {
            if (envelope.Kind == CoopMessageKind.Hello)
            {
                HandleHello(envelope, transportSender);
                return;
            }
            RequireActive();
            if (envelope.Kind == CoopMessageKind.Heartbeat)
            {
                _ = envelope.ReadPayload<HeartbeatPayload>();
                return;
            }
            if (envelope.Kind == CoopMessageKind.ClientControlRequest)
            {
                ClientControlRequestPayload request = envelope.ReadPayload<ClientControlRequestPayload>();
                if (request.Control != CoopClientControl.PauseAutomation)
                    throw new InvalidOperationException($"Unsupported Client control {request.Control}.");
                ClientPauseRequested?.Invoke(transportSender);
                return;
            }
            HostResponseReceived?.Invoke(envelope, transportSender);
            return;
        }

        switch (envelope.Kind)
        {
            case CoopMessageKind.SessionAccepted:
                SessionAcceptedPayload accepted = envelope.ReadPayload<SessionAcceptedPayload>();
                if (!string.Equals(accepted.ModVersion, ModVersion, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"Host CoopBot {accepted.ModVersion} != Client {ModVersion}.");
                Session.AdoptHostCombatSessionId(envelope.Header.CombatSessionId);
                Session.AcceptSession(accepted);
                break;
            case CoopMessageKind.ActorAssignment:
                Session.AcceptAssignment(envelope.ReadPayload<ActorAssignmentPayload>());
                _choiceDriver.ConfigureActors(Session.Actors);
                _liveness.Configure([Session.HostNetworkPlayerId], Environment.TickCount64);
                SessionActivated?.Invoke();
                break;
            case CoopMessageKind.Heartbeat:
                RequireActive();
                _ = envelope.ReadPayload<HeartbeatPayload>();
                break;
            case CoopMessageKind.AutomationModeChanged:
                RequireActive();
                _automationMode = envelope.ReadPayload<AutomationModeChangedPayload>().Mode;
                if (_automationMode == CoopAutomationMode.Auto && _pendingConsent is PendingConsent pending)
                {
                    _pendingConsent = null;
                    SendPrepared(pending);
                    _clientActivity = CoopClientActivity.Prepared;
                    _clientActivityDetail = pending.Envelope.Header.ActionId ?? "-";
                }
                else if (_automationMode is CoopAutomationMode.Observe or CoopAutomationMode.Suggest
                         && _pendingConsent is not null)
                {
                    RejectPendingConsent(
                        CoopActionRejectionCode.UserPaused,
                        $"Host changed mode to {_automationMode}");
                }
                ClientStateChanged?.Invoke();
                break;
            case CoopMessageKind.PlanPublished:
                RequireActive();
                PlanPublishedPayload plan = envelope.ReadPayload<PlanPublishedPayload>();
                if (plan.RootRevision != envelope.Header.RootRevision
                    || plan.RootRevision != _publishedRootRevision
                    || !string.Equals(plan.RootFingerprint, _publishedRootFingerprint, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Plan root {plan.RootRevision}/{plan.RootFingerprint} does not match " +
                        $"published root {_publishedRootRevision}/{_publishedRootFingerprint}.");
                }
                _publishedPlanId = plan.PlanId;
                _clientActivity = CoopClientActivity.PlanReady;
                _clientActivityDetail = envelope.Header.PlanId ?? "-";
                PlanReceived?.Invoke(plan);
                ClientStateChanged?.Invoke();
                break;
            case CoopMessageKind.RootPublished:
                RequireActive();
                RootPublishedPayload publishedRoot = envelope.ReadPayload<RootPublishedPayload>();
                if (publishedRoot.RootRevision != envelope.Header.RootRevision
                    || publishedRoot.RootRevision <= _publishedRootRevision
                    || publishedRoot.ActorFingerprints.Count != Session.Actors.Count)
                {
                    throw new InvalidOperationException(
                        $"Invalid published root revision {publishedRoot.RootRevision}.");
                }
                if (_pendingConsent is not null)
                {
                    RejectPendingConsent(
                        CoopActionRejectionCode.StaleRoot,
                        $"Host published newer root {publishedRoot.RootRevision}");
                }
                if (_preparedAuthorizations.Count > 0)
                {
                    _agent.Cancel();
                    foreach ((string actionId, var authorization) in _preparedAuthorizations.ToArray())
                    {
                        ActionRejectedPayload stale = new(
                            CoopActionRejectionCode.StaleRoot,
                            $"Host published newer root {publishedRoot.RootRevision}");
                        _terminalResponses[actionId] = (CoopMessageKind.ActionRejected, stale);
                        SendToHost(
                            CoopMessageKind.ActionRejected,
                            authorization.RootRevision,
                            stale,
                            authorization.PlanId,
                            actionId);
                    }
                    _preparedAuthorizations.Clear();
                }
                _publishedRootRevision = publishedRoot.RootRevision;
                _publishedRootFingerprint = publishedRoot.RootFingerprint;
                _publishedPlanId = null;
                RootReceived?.Invoke(publishedRoot);
                break;
            case CoopMessageKind.ActionPrepare:
                RequireActive();
                HandlePrepare(envelope);
                break;
            case CoopMessageKind.ActionCommit:
                RequireActive();
                _ = HandleCommitAsync(envelope);
                break;
            case CoopMessageKind.ActionObserveCommit:
                RequireActive();
                RequireCurrentAuthorization(envelope);
                string observeActionId = envelope.Header.ActionId
                    ?? throw new InvalidOperationException("ActionObserveCommit has no ActionId.");
                _choiceDriver.Arm(
                    observeActionId,
                    envelope.ReadPayload<ActionObserveCommitPayload>().Action);
                ActionObservePreparedPayload observed = new(Session.LocalActor.ActorId);
                _terminalResponses[observeActionId] = (CoopMessageKind.ActionObservePrepared, observed);
                SendToHost(
                    CoopMessageKind.ActionObservePrepared,
                    envelope.Header.RootRevision,
                    observed,
                    envelope.Header.PlanId,
                    observeActionId);
                break;
            case CoopMessageKind.PlanCancelled:
                if (_agent.State is LocalActorAgentState.Idle or LocalActorAgentState.Prepared
                    or LocalActorAgentState.Reporting)
                    _agent.Cancel();
                _pendingConsent = null;
                _choiceDriver.Dispose();
                if (envelope.Header.ActionId is string cancelledActionId)
                {
                    _terminalResponses.Remove(cancelledActionId);
                    _preparedAuthorizations.Remove(cancelledActionId);
                }
                _publishedPlanId = null;
                _clientActivity = CoopClientActivity.Cancelled;
                _clientActivityDetail = envelope.ReadPayload<PlanCancelledPayload>().Reason;
                ClientStateChanged?.Invoke();
                break;
            default:
                SessionFailed?.Invoke($"unexpected_host_message:{envelope.Kind}");
                break;
        }
    }

    private void HandleHello(CoopWireEnvelope envelope, ulong senderNetworkPlayerId)
    {
        if (Session.State != CoopSessionState.Negotiating || _pendingHostAssignment is null)
            throw new InvalidOperationException("Host received Hello outside negotiation.");
        ActorBinding sender = _pendingHostAssignment.Actors.SingleOrDefault(actor =>
            actor.NetworkPlayerId == senderNetworkPlayerId)
            ?? throw new InvalidOperationException($"Hello sender {senderNetworkPlayerId} is absent from roster.");
        if (sender.IsHost)
            throw new InvalidOperationException("Host cannot negotiate with itself as a Client.");
        HelloPayload hello = envelope.ReadPayload<HelloPayload>();
        if (hello.ClaimedHostNetworkPlayerId != Session.HostNetworkPlayerId)
            throw new InvalidOperationException(
                $"Client {senderNetworkPlayerId} claimed Host {hello.ClaimedHostNetworkPlayerId}.");
        if (!string.Equals(hello.ModVersion, ModVersion, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Client {senderNetworkPlayerId} CoopBot {hello.ModVersion} != Host {ModVersion}.");
        string[] missing = CoopProtocol.RequiredCapabilities
            .Except(hello.Capabilities, StringComparer.Ordinal)
            .ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException(
                $"Client {senderNetworkPlayerId} lacks capabilities: {string.Join(',', missing)}.");
        if (!_hostHellos.TryAdd(senderNetworkPlayerId, hello))
            throw new InvalidOperationException($"Client {senderNetworkPlayerId} sent Hello twice.");
        int expected = _pendingHostAssignment.Actors.Count(actor => !actor.IsHost);
        if (_hostHellos.Count == expected)
            CompleteHostNegotiation();
    }

    private void CompleteHostNegotiation()
    {
        ActorAssignmentPayload assignment = _pendingHostAssignment
            ?? throw new InvalidOperationException("Host has no pending ActorAssignment.");
        string nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        Session.AcceptAsHost(nonce, CoopProtocol.RequiredCapabilities);
        Session.AcceptAssignment(assignment);
        _choiceDriver.ConfigureActors(Session.Actors);
        ulong[] clients = assignment.Actors
            .Where(actor => !actor.IsHost)
            .Select(actor => actor.NetworkPlayerId)
            .Order()
            .ToArray();
        SessionAcceptedPayload accepted = new(
            CoopProtocol.Version,
            ModVersion,
            Session.HostNetworkPlayerId,
            nonce,
            CoopProtocol.RequiredCapabilities);
        foreach (ulong client in clients)
            SendToClient(client, CoopMessageKind.SessionAccepted, 0, accepted, null, null);
        foreach (ulong client in clients)
            SendToClient(client, CoopMessageKind.ActorAssignment, 0, assignment, null, null);
        _liveness.Configure(clients, Environment.TickCount64);
        _pendingHostAssignment = null;
        SessionActivated?.Invoke();
    }

    private void HandlePrepare(CoopWireEnvelope envelope)
    {
        string actionId = envelope.Header.ActionId
            ?? throw new InvalidOperationException("ActionPrepare has no ActionId.");
        if (!IsCurrentAuthorization(envelope))
        {
            ActionRejectedPayload stale = new(
                CoopActionRejectionCode.StaleRoot,
                $"current={_publishedRootRevision}/{_publishedPlanId} " +
                $"command={envelope.Header.RootRevision}/{envelope.Header.PlanId}");
            _terminalResponses[actionId] = (CoopMessageKind.ActionRejected, stale);
            SendToHost(
                CoopMessageKind.ActionRejected,
                envelope.Header.RootRevision,
                stale,
                envelope.Header.PlanId,
                actionId);
            _clientActivity = CoopClientActivity.Rejected;
            _clientActivityDetail = stale.Code + ":" + stale.Detail;
            ClientStateChanged?.Invoke();
            return;
        }
        if (!_ledger.TryPrepare(actionId))
        {
            SendToHost(CoopMessageKind.ActionRejected, envelope.Header.RootRevision,
                new ActionRejectedPayload(CoopActionRejectionCode.DuplicateAction, actionId),
                envelope.Header.PlanId, actionId);
            return;
        }
        ActionPreparePayload command = envelope.ReadPayload<ActionPreparePayload>();
        ActorBinding localBinding = Session.LocalActor;
        Player localPlayer = _combat.Players[localBinding.ActorId];
        if (localPlayer.NetId != localBinding.NetworkPlayerId)
            throw new InvalidOperationException("Local ActorAssignment no longer matches combat roster.");
        LocalPrepareResult result = _agent.Prepare(
            actionId,
            command,
            _combat,
            localBinding.ActorId,
            localPlayer);
        if (result.Accepted)
        {
            PendingConsent pending = new(envelope, result.Prepared!);
            if (_automationMode == CoopAutomationMode.Auto)
                SendPrepared(pending);
            else if (_automationMode == CoopAutomationMode.ConfirmEach)
            {
                _pendingConsent = pending;
                _clientActivity = CoopClientActivity.AwaitingConsent;
                _clientActivityDetail = actionId;
                ClientStateChanged?.Invoke();
            }
            else
            {
                _pendingConsent = pending;
                RejectPendingConsent(
                    CoopActionRejectionCode.UserPaused,
                    $"automation mode {_automationMode} does not authorize execution");
            }
        }
        else
        {
            _ledger.TryFail(actionId);
            _terminalResponses[actionId] = (CoopMessageKind.ActionRejected, result.Rejected!);
            _clientActivity = CoopClientActivity.Rejected;
            _clientActivityDetail = result.Rejected!.Code + ":" + result.Rejected.Detail;
            SendToHost(CoopMessageKind.ActionRejected, envelope.Header.RootRevision,
                result.Rejected!, envelope.Header.PlanId, actionId);
            ClientStateChanged?.Invoke();
        }
    }

    private void SendPrepared(PendingConsent pending)
    {
        string actionId = pending.Envelope.Header.ActionId
            ?? throw new InvalidOperationException("Prepared action has no ActionId.");
        _preparedAuthorizations[actionId] = (
            pending.Envelope.Header.RootRevision,
            pending.Envelope.Header.PlanId
                ?? throw new InvalidOperationException("Prepared action has no PlanId."));
        SendToHost(
            CoopMessageKind.ActionPrepared,
            pending.Envelope.Header.RootRevision,
            pending.Prepared,
            pending.Envelope.Header.PlanId,
            actionId);
    }

    private void RejectPendingConsent(CoopActionRejectionCode code, string detail)
    {
        PendingConsent pending = _pendingConsent
            ?? throw new InvalidOperationException("Client has no action awaiting consent.");
        _pendingConsent = null;
        string actionId = pending.Envelope.Header.ActionId
            ?? throw new InvalidOperationException("Prepared action has no ActionId.");
        _ledger.TryFail(actionId);
        _agent.Cancel();
        ActionRejectedPayload rejected = new(code, detail);
        _terminalResponses[actionId] = (CoopMessageKind.ActionRejected, rejected);
        _clientActivity = CoopClientActivity.Rejected;
        _clientActivityDetail = code + ":" + detail;
        SendToHost(
            CoopMessageKind.ActionRejected,
            pending.Envelope.Header.RootRevision,
            rejected,
            pending.Envelope.Header.PlanId,
            actionId);
        ClientStateChanged?.Invoke();
    }

    private async Task HandleCommitAsync(CoopWireEnvelope envelope)
    {
        string actionId = envelope.Header.ActionId
            ?? throw new InvalidOperationException("ActionCommit has no ActionId.");
        if (!_preparedAuthorizations.TryGetValue(actionId, out var authorization)
            || authorization.RootRevision != envelope.Header.RootRevision
            || !string.Equals(authorization.PlanId, envelope.Header.PlanId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Commit authorization for {actionId} does not match its Prepare root/plan.");
        }
        if (!_ledger.TryCommit(actionId))
        {
            if (_terminalResponses.TryGetValue(actionId, out var response))
                SendStoredToHost(response.Kind, envelope.Header.RootRevision, response.Payload,
                    envelope.Header.PlanId, actionId);
            return;
        }
        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(
                _lifetimeCancellation.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            LocalExecutionStarted?.Invoke(actionId);
            _clientActivity = CoopClientActivity.Executing;
            _clientActivityDetail = actionId;
            ClientStateChanged?.Invoke();
            ActionAckPayload ack = await _agent.CommitAsync(
                actionId,
                envelope.ReadPayload<ActionCommitPayload>(),
                _combat,
                timeout.Token);
            if (!_ledger.TryComplete(actionId))
                throw new InvalidOperationException($"Cannot complete ledger action {actionId}.");
            _terminalResponses[actionId] = (CoopMessageKind.ActionAck, ack);
            SendToHost(CoopMessageKind.ActionAck, envelope.Header.RootRevision,
                ack, envelope.Header.PlanId, actionId);
            _agent.FinishReport(actionId);
            _preparedAuthorizations.Remove(actionId);
            _clientActivity = CoopClientActivity.Completed;
            _clientActivityDetail = $"{ack.NativeActionType}:{ack.CompletionState}";
            ClientStateChanged?.Invoke();
        }
        catch (Exception exception)
        {
            _ledger.TryFail(actionId);
            if (_disposed)
                return;
            ActionFailedPayload failed = new("native_action_failed", exception.Message);
            _terminalResponses[actionId] = (CoopMessageKind.ActionFailed, failed);
            _preparedAuthorizations.Remove(actionId);
            _clientActivity = CoopClientActivity.Failed;
            _clientActivityDetail = failed.FailureCode + ":" + failed.Detail;
            SendToHost(CoopMessageKind.ActionFailed, envelope.Header.RootRevision,
                failed, envelope.Header.PlanId, actionId);
            ClientStateChanged?.Invoke();
        }
    }

    private void Broadcast<T>(
        CoopMessageKind kind,
        long rootRevision,
        T payload,
        string? planId = null,
        string? actionId = null)
    {
        foreach (ActorBinding actor in Session.Actors.Where(actor => !actor.IsHost))
        {
            _transport.SendToClient(
                actor.NetworkPlayerId,
                Create(actor.NetworkPlayerId, kind, rootRevision, payload, planId, actionId));
        }
    }

    private void SendToClient<T>(
        ulong client,
        CoopMessageKind kind,
        long rootRevision,
        T payload,
        string? planId,
        string? actionId)
        => _transport.SendToClient(client, Create(client, kind, rootRevision, payload, planId, actionId));

    private void SendToHost<T>(
        CoopMessageKind kind,
        long rootRevision,
        T payload,
        string? planId,
        string? actionId)
        => _transport.SendToHost(Create(
            Session.HostNetworkPlayerId,
            kind,
            rootRevision,
            payload,
            planId,
            actionId));

    private void SendStoredToHost(
        CoopMessageKind kind,
        long rootRevision,
        object payload,
        string? planId,
        string? actionId)
    {
        switch (payload)
        {
            case ActionAckPayload ack:
                SendToHost(kind, rootRevision, ack, planId, actionId);
                break;
            case ActionRejectedPayload rejected:
                SendToHost(kind, rootRevision, rejected, planId, actionId);
                break;
            case ActionFailedPayload failed:
                SendToHost(kind, rootRevision, failed, planId, actionId);
                break;
            case ActionObservePreparedPayload observed:
                SendToHost(kind, rootRevision, observed, planId, actionId);
                break;
            default:
                throw new InvalidOperationException($"Unsupported stored response {payload.GetType().FullName}.");
        }
    }

    private CoopWireEnvelope Create<T>(
        ulong recipient,
        CoopMessageKind kind,
        long rootRevision,
        T payload,
        string? planId,
        string? actionId)
    {
        CoopWireEnvelope envelope = CoopWireEnvelope.Create(
            kind,
            new CoopMessageHeader(
                CoopProtocol.Version,
                Session.CombatSessionId,
                NextSequence(recipient),
                _transport.LocalNetworkPlayerId,
                rootRevision,
                planId,
                actionId,
                kind is CoopMessageKind.Hello or CoopMessageKind.SessionAccepted
                    ? null
                    : Session.SessionNonce
                      ?? throw new InvalidOperationException(
                          $"Cannot send {kind} before session nonce negotiation.")),
            payload);
        ProtocolEventObserved?.Invoke(new CoopProtocolEvent(
            "outbound",
            kind,
            envelope.Header.MessageSequence,
            recipient,
            rootRevision,
            planId,
            actionId,
            "Sent"));
        return envelope;
    }

    private long NextSequence(ulong recipient)
    {
        long next = _outboundSequenceByRecipient.GetValueOrDefault(recipient) + 1;
        _outboundSequenceByRecipient[recipient] = next;
        return next;
    }

    private void RequireActive()
    {
        if (Session.State != CoopSessionState.Active)
            throw new InvalidOperationException($"Coop session is {Session.State}, expected Active.");
    }

    private void RequireCurrentAuthorization(CoopWireEnvelope envelope)
    {
        if (!IsCurrentAuthorization(envelope))
        {
            throw new InvalidOperationException(
                $"Message {envelope.Kind} root/plan {envelope.Header.RootRevision}/{envelope.Header.PlanId} " +
                $"does not match current {_publishedRootRevision}/{_publishedPlanId}.");
        }
    }

    private bool IsCurrentAuthorization(CoopWireEnvelope envelope)
        => envelope.Header.RootRevision == _publishedRootRevision
           && string.Equals(envelope.Header.PlanId, _publishedPlanId, StringComparison.Ordinal);

    private void OnDisconnected()
    {
        FailSession("disconnected", "Native multiplayer transport disconnected.");
    }

    private void SendHeartbeatToClient(ulong client)
        => SendToClient(
            client,
            CoopMessageKind.Heartbeat,
            rootRevision: 0,
            new HeartbeatPayload(Session.Inbound.LastAcceptedSequence(client), Session.State.ToString()),
            planId: null,
            actionId: null);

    private void FailSession(string code, string detail)
    {
        if (_failureRaised || Session.State is CoopSessionState.Stopped or CoopSessionState.Failed)
            return;
        _failureRaised = true;
        Session.Fail(code, detail);
        SessionFailed?.Invoke($"{code}:{detail}");
    }
}
