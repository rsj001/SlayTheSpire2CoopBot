namespace CoopBot.Session;

public enum HostRootChangeKind
{
    Ignored,
    ManualInsertion,
    ExpectedActionResult,
}

public sealed record HostRootChange(
    HostRootChangeKind Kind,
    string? PlanId,
    string? ActionId,
    long SourceRootRevision,
    long ActualRootRevision);

public sealed class HostPlanLease
{
    public string? PlanId { get; private set; }
    public string? ActionId { get; private set; }
    public long RootRevision { get; private set; }

    public void Publish(string planId, long rootRevision)
    {
        if (ActionId is not null)
            throw new InvalidOperationException($"Cannot replace plan {PlanId} while {ActionId} is in flight.");
        PlanId = string.IsNullOrWhiteSpace(planId)
            ? throw new ArgumentException("PlanId is required.", nameof(planId))
            : planId;
        RootRevision = rootRevision > 0
            ? rootRevision
            : throw new ArgumentOutOfRangeException(nameof(rootRevision), rootRevision, "RootRevision must be positive.");
    }

    public void BeginAction(string planId, string actionId)
    {
        if (!string.Equals(PlanId, planId, StringComparison.Ordinal))
            throw new InvalidOperationException($"Action plan {planId} does not match lease {PlanId ?? "-"}.");
        if (ActionId is not null)
            throw new InvalidOperationException($"Action {ActionId} is already in flight.");
        ActionId = string.IsNullOrWhiteSpace(actionId)
            ? throw new ArgumentException("ActionId is required.", nameof(actionId))
            : actionId;
    }

    public HostRootChange ObserveRoot(long actualRootRevision)
    {
        if (PlanId is null || actualRootRevision <= RootRevision)
            return new HostRootChange(HostRootChangeKind.Ignored, PlanId, ActionId, RootRevision, actualRootRevision);
        HostRootChange result = new(
            ActionId is null ? HostRootChangeKind.ManualInsertion : HostRootChangeKind.ExpectedActionResult,
            PlanId,
            ActionId,
            RootRevision,
            actualRootRevision);
        Clear();
        return result;
    }

    public void Cancel(string? actionId = null)
    {
        if (actionId is not null && ActionId is not null
            && !string.Equals(ActionId, actionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Cannot cancel action {ActionId} with {actionId}.");
        }
        Clear();
    }

    private void Clear()
    {
        PlanId = null;
        ActionId = null;
        RootRevision = 0;
    }
}
