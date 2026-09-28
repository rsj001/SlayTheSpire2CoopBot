using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private static void AssertCoopObjectiveContract()
    {
        JointObjectiveScore baseline = Score();
        RequireBetter(Score(outcome: CombatTerminalOutcome.Victory), baseline, "胜利");
        RequireBetter(baseline, Score(outcome: CombatTerminalOutcome.Defeat), "未完成边界");
        RequireBetter(baseline, Score(dead: 1), "全队存活");
        RequireBetter(baseline, Score(total: 1), "总战损");
        RequireBetter(Score(total: 2, losses: [0, 2]), Score(total: 2, losses: [1, 1]), "逐 Actor 战损");
        RequireBetter(baseline, Score(potionCost: 1), "药水战略成本");
        RequireBetter(baseline, Score(deathSaves: 1), "保命资源");
        RequireBetter(baseline, Score(potionUses: 1), "药水次数");
        RequireBetter(Score(growth: 1), baseline, "成长收益");
        RequireBetter(Score(longTerm: 1), baseline, "长期资源");
        RequireBetter(baseline, Score(stolen: 1), "偷窃回收");
        RequireBetter(baseline, Score(turns: 2), "回合数");
        RequireBetter(baseline, Score(actions: 1), "动作数");
        JointBeamRetentionCandidate<string>[] retentionCandidates =
        [
            new("best", baseline, [new(PlanActionKind.EndTurn, 1, Actor: new CombatActorId(0))]),
            new("actor1", Score(total: 1), [new(PlanActionKind.EndTurn, 1, Actor: new CombatActorId(1))]),
            new("potion", Score(total: 2, potionUses: 1),
                [new(PlanActionKind.UsePotion, 1, PotionSlot: 0, PotionId: "BLOCK_POTION", Actor: new CombatActorId(0))]),
            new("growth", Score(total: 3, growth: 2),
                [new(PlanActionKind.EndTurn, 1, Actor: new CombatActorId(0))]),
        ];
        IReadOnlyList<JointBeamRetentionCandidate<string>> retained =
            JointBeamRetentionPolicy.Select(retentionCandidates, width: 4);
        HashSet<string> values = retained
            .Select(static candidate => candidate.Value)
            .ToHashSet(StringComparer.Ordinal);
        if (values.Count != 4 || !values.SetEquals(["best", "actor1", "potion", "growth"]))
        {
            throw new InvalidOperationException("联合 Beam 未保留 Actor、药水或 Pareto 多样性通道。");
        }

        static JointObjectiveScore Score(
            CombatTerminalOutcome? outcome = null,
            int dead = 0,
            int total = 0,
            IReadOnlyList<int>? losses = null,
            int potionCost = 0,
            int potionUses = 0,
            int deathSaves = 0,
            int growth = 0,
            int longTerm = 0,
            int stolen = 0,
            int turns = 1,
            int actions = 0)
            => new(
                outcome,
                dead,
                total,
                losses ?? [0, 0],
                potionCost,
                potionUses,
                deathSaves,
                growth,
                longTerm,
                stolen,
                turns,
                actions);

        static void RequireBetter(JointObjectiveScore better, JointObjectiveScore worse, string field)
        {
            if (JointObjectiveScore.Compare(better, worse) <= 0
                || JointObjectiveScore.Compare(worse, better) >= 0)
            {
                throw new InvalidOperationException($"联合终局政策未正确排序：{field}。");
            }
        }
    }
}
