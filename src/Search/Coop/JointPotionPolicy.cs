using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal readonly record struct JointPotionSlotDirective(
    CombatActorId Actor,
    int Slot,
    string PotionId,
    SolverPotionDirective Directive);

internal sealed class JointPotionSearchPolicy
{
    private readonly Dictionary<(CombatActorId Actor, int Slot, string PotionId), SolverPotionDirective>
        _directives;

    internal JointPotionSearchPolicy(
        SolverPotionPolicy defaultPolicy = SolverPotionPolicy.Smart,
        IEnumerable<JointPotionSlotDirective>? directives = null,
        int minimumUses = 0,
        int? maximumUses = null)
    {
        if (minimumUses < 0)
            throw new ArgumentOutOfRangeException(nameof(minimumUses));
        if (maximumUses is < 0 || maximumUses is { } maximum && minimumUses > maximum)
            throw new ArgumentOutOfRangeException(nameof(maximumUses));
        DefaultPolicy = defaultPolicy;
        MinimumUses = minimumUses;
        MaximumUses = maximumUses;
        JointPotionSlotDirective[] entries = directives?.ToArray() ?? [];
        _directives = entries.ToDictionary(
            static directive => (directive.Actor, directive.Slot, directive.PotionId),
            static directive => directive.Directive);
        Directives = entries
            .OrderBy(static directive => directive.Actor.Index)
            .ThenBy(static directive => directive.Slot)
            .ThenBy(static directive => directive.PotionId, StringComparer.Ordinal)
            .ToArray();
    }

    internal static JointPotionSearchPolicy Unrestricted { get; } = new();

    internal SolverPotionPolicy DefaultPolicy { get; }
    internal int MinimumUses { get; }
    internal int? MaximumUses { get; }
    internal IReadOnlyList<JointPotionSlotDirective> Directives { get; }
    internal bool RequiresSmartCounterfactual
        => DefaultPolicy == SolverPotionPolicy.Smart
            || Directives.Any(static directive => directive.Directive == SolverPotionDirective.Smart);

    internal JointPotionSearchPolicy ForSmartBaseline()
        => new(
            SolverPotionPolicy.Disabled,
            Directives.Select(static directive => directive.Directive == SolverPotionDirective.Smart
                ? directive with { Directive = SolverPotionDirective.Disabled }
                : directive),
            MinimumUses,
            MaximumUses);

    internal bool Allows(PlanAction action, IReadOnlyList<PlanAction> priorActions)
    {
        if (action.Kind != PlanActionKind.UsePotion)
            return true;
        int uses = CountUses(priorActions);
        if (MaximumUses is { } maximum && uses >= maximum)
            return false;
        SolverPotionDirective directive = Resolve(
            action.Actor,
            action.PotionSlot,
            action.PotionId ?? throw new InvalidOperationException("联合药水动作缺少 PotionId。"));
        return directive != SolverPotionDirective.Disabled;
    }

    internal bool IsBoundaryEligible(IReadOnlyList<PlanAction> actions)
    {
        int uses = CountUses(actions);
        if (uses < MinimumUses || MaximumUses is { } maximum && uses > maximum)
            return false;
        foreach (JointPotionSlotDirective directive in Directives)
        {
            if (directive.Directive != SolverPotionDirective.Force)
                continue;
            if (!actions.Any(action => action.Kind == PlanActionKind.UsePotion
                    && action.Actor == directive.Actor
                    && action.PotionSlot == directive.Slot
                    && string.Equals(action.PotionId, directive.PotionId, StringComparison.Ordinal)))
            {
                return false;
            }
        }
        return true;
    }

    internal int StrategicHpCost(IReadOnlyList<PlanAction> actions, bool renewablePotionShapedRock)
        => actions
            .Where(static action => action.Kind == PlanActionKind.UsePotion)
            .Sum(action => PotionUsePolicy.StrategicHpCost(
                action.PotionId ?? throw new InvalidOperationException("联合药水动作缺少 PotionId。"),
                renewablePotionShapedRock));

    internal bool IsSmartCandidateEligible(
        JointOfflineSearchResult baseline,
        JointOfflineSearchResult candidate,
        IReadOnlyList<CombatActorRoot> actors)
    {
        PlanAction[] optionalUses = candidate.Actions
            .Where(action => action.Kind == PlanActionKind.UsePotion
                && Resolve(
                    action.Actor,
                    action.PotionSlot,
                    action.PotionId
                        ?? throw new InvalidOperationException("联合药水动作缺少 PotionId。"))
                    == SolverPotionDirective.Smart)
            .ToArray();
        if (optionalUses.Length == 0)
            return true;
        int strategicCost = optionalUses.Sum(action => PotionUsePolicy.StrategicHpCost(
            action.PotionId!,
            actors[action.Actor.Index].HasRenewablePotionShapedRock));
        bool baselineWon = baseline.Score.Outcome == CombatTerminalOutcome.Victory;
        bool candidateWon = candidate.Score.Outcome == CombatTerminalOutcome.Victory;
        return candidateWon && !baselineWon
            || PotionUsePolicy.HpSaved(
                    baseline.Score.TotalHpLost,
                    candidate.Score.TotalHpLost)
                >= PotionUsePolicy.SmartRequiredHpSaved(strategicCost);
    }

    internal void Validate(int actorCount)
    {
        foreach (JointPotionSlotDirective directive in Directives)
        {
            if (directive.Actor.Index < 0 || directive.Actor.Index >= actorCount)
                throw new ArgumentOutOfRangeException(nameof(directive.Actor));
            if (directive.Slot < 0)
                throw new ArgumentOutOfRangeException(nameof(directive.Slot));
            if (string.IsNullOrWhiteSpace(directive.PotionId))
                throw new ArgumentException("联合药水指令缺少 PotionId。", nameof(Directives));
        }
    }

    private SolverPotionDirective Resolve(CombatActorId actor, int slot, string potionId)
        => _directives.GetValueOrDefault(
            (actor, slot, potionId),
            DefaultPolicy == SolverPotionPolicy.Disabled
                ? SolverPotionDirective.Disabled
                : SolverPotionDirective.Smart);

    private static int CountUses(IReadOnlyList<PlanAction> actions)
        => actions.Count(static action => action.Kind == PlanActionKind.UsePotion);
}
