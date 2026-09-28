namespace CombatSolver;

/// <summary>
/// Offline joint plan contract. The existing single-player route still uses <see cref="PlanAction[]"/>
/// directly; this wrapper is the first boundary at which an Actor count becomes mandatory.
/// </summary>
internal sealed record JointPlan(
    int ActorCount,
    IReadOnlyList<PlanAction> Actions)
{
    internal void Validate()
    {
        if (ActorCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(ActorCount), ActorCount, "联合计划必须至少包含一个 Actor。");
        if (Actions is null)
            throw new ArgumentNullException(nameof(Actions));

        foreach (PlanAction action in Actions)
        {
            action.ValidateActor(ActorCount);
            ValidateChoiceOwner(action, action.Choice);
            foreach (PlanCardChoice choice in action.NestedChoices ?? [])
                ValidateChoiceOwner(action, choice);
            foreach (PlanCardChoice choice in action.TurnStartChoices ?? [])
                ValidateChoiceOwner(action, choice);
        }
    }

    private static void ValidateChoiceOwner(PlanAction action, PlanCardChoice? choice)
    {
        if (choice is null)
            return;
        if (choice.Actor != action.Actor)
        {
            throw new InvalidOperationException(
                $"动作 {action.Kind} 的选牌 Actor {choice.Actor} 与动作 Actor {action.Actor} 不一致。");
        }
    }
}
