using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed record JointObjectiveScore(
    CombatTerminalOutcome? Outcome,
    int DeadActorCount,
    int TotalHpLost,
    IReadOnlyList<int> HpLostByActor,
    int PotionStrategicCost,
    int PotionUses,
    int DeathSaveUses,
    int GrowthRewards,
    int LongTermResourceValue,
    int OutstandingStolenResource,
    int Turns,
    int Actions)
{
    internal static int Compare(JointObjectiveScore left, JointObjectiveScore right)
    {
        int comparison = OutcomeRank(left.Outcome).CompareTo(OutcomeRank(right.Outcome));
        if (comparison != 0)
            return comparison;
        comparison = right.DeadActorCount.CompareTo(left.DeadActorCount);
        if (comparison != 0)
            return comparison;
        comparison = right.TotalHpLost.CompareTo(left.TotalHpLost);
        if (comparison != 0)
            return comparison;
        int count = Math.Max(left.HpLostByActor.Count, right.HpLostByActor.Count);
        for (int index = 0; index < count; index++)
        {
            int leftLoss = index < left.HpLostByActor.Count ? left.HpLostByActor[index] : int.MaxValue;
            int rightLoss = index < right.HpLostByActor.Count ? right.HpLostByActor[index] : int.MaxValue;
            comparison = rightLoss.CompareTo(leftLoss);
            if (comparison != 0)
                return comparison;
        }
        comparison = right.PotionStrategicCost.CompareTo(left.PotionStrategicCost);
        if (comparison != 0)
            return comparison;
        comparison = right.DeathSaveUses.CompareTo(left.DeathSaveUses);
        if (comparison != 0)
            return comparison;
        comparison = right.PotionUses.CompareTo(left.PotionUses);
        if (comparison != 0)
            return comparison;
        comparison = left.GrowthRewards.CompareTo(right.GrowthRewards);
        if (comparison != 0)
            return comparison;
        comparison = left.LongTermResourceValue.CompareTo(right.LongTermResourceValue);
        if (comparison != 0)
            return comparison;
        comparison = right.OutstandingStolenResource.CompareTo(left.OutstandingStolenResource);
        if (comparison != 0)
            return comparison;
        comparison = right.Turns.CompareTo(left.Turns);
        return comparison != 0 ? comparison : right.Actions.CompareTo(left.Actions);
    }

    private static int OutcomeRank(CombatTerminalOutcome? outcome)
        => outcome switch
        {
            CombatTerminalOutcome.Victory => 2,
            null => 1,
            _ => 0,
        };
}

internal static class JointObjective
{
    internal static JointObjectiveScore Capture(
        CombatRootSnapshot root,
        JointCombatSnapshot snapshot,
        IReadOnlyList<PlanAction> actions)
    {
        List<int> losses = new(snapshot.Actors.Count);
        int total = 0;
        int deadActors = 0;
        foreach (JointActorSnapshot actor in snapshot.Actors)
        {
            CombatActorRoot initial = root.Actors[actor.Id.Index];
            int loss = Math.Max(0, initial.InitialHp - actor.Hp);
            losses.Add(loss);
            total = checked(total + loss);
            if (actor.Hp <= 0)
                deadActors++;
        }
        SimulatedCombatState combat = (SimulatedCombatState)snapshot.Simulator.State.CombatState;
        int potionStrategicCost = actions
            .Where(static action => action.Kind == PlanActionKind.UsePotion)
            .Sum(action => PotionUsePolicy.StrategicHpCost(
                action.PotionId
                    ?? throw new InvalidOperationException("联合药水动作缺少 PotionId。"),
                root.Actors[action.Actor.Index].HasRenewablePotionShapedRock));
        return new JointObjectiveScore(
            snapshot.Simulator.TerminalStamp?.Outcome,
            deadActors,
            total,
            losses,
            potionStrategicCost,
            actions.Count(action => action.Kind == PlanActionKind.UsePotion),
            combat.DeathSaveUseCount,
            combat.GrowthRewards.Total,
            combat.LongTermResourceValue,
            combat.OutstandingStolenResource(snapshot.Simulator),
            snapshot.Turn,
            actions.Count);
    }
}
