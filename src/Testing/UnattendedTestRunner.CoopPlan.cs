namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private static void AssertCoopActorPlanContract()
    {
        new JointPlan(1, [new PlanAction(PlanActionKind.EndTurn, 1)]).Validate();

        CombatActorId second = new(1);
        PlanCardChoice secondChoice = new(
            PlanChoiceEffect.Discard,
            MegaCrit.Sts2.Core.Entities.Cards.PileType.Hand,
            [],
            Actor: second);
        new JointPlan(2,
        [
            new PlanAction(
                PlanActionKind.PlayCard,
                1,
                CardId: "TEST.COOP.ACTOR.ONE",
                Choice: secondChoice,
                Actor: second),
        ]).Validate();

        AssertInvalidJointPlan(
            new JointPlan(2, [new PlanAction(PlanActionKind.EndTurn, 1, Actor: new CombatActorId(2))]),
            "范围外 Actor 未被拒绝。");
        AssertInvalidJointPlan(
            new JointPlan(2,
            [
                new PlanAction(
                    PlanActionKind.PlayCard,
                    1,
                    CardId: "TEST.COOP.CHOICE.OWNER",
                    Choice: secondChoice with { Actor = default },
                    Actor: second),
            ]),
            "跨 Actor 选牌归属未被拒绝。");
    }

    private static void AssertInvalidJointPlan(JointPlan plan, string message)
    {
        try
        {
            plan.Validate();
        }
        catch (InvalidOperationException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static void AssertCoopJointTurnBarrier()
    {
        CombatActorId actor0 = new(0);
        CombatActorId actor1 = new(1);
        JointTurnState state = JointTurnState.Start(2);
        if (!state.IsActionable(actor0) || !state.IsActionable(actor1) || state.IsBarrierReached)
            throw new InvalidOperationException("联合回合初始可行动状态错误。");
        state = state.EndTurn(actor1);
        if (state.IsBarrierReached || !state.IsActionable(actor0))
            throw new InvalidOperationException("单个 Actor 结束回合错误地越过了全员屏障。");
        state = state.EndTurn(actor0);
        if (!state.IsBarrierReached)
            throw new InvalidOperationException("全员结束回合后未到达屏障。");
        state = state.AdvanceTurn();
        if (state.Turn != 2 || !state.IsActionable(actor0) || !state.IsActionable(actor1))
            throw new InvalidOperationException("联合回合屏障推进后阶段错误。");
        state = state.EndTurn(actor0);
        state = state.MarkDead(actor1);
        if (!state.IsBarrierReached)
            throw new InvalidOperationException("死亡 Actor 未被视为已通过屏障。");

        JointPlan valid = new(2,
        [
            new PlanAction(PlanActionKind.PlayCard, 1, CardId: "A0", Actor: actor0),
            new PlanAction(PlanActionKind.EndTurn, 1, Actor: actor1),
            new PlanAction(PlanActionKind.EndTurn, 1, Actor: actor0),
            new PlanAction(PlanActionKind.EndTurn, 2, Actor: actor0),
            new PlanAction(PlanActionKind.EndTurn, 2, Actor: actor1),
        ]);
        valid.ValidateComplete();

        AssertInvalidJointPlan(
            new JointPlan(2,
            [
                new PlanAction(PlanActionKind.EndTurn, 1, Actor: actor1),
                new PlanAction(PlanActionKind.PlayCard, 2, CardId: "EARLY", Actor: actor0),
            ]),
            "未完成全员屏障就进入下一回合。");
    }
}
