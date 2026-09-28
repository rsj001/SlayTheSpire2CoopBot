using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed record JointOfflineSearchResult(
    JointCombatSnapshot Snapshot,
    JointObjectiveScore Score,
    IReadOnlyList<PlanAction> Actions,
    int ExpandedStates);

internal sealed record JointOfflineSearchRequest(
    IReadOnlyList<PlanAction> FixedPrefix,
    int MaximumActions = 12,
    int MaximumStates = 100_000)
{
    internal static JointOfflineSearchRequest Default(int maximumActions, int maximumStates)
        => new([], maximumActions, maximumStates);
}

internal static class JointOfflineSearch
{
    private sealed record Node(
        CombatPredictionSimulator Simulator,
        ForkableSet<uint> ProcessedEnemyDeaths,
        JointTurnState Turns,
        IReadOnlyList<PlanAction> Actions);

    /// <summary>Offline production member: breadth-first enumeration with complete-state deduplication.</summary>
    internal static JointOfflineSearchResult SolveBreadthFirst(
        CombatRootSnapshot root,
        int maximumActions = 12,
        int maximumStates = 100_000)
        => SolveBreadthFirst(
            root,
            JointOfflineSearchRequest.Default(maximumActions, maximumStates));

    internal static JointOfflineSearchResult SolveBreadthFirst(
        CombatRootSnapshot root,
        JointOfflineSearchRequest request)
    {
        ValidateRequest(root, request);
        Node seed = ReplayFixedPrefix(root, request.FixedPrefix);
        Queue<Node> open = new();
        open.Enqueue(seed);
        HashSet<StateFingerprint> seen = [];
        JointOfflineSearchResult? best = null;
        int expanded = 0;
        while (open.TryDequeue(out Node? node))
        {
            if (++expanded > request.MaximumStates)
                throw new InvalidOperationException($"联合 BFS 超过状态上限 {request.MaximumStates}。");
            JointCombatSnapshot snapshot = JointCombatSnapshot.Capture(root, node.Simulator, node.Turns);
            if (!seen.Add(snapshot.StateKey))
                continue;
            if (IsBoundary(node, request.MaximumActions))
            {
                best = SelectBetter(root, best, snapshot, node.Actions, expanded);
                continue;
            }

            foreach (JointActionCandidate candidate in JointActionExpander.Expand(node.Simulator, node.Turns))
            {
                CombatPredictionSimulator child = node.Simulator.Fork();
                ForkableSet<uint> deaths = node.ProcessedEnemyDeaths.Fork();
                JointTurnState turns = JointActionTransition.Apply(
                    child, node.Turns, candidate.Action, deaths);
                open.Enqueue(new Node(child, deaths, turns, [.. node.Actions, candidate.Action]));
            }
        }
        return best ?? throw new InvalidOperationException("联合 BFS 没有到达终局或回合屏障。");
    }

    /// <summary>Independent oracle enumerator: plain depth-first traversal without deduplication.</summary>
    internal static JointOfflineSearchResult SolveDepthFirstOracle(
        CombatRootSnapshot root,
        int maximumActions = 12,
        int maximumStates = 100_000)
        => SolveDepthFirstOracle(
            root,
            JointOfflineSearchRequest.Default(maximumActions, maximumStates));

    internal static JointOfflineSearchResult SolveDepthFirstOracle(
        CombatRootSnapshot root,
        JointOfflineSearchRequest request)
    {
        ValidateRequest(root, request);
        JointOfflineSearchResult? best = null;
        int expanded = 0;
        Node seed = ReplayFixedPrefix(root, request.FixedPrefix);
        Visit(
            seed.Simulator,
            seed.ProcessedEnemyDeaths,
            seed.Turns,
            seed.Actions);
        return best ?? throw new InvalidOperationException("联合 DFS oracle 没有到达终局或回合屏障。");

        void Visit(
            CombatPredictionSimulator simulator,
            ForkableSet<uint> processedEnemyDeaths,
            JointTurnState turns,
            IReadOnlyList<PlanAction> actions)
        {
            if (++expanded > request.MaximumStates)
                throw new InvalidOperationException($"联合 DFS oracle 超过状态上限 {request.MaximumStates}。");
            Node node = new(simulator, processedEnemyDeaths, turns, actions);
            if (IsBoundary(node, request.MaximumActions))
            {
                JointCombatSnapshot snapshot = JointCombatSnapshot.Capture(root, simulator, turns);
                best = SelectBetter(root, best, snapshot, actions, expanded);
                return;
            }
            foreach (JointActionCandidate candidate in JointActionExpander.Expand(simulator, turns))
            {
                CombatPredictionSimulator child = simulator.Fork();
                ForkableSet<uint> deaths = processedEnemyDeaths.Fork();
                JointTurnState childTurns = JointActionTransition.Apply(
                    child, turns, candidate.Action, deaths);
                Visit(child, deaths, childTurns, [.. actions, candidate.Action]);
            }
        }
    }

    private static Node ReplayFixedPrefix(
        CombatRootSnapshot root,
        IReadOnlyList<PlanAction> prefix)
    {
        CombatPredictionSimulator simulator = root.ForkSimulator();
        ForkableSet<uint> deaths = JointActionTransition.CaptureProcessedEnemyDeaths(root, simulator);
        JointTurnState turns = JointTurnState.Start(root.Actors.Count, root.StartTurnNumber);
        foreach (PlanAction action in prefix)
        {
            if (action.Turn != turns.Turn)
            {
                throw new NotSupportedException(
                    $"F3 固定前缀不能跨回合：current={turns.Turn} action={action.Turn}；" +
                    "等待 F7 联合回合生命周期。");
            }
            turns = JointActionTransition.Apply(simulator, turns, action, deaths);
        }
        return new Node(simulator, deaths, turns, prefix.ToArray());
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

    private static void ValidateRequest(
        CombatRootSnapshot root,
        JointOfflineSearchRequest request)
    {
        if (request.MaximumActions <= 0)
            throw new ArgumentOutOfRangeException(nameof(request.MaximumActions));
        if (request.MaximumStates <= 0)
            throw new ArgumentOutOfRangeException(nameof(request.MaximumStates));
        if (request.FixedPrefix.Count > request.MaximumActions)
            throw new ArgumentException("联合固定前缀超过动作上限。", nameof(request));
        foreach (PlanAction action in request.FixedPrefix)
            action.ValidateActor(root.Actors.Count);
    }
}
