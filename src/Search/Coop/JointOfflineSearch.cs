using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed record JointOfflineSearchResult(
    JointCombatSnapshot Snapshot,
    JointObjectiveScore Score,
    IReadOnlyList<PlanAction> Actions,
    int ExpandedStates);

internal static class JointOfflineSearch
{
    private sealed record Node(
        CombatPredictionSimulator Simulator,
        JointTurnState Turns,
        IReadOnlyList<PlanAction> Actions);

    /// <summary>Offline production member: breadth-first enumeration with complete-state deduplication.</summary>
    internal static JointOfflineSearchResult SolveBreadthFirst(
        CombatRootSnapshot root,
        int maximumActions = 12,
        int maximumStates = 100_000)
    {
        ValidateLimits(maximumActions, maximumStates);
        Queue<Node> open = new();
        open.Enqueue(new Node(
            root.ForkSimulator(),
            JointTurnState.Start(root.Actors.Count, root.StartTurnNumber),
            []));
        HashSet<StateFingerprint> seen = [];
        JointOfflineSearchResult? best = null;
        int expanded = 0;
        while (open.TryDequeue(out Node? node))
        {
            if (++expanded > maximumStates)
                throw new InvalidOperationException($"联合 BFS 超过状态上限 {maximumStates}。");
            JointCombatSnapshot snapshot = JointCombatSnapshot.Capture(root, node.Simulator, node.Turns);
            if (!seen.Add(snapshot.StateKey))
                continue;
            if (IsBoundary(node, maximumActions))
            {
                best = SelectBetter(root, best, snapshot, node.Actions, expanded);
                continue;
            }

            foreach (JointActionCandidate candidate in JointActionExpander.Expand(node.Simulator, node.Turns))
            {
                CombatPredictionSimulator child = node.Simulator.Fork();
                JointTurnState turns = JointActionTransition.Apply(child, node.Turns, candidate.Action);
                open.Enqueue(new Node(child, turns, [.. node.Actions, candidate.Action]));
            }
        }
        return best ?? throw new InvalidOperationException("联合 BFS 没有到达终局或回合屏障。");
    }

    /// <summary>Independent oracle enumerator: plain depth-first traversal without deduplication.</summary>
    internal static JointOfflineSearchResult SolveDepthFirstOracle(
        CombatRootSnapshot root,
        int maximumActions = 12,
        int maximumStates = 100_000)
    {
        ValidateLimits(maximumActions, maximumStates);
        JointOfflineSearchResult? best = null;
        int expanded = 0;
        Visit(
            root.ForkSimulator(),
            JointTurnState.Start(root.Actors.Count, root.StartTurnNumber),
            []);
        return best ?? throw new InvalidOperationException("联合 DFS oracle 没有到达终局或回合屏障。");

        void Visit(
            CombatPredictionSimulator simulator,
            JointTurnState turns,
            IReadOnlyList<PlanAction> actions)
        {
            if (++expanded > maximumStates)
                throw new InvalidOperationException($"联合 DFS oracle 超过状态上限 {maximumStates}。");
            Node node = new(simulator, turns, actions);
            if (IsBoundary(node, maximumActions))
            {
                JointCombatSnapshot snapshot = JointCombatSnapshot.Capture(root, simulator, turns);
                best = SelectBetter(root, best, snapshot, actions, expanded);
                return;
            }
            foreach (JointActionCandidate candidate in JointActionExpander.Expand(simulator, turns))
            {
                CombatPredictionSimulator child = simulator.Fork();
                JointTurnState childTurns = JointActionTransition.Apply(child, turns, candidate.Action);
                Visit(child, childTurns, [.. actions, candidate.Action]);
            }
        }
    }

    private static bool IsBoundary(Node node, int maximumActions)
        => node.Simulator.TerminalStamp.HasValue
            || node.Turns.IsBarrierReached
            || node.Actions.Count >= maximumActions;

    private static JointOfflineSearchResult SelectBetter(
        CombatRootSnapshot root,
        JointOfflineSearchResult? best,
        JointCombatSnapshot snapshot,
        IReadOnlyList<PlanAction> actions,
        int expanded)
    {
        JointObjectiveScore score = JointObjective.Capture(root, snapshot, actions);
        JointOfflineSearchResult candidate = new(snapshot, score, actions.ToArray(), expanded);
        if (best is null)
            return candidate;
        int comparison = JointObjectiveScore.Compare(candidate.Score, best.Score);
        if (comparison > 0 || comparison == 0 && CompareActions(candidate.Actions, best.Actions) < 0)
            return candidate;
        return best;
    }

    private static int CompareActions(
        IReadOnlyList<PlanAction> left,
        IReadOnlyList<PlanAction> right)
    {
        int count = Math.Min(left.Count, right.Count);
        for (int index = 0; index < count; index++)
        {
            PlanAction l = left[index];
            PlanAction r = right[index];
            int comparison = l.Actor.Index.CompareTo(r.Actor.Index);
            if (comparison != 0)
                return comparison;
            comparison = l.Kind.CompareTo(r.Kind);
            if (comparison != 0)
                return comparison;
            comparison = string.CompareOrdinal(l.CardId, r.CardId);
            if (comparison != 0)
                return comparison;
            comparison = l.CardOccurrence.CompareTo(r.CardOccurrence);
            if (comparison != 0)
                return comparison;
            comparison = Nullable.Compare(l.TargetCombatId, r.TargetCombatId);
            if (comparison != 0)
                return comparison;
        }
        return left.Count.CompareTo(right.Count);
    }

    private static void ValidateLimits(int maximumActions, int maximumStates)
    {
        if (maximumActions <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumActions));
        if (maximumStates <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumStates));
    }
}
