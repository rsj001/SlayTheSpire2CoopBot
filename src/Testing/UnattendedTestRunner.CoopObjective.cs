namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private static void AssertCoopObjectiveContract()
    {
        JointObjectiveScore zeroLoss = new(true, 0, [0, 0], 1, 2, 3);
        JointObjectiveScore actorOneLoss = new(true, 3, [0, 3], 0, 2, 2);
        JointObjectiveScore defeat = new(false, 0, [0, 0], 0, 1, 1);
        JointObjectiveScore morePotions = new(true, 0, [0, 0], 2, 2, 3);
        JointObjectiveScore moreTurns = new(true, 0, [0, 0], 1, 3, 3);
        JointObjectiveScore moreActions = new(true, 0, [0, 0], 1, 2, 4);
        if (JointObjectiveScore.Compare(zeroLoss, actorOneLoss) <= 0
            || JointObjectiveScore.Compare(actorOneLoss, defeat) <= 0
            || JointObjectiveScore.Compare(zeroLoss, new JointObjectiveScore(true, 0, [1, 0], 1, 2, 3)) <= 0
            || JointObjectiveScore.Compare(zeroLoss, morePotions) <= 0
            || JointObjectiveScore.Compare(zeroLoss, moreTurns) <= 0
            || JointObjectiveScore.Compare(zeroLoss, moreActions) <= 0)
            throw new InvalidOperationException("联合队伍目标的胜负、战损分布、药水、回合或动作排序错误。");
    }
}
