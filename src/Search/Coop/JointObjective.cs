using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed record JointObjectiveScore(
    bool Victory,
    int TotalHpLost,
    IReadOnlyList<int> HpLostByActor,
    int PotionUses,
    int Turns,
    int Actions)
{
    internal static int Compare(JointObjectiveScore left, JointObjectiveScore right)
    {
        int comparison = left.Victory.CompareTo(right.Victory);
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
        comparison = right.PotionUses.CompareTo(left.PotionUses);
        if (comparison != 0)
            return comparison;
        comparison = right.Turns.CompareTo(left.Turns);
        return comparison != 0 ? comparison : right.Actions.CompareTo(left.Actions);
    }
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
        foreach (JointActorSnapshot actor in snapshot.Actors)
        {
            CombatActorRoot initial = root.Actors[actor.Id.Index];
            int loss = Math.Max(0, initial.InitialHp - actor.Hp);
            losses.Add(loss);
            total = checked(total + loss);
        }
        return new JointObjectiveScore(
            snapshot.Simulator.TerminalStamp is { Outcome: CombatTerminalOutcome.Victory },
            total,
            losses,
            actions.Count(action => action.Kind == PlanActionKind.UsePotion),
            snapshot.Turn,
            actions.Count);
    }
}
