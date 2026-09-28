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
}
