namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private static void AssertCoopOracleContracts()
    {
        OfflineJointFixture twoActor = new(
            7,
            [
                [new OfflineJointAction(new CombatActorId(0), "Strike", 1, 3)],
                [new OfflineJointAction(new CombatActorId(1), "Bolt", 1, 4)],
            ]);
        OfflineJointOracleResult two = JointExhaustiveOracle.Solve(twoActor);
        if (!two.Victory || two.RemainingEnemyHp != 0
            || !two.Actions.Any(action => action.Actor.Index == 0 && action.Id == "Strike")
            || !two.Actions.Any(action => action.Actor.Index == 1 && action.Id == "Bolt")
            || two.ExpandedStates <= 0)
            throw new InvalidOperationException("两 Actor 穷举 oracle 未找到固定 fixture 的全局最优胜利路线。");

        OfflineJointFixture fourActor = new(
            18,
            [
                [new OfflineJointAction(new CombatActorId(0), "A0", 1, 3)],
                [new OfflineJointAction(new CombatActorId(1), "A1", 1, 4)],
                [new OfflineJointAction(new CombatActorId(2), "A2", 1, 5)],
                [new OfflineJointAction(new CombatActorId(3), "A3", 1, 6)],
            ]);
        OfflineJointOracleResult four = JointExhaustiveOracle.Solve(fourActor);
        if (!four.Victory || four.RemainingEnemyHp != 0
            || four.Actions.Select(action => action.Actor.Index).Distinct().Count() != 4)
            throw new InvalidOperationException("四 Actor 扩展未找到固定 fixture 的全局最优胜利路线。");
    }
}
