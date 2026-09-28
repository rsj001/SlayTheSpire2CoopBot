namespace CombatSolver;

internal sealed record OfflineJointAction(
    CombatActorId Actor,
    string Id,
    int EnergyCost,
    int Damage,
    bool EndsTurn = false);

internal sealed record OfflineJointFixture(
    int EnemyHp,
    IReadOnlyList<IReadOnlyList<OfflineJointAction>> ActionsByActor);

internal sealed record OfflineJointOracleResult(
    bool Victory,
    int RemainingEnemyHp,
    IReadOnlyList<OfflineJointAction> Actions,
    int ExpandedStates);

/// <summary>
/// Tiny deterministic oracle used only for offline fixtures. It exhausts every action order and
/// does not claim to model arbitrary game effects; production search never calls this type.
/// </summary>
internal static class JointExhaustiveOracle
{
    internal static OfflineJointOracleResult Solve(OfflineJointFixture fixture)
    {
        if (fixture.EnemyHp <= 0)
            throw new ArgumentOutOfRangeException(nameof(fixture), "fixture 敌人生命必须为正数。");
        if (fixture.ActionsByActor.Count is < 2 or > 4)
            throw new ArgumentOutOfRangeException(nameof(fixture), "oracle 只接受 2 至 4 个 Actor。");

        bool[] used = new bool[fixture.ActionsByActor.Sum(actions => actions.Count)];
        bool[] ended = new bool[fixture.ActionsByActor.Count];
        int[] energy = fixture.ActionsByActor.Select(actions => actions.Count == 0 ? 0 : 1).ToArray();
        List<OfflineJointAction> prefix = [];
        OfflineJointOracleResult? best = null;
        int expanded = 0;
        Visit(fixture.EnemyHp, used, ended, energy, prefix);
        return best! with { ExpandedStates = expanded };

        void Visit(
            int enemyHp,
            bool[] usedState,
            bool[] endedState,
            int[] energyState,
            List<OfflineJointAction> actions)
        {
            expanded++;
            Consider(enemyHp, actions);
            if (enemyHp <= 0 || endedState.All(static value => value))
                return;

            int global = 0;
            for (int actorIndex = 0; actorIndex < fixture.ActionsByActor.Count; actorIndex++)
            {
                IReadOnlyList<OfflineJointAction> actorActions = fixture.ActionsByActor[actorIndex];
                if (!endedState[actorIndex])
                {
                    for (int actionIndex = 0; actionIndex < actorActions.Count; actionIndex++)
                    {
                        OfflineJointAction action = actorActions[actionIndex];
                        if (usedState[global + actionIndex]
                            || action.EndsTurn
                            || action.EnergyCost > energyState[actorIndex])
                            continue;
                        bool[] nextUsed = [.. usedState];
                        bool[] nextEnded = [.. endedState];
                        int[] nextEnergy = [.. energyState];
                        nextUsed[global + actionIndex] = true;
                        nextEnergy[actorIndex] -= action.EnergyCost;
                        actions.Add(action);
                        Visit(Math.Max(0, enemyHp - action.Damage), nextUsed, nextEnded, nextEnergy, actions);
                        actions.RemoveAt(actions.Count - 1);
                    }
                }
                global += actorActions.Count;
            }

            for (int actorIndex = 0; actorIndex < endedState.Length; actorIndex++)
            {
                if (endedState[actorIndex])
                    continue;
                bool[] nextEnded = [.. endedState];
                nextEnded[actorIndex] = true;
                actions.Add(new OfflineJointAction(new CombatActorId(actorIndex), "EndTurn", 0, 0, true));
                Visit(enemyHp, usedState, nextEnded, energyState, actions);
                actions.RemoveAt(actions.Count - 1);
            }
        }

        void Consider(int enemyHp, IReadOnlyList<OfflineJointAction> actions)
        {
            OfflineJointOracleResult candidate = new(enemyHp <= 0, enemyHp, actions.ToArray(), 0);
            if (best is null || Compare(candidate, best) > 0)
                best = candidate;
        }
    }

    private static int Compare(OfflineJointOracleResult left, OfflineJointOracleResult right)
    {
        int comparison = left.Victory.CompareTo(right.Victory);
        if (comparison != 0)
            return comparison;
        comparison = right.RemainingEnemyHp.CompareTo(left.RemainingEnemyHp);
        if (comparison != 0)
            return comparison;
        comparison = right.Actions.Count.CompareTo(left.Actions.Count);
        if (comparison != 0)
            return comparison;
        int count = Math.Min(left.Actions.Count, right.Actions.Count);
        for (int index = 0; index < count; index++)
        {
            OfflineJointAction l = left.Actions[index];
            OfflineJointAction r = right.Actions[index];
            comparison = l.Actor.Index.CompareTo(r.Actor.Index);
            if (comparison != 0)
                return -comparison;
            comparison = string.CompareOrdinal(r.Id, l.Id);
            if (comparison != 0)
                return comparison;
        }
        return 0;
    }
}
