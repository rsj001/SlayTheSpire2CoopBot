using CombatSolver;
using CoopBot.Capture;
using CoopBot.NativeAdapter;
using CoopBot.Protocol;
using MegaCrit.Sts2.Core.Combat;

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
        JointCombatSnapshot Expected);

    private readonly CombatState _combat;
    private readonly HostCombatRecorder _recorder;
    private readonly HostSearchCoordinator _search;
    private readonly LocalActorAgent _agent;
    private AwaitingVerification? _awaiting;
    private bool _starting;
    private bool _disposed;

    internal HostDeploymentCoordinator(
        CombatState combat,
        HostCombatRecorder recorder,
        HostSearchCoordinator search,
        LocalActorAgent agent)
    {
        _combat = combat;
        _recorder = recorder;
        _search = search;
        _agent = agent;
        _recorder.RootRecorded += OnRootRecorded;
    }

    internal event Action<HostDeploymentEvent>? EventPublished;

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
            if (action.Actor != result.RecordedRoot.Root.LocalActorId)
                throw new InvalidOperationException(
                    $"C5 only executes the Host local actor; next owner is {action.Actor}.");
            actionId = $"{planId}:0";
            ActionPreparePayload command = HostActionCommandFactory.Create(result, actionIndex: 0);
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
                result.Replay.ActionSnapshots[0]);
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
            _agent.FinishReport(awaiting.ActionId);
            EventPublished?.Invoke(new HostDeploymentEvent(
                "verified", awaiting.PlanId, awaiting.ActionId, "actual/simulated identical"));
            return;
        }
        _agent.Cancel();
        EventPublished?.Invoke(new HostDeploymentEvent(
            "diverged", awaiting.PlanId, awaiting.ActionId, difference));
    }
}
