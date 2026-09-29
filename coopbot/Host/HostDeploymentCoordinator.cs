using CombatSolver;
using CoopBot.Capture;
using CoopBot.NativeAdapter;
using CoopBot.Protocol;
using MegaCrit.Sts2.Core.Combat;
using CoopBot.Runtime;
using CoopBot.Session;

namespace CoopBot.Host;

internal sealed record HostDeploymentEvent(
    string Kind,
    string PlanId,
    string ActionId,
    string Detail);

internal sealed class HostDeploymentCoordinator : IDisposable
{
    private sealed record AwaitingVerification(
        string PlanId,
        string ActionId,
        long SourceRootRevision,
        JointCombatSnapshot Expected,
        bool LocalAgentOwnsReport);

    private readonly CombatState _combat;
    private readonly HostCombatRecorder _recorder;
    private readonly HostSearchCoordinator _search;
    private readonly LocalActorAgent _agent;
    private readonly CoopPeerController _peer;
    private readonly HostRemoteActionTracker _remote = new();
    private long _remoteDeadlineMilliseconds;
    private HostSearchResult? _remotePlan;
    private AwaitingVerification? _awaiting;
    private bool _starting;
    private bool _disposed;

    internal HostDeploymentCoordinator(
        CombatState combat,
        HostCombatRecorder recorder,
        HostSearchCoordinator search,
        LocalActorAgent agent,
        CoopPeerController peer)
    {
        _combat = combat;
        _recorder = recorder;
        _search = search;
        _agent = agent;
        _peer = peer;
        _recorder.RootRecorded += OnRootRecorded;
        _peer.HostResponseReceived += OnHostResponseReceived;
    }

    internal event Action<HostDeploymentEvent>? EventPublished;

    internal void Poll()
    {
        if (_remoteDeadlineMilliseconds == 0
            || Environment.TickCount64 < _remoteDeadlineMilliseconds)
            return;
        _remoteDeadlineMilliseconds = 0;
        RemoteActionStatus? current = _remote.Current;
        if (current is null || current.State is RemoteActionState.Completed
            or RemoteActionState.Rejected or RemoteActionState.Failed or RemoteActionState.TimedOut)
            return;
        _remote.Timeout(current.ActionId, "remote response timeout");
        _remotePlan = null;
        EventPublished?.Invoke(new HostDeploymentEvent(
            "timeout", "-", current.ActionId, "remote response timeout"));
    }

    internal void ExecuteNext()
    {
        if (_disposed || _starting || _awaiting is not null)
            return;
        _ = ExecuteNextAsync();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _recorder.RootRecorded -= OnRootRecorded;
        _peer.HostResponseReceived -= OnHostResponseReceived;
        _remoteDeadlineMilliseconds = 0;
        if (_agent.State is LocalActorAgentState.Idle or LocalActorAgentState.Prepared or LocalActorAgentState.Reporting)
            _agent.Cancel();
        _awaiting = null;
    }

    private async Task ExecuteNextAsync()
    {
        _starting = true;
        HostSearchResult? result = _search.Current;
        string planId = result?.Published.PlanId ?? "-";
        string actionId = "-";
        try
        {
            if (result is null || result.Search.Actions.Count == 0)
                throw new InvalidOperationException("No published Host plan is ready.");
            PlanAction action = result.Search.Actions[0];
            actionId = $"{planId}:0";
            ActionPreparePayload command = HostActionCommandFactory.Create(result, actionIndex: 0);
            if (action.Actor != result.RecordedRoot.Root.LocalActorId)
            {
                ActorBinding owner = _peer.Session.Actors.Single(binding =>
                    binding.ActorId == action.Actor.Index);
                _remote.Begin(actionId, action.Actor.Index, owner.NetworkPlayerId);
                _remotePlan = result;
                _peer.SendPrepare(
                    owner.NetworkPlayerId,
                    result.RecordedRoot.RootRevision,
                    planId,
                    actionId,
                    command);
                StartRemoteTimeout(actionId);
                EventPublished?.Invoke(new HostDeploymentEvent(
                    "prepare_sent", planId, actionId, $"owner={owner.NetworkPlayerId}"));
                return;
            }
            LocalPrepareResult prepared = _agent.Prepare(
                actionId,
                command,
                _combat,
                action.Actor.Index,
                result.RecordedRoot.Root.Actors[action.Actor.Index].PlayerIdentity);
            if (!prepared.Accepted)
                throw new InvalidOperationException($"Local prepare rejected: {prepared.Rejected}.");
            EventPublished?.Invoke(new HostDeploymentEvent(
                "prepared", planId, actionId, prepared.Prepared!.NativeInstanceIdentity));
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
            ActionAckPayload ack = await _agent.CommitAsync(
                actionId,
                new ActionCommitPayload(actionId),
                _combat,
                timeout.Token);
            _awaiting = new AwaitingVerification(
                planId,
                actionId,
                result.RecordedRoot.RootRevision,
                result.Replay.ActionSnapshots[0],
                LocalAgentOwnsReport: true);
            EventPublished?.Invoke(new HostDeploymentEvent(
                "ack", planId, actionId, $"{ack.NativeActionType}:{ack.CompletionState}"));
        }
        catch (Exception exception)
        {
            if (_agent.State is LocalActorAgentState.Prepared or LocalActorAgentState.Reporting)
                _agent.Cancel();
            EventPublished?.Invoke(new HostDeploymentEvent(
                "failed", planId, actionId, exception.Message));
        }
        finally
        {
            _starting = false;
        }
    }

    private void OnRootRecorded(RecordedCombatRoot actual)
    {
        AwaitingVerification? awaiting = _awaiting;
        if (awaiting is null || actual.RootRevision <= awaiting.SourceRootRevision)
            return;
        string? difference = string.Equals(
            awaiting.Expected.Continuation.StateText,
            actual.Root.ContinuationStamp.StateText,
            StringComparison.Ordinal)
                ? null
                : awaiting.Expected.Continuation.DescribeFirstDifference(actual.Root.ContinuationStamp);
        _awaiting = null;
        if (difference is null)
        {
            if (awaiting.LocalAgentOwnsReport)
                _agent.FinishReport(awaiting.ActionId);
            EventPublished?.Invoke(new HostDeploymentEvent(
                "verified", awaiting.PlanId, awaiting.ActionId, "actual/simulated identical"));
            return;
        }
        if (awaiting.LocalAgentOwnsReport)
            _agent.Cancel();
        EventPublished?.Invoke(new HostDeploymentEvent(
            "diverged", awaiting.PlanId, awaiting.ActionId, difference));
    }

    private void OnHostResponseReceived(CoopWireEnvelope envelope, ulong senderNetworkPlayerId)
    {
        RemoteActionStatus? current = _remote.Current;
        if (current is null
            || !string.Equals(current.ActionId, envelope.Header.ActionId, StringComparison.Ordinal))
            return;
        try
        {
            switch (envelope.Kind)
            {
                case CoopMessageKind.ActionPrepared:
                    _remote.Prepared(current.ActionId, senderNetworkPlayerId);
                    _remote.Commit(current.ActionId);
                    _peer.SendCommit(
                        current.OwnerNetworkPlayerId,
                        envelope.Header.RootRevision,
                        envelope.Header.PlanId!,
                        current.ActionId);
                    StartRemoteTimeout(current.ActionId);
                    EventPublished?.Invoke(new HostDeploymentEvent(
                        "commit_sent", envelope.Header.PlanId!, current.ActionId,
                        $"owner={current.OwnerNetworkPlayerId}"));
                    break;
                case CoopMessageKind.ActionAck:
                    _remote.Acknowledge(current.ActionId, senderNetworkPlayerId);
                    CancelRemoteTimeout();
                    HostSearchResult plan = _remotePlan
                        ?? throw new InvalidOperationException("Remote ACK has no retained Host plan.");
                    _awaiting = new AwaitingVerification(
                        plan.Published.PlanId,
                        current.ActionId,
                        plan.RecordedRoot.RootRevision,
                        plan.Replay.ActionSnapshots[0],
                        LocalAgentOwnsReport: false);
                    _remotePlan = null;
                    EventPublished?.Invoke(new HostDeploymentEvent(
                        "ack", plan.Published.PlanId, current.ActionId,
                        envelope.ReadPayload<ActionAckPayload>().CompletionState));
                    break;
                case CoopMessageKind.ActionRejected:
                    CancelRemoteTimeout();
                    ActionRejectedPayload rejected = envelope.ReadPayload<ActionRejectedPayload>();
                    _remote.Reject(current.ActionId, senderNetworkPlayerId, rejected.Detail);
                    _remotePlan = null;
                    EventPublished?.Invoke(new HostDeploymentEvent(
                        "rejected", envelope.Header.PlanId!, current.ActionId,
                        $"{rejected.Code}:{rejected.Detail}"));
                    break;
                case CoopMessageKind.ActionFailed:
                    CancelRemoteTimeout();
                    ActionFailedPayload failed = envelope.ReadPayload<ActionFailedPayload>();
                    _remote.Fail(current.ActionId, senderNetworkPlayerId, failed.Detail);
                    _remotePlan = null;
                    EventPublished?.Invoke(new HostDeploymentEvent(
                        "failed", envelope.Header.PlanId!, current.ActionId,
                        $"{failed.FailureCode}:{failed.Detail}"));
                    break;
            }
        }
        catch (Exception exception)
        {
            CancelRemoteTimeout();
            _remotePlan = null;
            EventPublished?.Invoke(new HostDeploymentEvent(
                "failed", envelope.Header.PlanId ?? "-", current.ActionId, exception.Message));
        }
    }

    private void StartRemoteTimeout(string actionId)
    {
        _remoteDeadlineMilliseconds = Environment.TickCount64 + 10_000;
    }

    private void CancelRemoteTimeout()
    {
        _remoteDeadlineMilliseconds = 0;
    }
}
