using System.Collections.Concurrent;
using CombatSolver;
using CoopBot.Capture;
using CoopBot.Protocol;

namespace CoopBot.Host;

internal sealed record HostSearchPolicy(
    int MaximumActions,
    int MaximumStates,
    int BeamWidth,
    int DegreeOfParallelism)
{
    internal static HostSearchPolicy Default { get; } = new(
        MaximumActions: 12,
        MaximumStates: 100_000,
        BeamWidth: 512,
        DegreeOfParallelism: Math.Clamp(Environment.ProcessorCount, 1, 4));
}

internal sealed record HostSearchResult(
    RecordedCombatRoot RecordedRoot,
    JointOfflineSearchResult Search,
    JointReplayResult Replay,
    PlanPublishedPayload Published);

internal sealed record HostSearchFailure(
    long RootRevision,
    string RootFingerprint,
    Exception Error);

internal sealed class HostSearchCoordinator : IDisposable
{
    private readonly HostSearchPolicy _policy;
    private readonly ConcurrentQueue<object> _completed = new();
    private readonly object _gate = new();
    private CancellationTokenSource? _searchCancellation;
    private long _generation;
    private bool _disposed;

    internal HostSearchCoordinator(HostCombatRecorder recorder, HostSearchPolicy? policy = null)
    {
        Recorder = recorder;
        _policy = policy ?? HostSearchPolicy.Default;
        Recorder.RootRecorded += OnRootRecorded;
    }

    internal HostCombatRecorder Recorder { get; }
    internal HostSearchPolicy Policy => _policy;
    internal HostSearchResult? Current { get; private set; }
    internal bool IsSearching { get; private set; }
    internal event Action<HostSearchResult>? PlanPublished;
    internal event Action<HostSearchFailure>? SearchFailed;

    internal void Poll()
    {
        while (_completed.TryDequeue(out object? completion))
        {
            switch (completion)
            {
                case HostSearchResult result when IsCurrent(result.RecordedRoot.RootRevision):
                    Current = result;
                    IsSearching = false;
                    PlanPublished?.Invoke(result);
                    break;
                case HostSearchFailure failure when IsCurrent(failure.RootRevision):
                    Current = null;
                    IsSearching = false;
                    SearchFailed?.Invoke(failure);
                    break;
            }
        }
    }

    internal void Cancel(string reason)
    {
        lock (_gate)
        {
            _generation++;
            _searchCancellation?.Cancel();
            _searchCancellation?.Dispose();
            _searchCancellation = null;
            Current = null;
            IsSearching = false;
        }
    }

    internal void RestartCurrent()
    {
        RecordedCombatRoot recorded = Recorder.Current
            ?? throw new InvalidOperationException("Cannot replan before a stable Host root exists.");
        OnRootRecorded(recorded);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Recorder.RootRecorded -= OnRootRecorded;
        Cancel("disposed");
    }

    private void OnRootRecorded(RecordedCombatRoot recorded)
    {
        CancellationToken token;
        long generation;
        lock (_gate)
        {
            if (_disposed) return;
            _generation++;
            generation = _generation;
            _searchCancellation?.Cancel();
            _searchCancellation?.Dispose();
            _searchCancellation = new CancellationTokenSource();
            token = _searchCancellation.Token;
            Current = null;
            IsSearching = true;
        }

        _ = Task.Run(() => Search(recorded, generation, token), CancellationToken.None);
    }

    private void Search(RecordedCombatRoot recorded, long generation, CancellationToken token)
    {
        try
        {
            JointOfflineSearchRequest request = JointOfflineSearchRequest.Default(
                _policy.MaximumActions,
                _policy.MaximumStates);
            JointOfflineSearchResult searched = JointOfflineSearch.SolveBeam(
                recorded.Root,
                request,
                _policy.BeamWidth,
                token,
                _policy.DegreeOfParallelism);
            token.ThrowIfCancellationRequested();
            JointReplayResult replay = JointStrictReplayVerifier.Verify(recorded.Root, searched);
            token.ThrowIfCancellationRequested();
            PlanPublishedPayload published = CoopPlanSnapshotFactory.Create(
                recorded.RootRevision,
                recorded.Fingerprint,
                recorded.Root,
                searched,
                replay);
            if (IsGeneration(generation))
                _completed.Enqueue(new HostSearchResult(recorded, searched, replay, published));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (IsGeneration(generation))
                _completed.Enqueue(new HostSearchFailure(
                    recorded.RootRevision,
                    recorded.Fingerprint,
                    exception));
        }
    }

    private bool IsCurrent(long rootRevision)
    {
        lock (_gate)
            return !_disposed && Recorder.Current?.RootRevision == rootRevision;
    }

    private bool IsGeneration(long generation)
    {
        lock (_gate)
            return !_disposed && _generation == generation;
    }
}
