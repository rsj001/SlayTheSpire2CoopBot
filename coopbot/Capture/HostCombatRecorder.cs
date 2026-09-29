using System.Security.Cryptography;
using System.Text;
using CombatSolver;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Runs;

namespace CoopBot.Capture;

internal sealed record RecordedCombatRoot(
    long RootRevision,
    string Fingerprint,
    CombatRootSnapshot Root,
    HostVisibilityAuditResult Visibility);

internal sealed record RecorderEvent(
    long Sequence,
    string Kind,
    string Detail);

internal sealed class HostCombatRecorder
{
    private const int MaximumEvents = 256;
    private readonly CombatState _combat;
    private readonly string _combatIdentity;
    private readonly Func<bool> _hasModActionInFlight;
    private readonly StableRootGate _gate = new();
    private readonly List<RecorderEvent> _events = [];
    private readonly ulong[] _roster;
    private long _eventSequence;
    private bool _disposed;

    internal HostCombatRecorder(
        CombatState combat,
        string combatIdentity,
        Func<bool> hasModActionInFlight)
    {
        _combat = combat;
        _combatIdentity = combatIdentity;
        _hasModActionInFlight = hasModActionInFlight;
        _roster = combat.Players.Select(player => player.NetId).ToArray();
        Record("recorder_started", $"actors={_roster.Length}");
    }

    internal RecordedCombatRoot? Current { get; private set; }
    internal IReadOnlyList<RecorderEvent> Events => _events;
    internal event Action<RecordedCombatRoot>? RootRecorded;

    internal void Poll()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ContinuationStamp stamp = ContinuationStamp.CaptureLive(_combat);
        string fingerprint = Fingerprint(stamp.StateText);
        RootStabilitySample sample = new(
            _combatIdentity,
            fingerprint,
            CombatManager.Instance.IsInProgress && !CombatManager.Instance.IsEnding,
            _combat.CurrentSide == CombatSide.Player,
            RunManager.Instance.ActionQueueSet.IsEmpty,
            !RunManager.Instance.ActionExecutor.IsRunning
                && RunManager.Instance.ActionExecutor.CurrentlyRunningAction is null,
            !HasActiveChoiceTransaction(),
            !_hasModActionInFlight(),
            _combat.Players.Select(player => player.NetId).SequenceEqual(_roster));
        StableRootGateResult result = _gate.Observe(sample);
        if (result.Disposition != StableRootGateDisposition.Publish)
            return;

        CombatRootSnapshot root = CombatRootSnapshot.Capture(_combat);
        if (!string.Equals(root.ContinuationStamp.StateText, stamp.StateText, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Live combat changed between the stable observation and root capture: "
                + stamp.DescribeFirstDifference(root.ContinuationStamp));
        }
        HostVisibilityAuditResult visibility = HostVisibilityAudit.Inspect(_combat, root);
        if (!visibility.IsComplete)
        {
            throw new InvalidOperationException(
                "Host visibility audit failed: " + string.Join(',', visibility.Failures));
        }

        Current = new RecordedCombatRoot(result.RootRevision, fingerprint, root, visibility);
        Record("root_recorded", $"revision={result.RootRevision} fingerprint={fingerprint}");
        RootRecorded?.Invoke(Current);
    }

    internal void Dispose(string reason)
    {
        if (_disposed) return;
        _disposed = true;
        _gate.Reset();
        Current = null;
        Record("recorder_stopped", reason);
    }

    private bool HasActiveChoiceTransaction()
        => CardSelectCmd.Selector is not null
           || NPlayerHand.Instance?.IsInCardSelection == true
           || NOverlayStack.Instance?.Peek() is NChooseACardSelectionScreen
           || NOverlayStack.Instance?.Peek() is NSimpleCardSelectScreen;

    private void Record(string kind, string detail)
    {
        if (_events.Count == MaximumEvents)
            _events.RemoveAt(0);
        _events.Add(new RecorderEvent(++_eventSequence, kind, detail));
    }

    private static string Fingerprint(string stateText)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stateText)));
}
