namespace CombatSolver;

internal sealed record JointBeamRetentionCandidate<T>(
    T Value,
    JointObjectiveScore Score,
    IReadOnlyList<PlanAction> Actions);

internal static class JointBeamRetentionPolicy
{
    internal static IReadOnlyList<JointBeamRetentionCandidate<T>> Select<T>(
        IReadOnlyList<JointBeamRetentionCandidate<T>> candidates,
        int width)
    {
        if (width <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        JointBeamRetentionCandidate<T>[] ranked = candidates
            .OrderByDescending(static candidate => candidate.Score, ScoreComparer.Instance)
            .ThenBy(static candidate => candidate.Actions, ActionComparer.Instance)
            .ToArray();
        if (ranked.Length <= width)
            return ranked;

        List<JointBeamRetentionCandidate<T>> retained = new(width);
        HashSet<JointBeamRetentionCandidate<T>> selected = [];
        Add(ranked[0]);
        foreach (IGrouping<CombatActorId, JointBeamRetentionCandidate<T>> actorGroup in ranked
                     .Where(static candidate => candidate.Actions.Count > 0)
                     .GroupBy(static candidate => candidate.Actions[^1].Actor)
                     .OrderBy(static group => group.Key.Index))
        {
            Add(actorGroup.First());
        }
        Add(ranked.FirstOrDefault(static candidate =>
            candidate.Actions.All(static action => action.Kind != PlanActionKind.UsePotion)));
        Add(ranked.FirstOrDefault(static candidate =>
            candidate.Actions.Any(static action => action.Kind == PlanActionKind.UsePotion)));
        foreach (JointBeamRetentionCandidate<T> candidate in ranked)
        {
            if (!ranked.Any(other => !ReferenceEquals(other, candidate)
                    && Dominates(other.Score, candidate.Score)))
            {
                Add(candidate);
            }
        }
        foreach (JointBeamRetentionCandidate<T> candidate in ranked)
            Add(candidate);
        return retained;

        void Add(JointBeamRetentionCandidate<T>? candidate)
        {
            if (candidate is null || retained.Count >= width || !selected.Add(candidate))
                return;
            retained.Add(candidate);
        }
    }

    private static bool Dominates(JointObjectiveScore left, JointObjectiveScore right)
    {
        if (left.Outcome != right.Outcome)
            return false;
        bool noWorse = left.DeadActorCount <= right.DeadActorCount
            && left.TotalHpLost <= right.TotalHpLost
            && left.PotionStrategicCost <= right.PotionStrategicCost
            && left.DeathSaveUses <= right.DeathSaveUses
            && left.GrowthRewards >= right.GrowthRewards
            && left.LongTermResourceValue >= right.LongTermResourceValue
            && left.OutstandingStolenResource <= right.OutstandingStolenResource;
        bool strict = left.DeadActorCount < right.DeadActorCount
            || left.TotalHpLost < right.TotalHpLost
            || left.PotionStrategicCost < right.PotionStrategicCost
            || left.DeathSaveUses < right.DeathSaveUses
            || left.GrowthRewards > right.GrowthRewards
            || left.LongTermResourceValue > right.LongTermResourceValue
            || left.OutstandingStolenResource < right.OutstandingStolenResource;
        return noWorse && strict;
    }

    private sealed class ScoreComparer : IComparer<JointObjectiveScore>
    {
        internal static ScoreComparer Instance { get; } = new();
        public int Compare(JointObjectiveScore? left, JointObjectiveScore? right)
            => left is null ? right is null ? 0 : -1
                : right is null ? 1 : JointObjectiveScore.Compare(left, right);
    }

    private sealed class ActionComparer : IComparer<IReadOnlyList<PlanAction>>
    {
        internal static ActionComparer Instance { get; } = new();
        public int Compare(IReadOnlyList<PlanAction>? left, IReadOnlyList<PlanAction>? right)
            => left is null ? right is null ? 0 : -1
                : right is null ? 1 : CompareCore(left, right);

        private static int CompareCore(IReadOnlyList<PlanAction> left, IReadOnlyList<PlanAction> right)
        {
            int count = Math.Min(left.Count, right.Count);
            for (int index = 0; index < count; index++)
            {
                PlanAction l = left[index];
                PlanAction r = right[index];
                int comparison = l.Actor.Index.CompareTo(r.Actor.Index);
                if (comparison != 0) return comparison;
                comparison = l.Kind.CompareTo(r.Kind);
                if (comparison != 0) return comparison;
                comparison = string.CompareOrdinal(l.CardId, r.CardId);
                if (comparison != 0) return comparison;
                comparison = l.CardOccurrence.CompareTo(r.CardOccurrence);
                if (comparison != 0) return comparison;
                comparison = Nullable.Compare(l.TargetCombatId, r.TargetCombatId);
                if (comparison != 0) return comparison;
            }
            return left.Count.CompareTo(right.Count);
        }
    }
}
