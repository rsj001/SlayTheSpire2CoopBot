using CoopBot.NativeAdapter;
using CoopBot.Protocol;
using CoopBot.Session;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CoopBot.Runtime;

internal sealed class CoopPeerController : IDisposable
{
    private readonly CombatState _combat;
    private readonly ICoopTransport _transport;
    private readonly LocalActorAgent _agent;
    private readonly PlannedChoiceDriver _choiceDriver;
    private readonly ActionIdempotencyLedger _ledger = new();
    private readonly Dictionary<string, (CoopMessageKind Kind, object Payload)> _terminalResponses =
        new(StringComparer.Ordinal);
    private readonly Dictionary<ulong, long> _outboundSequenceByRecipient = [];
    private bool _disposed;

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
    internal event Action<CoopWireEnvelope, ulong>? HostResponseReceived;
    internal event Action<string>? SessionFailed;

    internal void StartHost(ActorAssignmentPayload assignment)
    {
        if (!Session.IsHost)
            throw new InvalidOperationException("Only Host may publish ActorAssignment.");
        Session.AcceptAssignment(assignment);
        _choiceDriver.ConfigureActors(Session.Actors);
        Broadcast(CoopMessageKind.ActorAssignment, rootRevision: 0, assignment);
    }

    internal void PublishPlan(PlanPublishedPayload plan)
    {
        if (!Session.IsHost || Session.State != CoopSessionState.Active)
            throw new InvalidOperationException("Only an active Host session may publish a plan.");
        Broadcast(CoopMessageKind.PlanPublished, plan.RootRevision, plan, plan.PlanId);
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
        _transport.Received -= OnReceived;
        _transport.Disconnected -= OnDisconnected;
        _ledger.Clear();
        _terminalResponses.Clear();
        _transport.Dispose();
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
        if (Session.IsHost)
        {
            HostResponseReceived?.Invoke(envelope, transportSender);
            return;
        }

        switch (envelope.Kind)
        {
            case CoopMessageKind.ActorAssignment:
                Session.AcceptAssignment(envelope.ReadPayload<ActorAssignmentPayload>());
                _choiceDriver.ConfigureActors(Session.Actors);
                break;
            case CoopMessageKind.PlanPublished:
                RequireActive();
                PlanReceived?.Invoke(envelope.ReadPayload<PlanPublishedPayload>());
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
                _choiceDriver.Dispose();
                if (envelope.Header.ActionId is string cancelledActionId)
                    _terminalResponses.Remove(cancelledActionId);
                break;
            default:
                SessionFailed?.Invoke($"unexpected_host_message:{envelope.Kind}");
                break;
        }
    }

    private void HandlePrepare(CoopWireEnvelope envelope)
    {
        string actionId = envelope.Header.ActionId
            ?? throw new InvalidOperationException("ActionPrepare has no ActionId.");
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
            SendToHost(CoopMessageKind.ActionPrepared, envelope.Header.RootRevision,
                result.Prepared!, envelope.Header.PlanId, actionId);
        }
        else
        {
            _ledger.TryFail(actionId);
            _terminalResponses[actionId] = (CoopMessageKind.ActionRejected, result.Rejected!);
            SendToHost(CoopMessageKind.ActionRejected, envelope.Header.RootRevision,
                result.Rejected!, envelope.Header.PlanId, actionId);
        }
    }

    private async Task HandleCommitAsync(CoopWireEnvelope envelope)
    {
        string actionId = envelope.Header.ActionId
            ?? throw new InvalidOperationException("ActionCommit has no ActionId.");
        if (!_ledger.TryCommit(actionId))
        {
            if (_terminalResponses.TryGetValue(actionId, out var response))
                SendStoredToHost(response.Kind, envelope.Header.RootRevision, response.Payload,
                    envelope.Header.PlanId, actionId);
            return;
        }
        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
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
        }
        catch (Exception exception)
        {
            _ledger.TryFail(actionId);
            ActionFailedPayload failed = new("native_action_failed", exception.Message);
            _terminalResponses[actionId] = (CoopMessageKind.ActionFailed, failed);
            SendToHost(CoopMessageKind.ActionFailed, envelope.Header.RootRevision,
                failed, envelope.Header.PlanId, actionId);
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
        => CoopWireEnvelope.Create(
            kind,
            new CoopMessageHeader(
                CoopProtocol.Version,
                Session.CombatSessionId,
                NextSequence(recipient),
                _transport.LocalNetworkPlayerId,
                rootRevision,
                planId,
                actionId),
            payload);

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

    private void OnDisconnected()
    {
        if (Session.State is not (CoopSessionState.Stopped or CoopSessionState.Failed))
            Session.Fail("disconnected", "Native multiplayer transport disconnected.");
        SessionFailed?.Invoke("disconnected");
    }
}
