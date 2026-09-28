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
                choice.ValidateActor(ActorCount);
        }
        ValidateTurnBarriers(requireFinalBarrier: false);
    }

    internal void ValidateComplete()
    {
        Validate();
        ValidateTurnBarriers(requireFinalBarrier: true);
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

    private void ValidateTurnBarriers(bool requireFinalBarrier)
    {
        JointTurnState state = JointTurnState.Start(ActorCount, Actions.Count == 0 ? 1 : Actions[0].Turn);
        foreach (PlanAction action in Actions)
        {
            if (action.Turn < state.Turn)
                throw new InvalidOperationException($"联合计划动作回合倒退：{action.Turn} < {state.Turn}。");
            while (action.Turn > state.Turn)
            {
                if (!state.IsBarrierReached)
                    throw new InvalidOperationException(
                        $"联合计划在回合 {state.Turn} 未完成全员结束屏障就进入回合 {action.Turn}。");
                state = state.AdvanceTurn();
            }

            if (!state.IsActionable(action.Actor))
                throw new InvalidOperationException(
                    $"联合计划 Actor {action.Actor} 在回合 {action.Turn} 已结束或死亡后仍有动作。");
            if (action.Kind == PlanActionKind.EndTurn || action.EndsPlayerTurn)
                state = state.EndTurn(action.Actor);
        }

        if (requireFinalBarrier && !state.IsBarrierReached)
            throw new InvalidOperationException("联合计划未完成最后一个回合的全员结束屏障。");
    }
}
