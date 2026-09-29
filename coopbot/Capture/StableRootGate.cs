namespace CoopBot.Capture;

public readonly record struct RootStabilitySample(
    string CombatIdentity,
    string Fingerprint,
    bool CombatInProgress,
    bool IsPlayerSide,
    bool NativeQueuesEmpty,
    bool NativeExecutorIdle,
    bool ChoiceTransactionsIdle,
    bool ModActionIdle,
    bool RosterStable)
{
    public bool IsStableBoundary
        => !string.IsNullOrWhiteSpace(CombatIdentity)
           && !string.IsNullOrWhiteSpace(Fingerprint)
           && CombatInProgress
           && IsPlayerSide
           && NativeQueuesEmpty
           && NativeExecutorIdle
           && ChoiceTransactionsIdle
           && ModActionIdle
           && RosterStable;
}

public enum StableRootGateDisposition
{
    Unstable,
    Candidate,
    Unchanged,
    Publish,
}

public readonly record struct StableRootGateResult(
    StableRootGateDisposition Disposition,
    long RootRevision,
    string Reason);

public sealed class StableRootGate
{
    private string? _candidateCombatIdentity;
    private string? _candidateFingerprint;
    private string? _publishedCombatIdentity;
    private string? _publishedFingerprint;
    private int _matchingObservations;

    public StableRootGate(int requiredMatchingObservations = 2)
    {
        if (requiredMatchingObservations < 2)
            throw new ArgumentOutOfRangeException(
                nameof(requiredMatchingObservations),
                "At least two observations are required.");
        RequiredMatchingObservations = requiredMatchingObservations;
    }

    public int RequiredMatchingObservations { get; }
    public long RootRevision { get; private set; }

    public StableRootGateResult Observe(RootStabilitySample sample)
    {
        if (!sample.IsStableBoundary)
        {
            ResetCandidate();
            return new StableRootGateResult(
                StableRootGateDisposition.Unstable,
                RootRevision,
                DescribeUnstable(sample));
        }

        if (!string.Equals(_candidateCombatIdentity, sample.CombatIdentity, StringComparison.Ordinal)
            || !string.Equals(_candidateFingerprint, sample.Fingerprint, StringComparison.Ordinal))
        {
            _candidateCombatIdentity = sample.CombatIdentity;
            _candidateFingerprint = sample.Fingerprint;
            _matchingObservations = 1;
            return new StableRootGateResult(
                StableRootGateDisposition.Candidate,
                RootRevision,
                "first_matching_observation");
        }

        _matchingObservations++;
        if (_matchingObservations < RequiredMatchingObservations)
        {
            return new StableRootGateResult(
                StableRootGateDisposition.Candidate,
                RootRevision,
                $"matching_observations={_matchingObservations}");
        }

        if (string.Equals(_publishedCombatIdentity, sample.CombatIdentity, StringComparison.Ordinal)
            && string.Equals(_publishedFingerprint, sample.Fingerprint, StringComparison.Ordinal))
        {
            return new StableRootGateResult(
                StableRootGateDisposition.Unchanged,
                RootRevision,
                "already_published");
        }

        _publishedCombatIdentity = sample.CombatIdentity;
        _publishedFingerprint = sample.Fingerprint;
        RootRevision++;
        return new StableRootGateResult(
            StableRootGateDisposition.Publish,
            RootRevision,
            "stable_root_changed");
    }

    public void Reset()
    {
        ResetCandidate();
        _publishedCombatIdentity = null;
        _publishedFingerprint = null;
        RootRevision = 0;
    }

    private void ResetCandidate()
    {
        _candidateCombatIdentity = null;
        _candidateFingerprint = null;
        _matchingObservations = 0;
    }

    private static string DescribeUnstable(RootStabilitySample sample)
    {
        List<string> reasons = [];
        if (!sample.CombatInProgress) reasons.Add("combat_not_in_progress");
        if (!sample.IsPlayerSide) reasons.Add("not_player_side");
        if (!sample.NativeQueuesEmpty) reasons.Add("native_queues_nonempty");
        if (!sample.NativeExecutorIdle) reasons.Add("native_executor_active");
        if (!sample.ChoiceTransactionsIdle) reasons.Add("choice_transaction_active");
        if (!sample.ModActionIdle) reasons.Add("mod_action_in_flight");
        if (!sample.RosterStable) reasons.Add("roster_changed");
        if (string.IsNullOrWhiteSpace(sample.CombatIdentity)) reasons.Add("combat_identity_missing");
        if (string.IsNullOrWhiteSpace(sample.Fingerprint)) reasons.Add("fingerprint_missing");
        return string.Join(',', reasons);
    }
}
