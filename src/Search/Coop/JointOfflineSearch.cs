using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed record JointOfflineSearchResult(
    JointCombatSnapshot Snapshot,
    JointObjectiveScore Score,
    IReadOnlyList<PlanAction> Actions,
    int ExpandedStates)
{
    internal JointSearchTermination Termination { get; init; } = JointSearchTermination.Completed;
}

internal enum JointSearchTermination
{
    Completed,
    StateBudget,
}

internal sealed record JointOfflineSearchRequest(
    IReadOnlyList<PlanAction> FixedPrefix,
    int MaximumActions = 12,
    int MaximumStates = 100_000,
    JointPotionSearchPolicy? PotionPolicy = null)
{
    internal static JointOfflineSearchRequest Default(int maximumActions, int maximumStates)
        => new([], maximumActions, maximumStates);

    internal JointPotionSearchPolicy EffectivePotionPolicy
        => PotionPolicy ?? JointPotionSearchPolicy.Unrestricted;
}

internal static partial class JointOfflineSearch
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
        Node seed = ReplayFixedPrefix(root, request);
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
                if (request.EffectivePotionPolicy.IsBoundaryEligible(node.Actions))
                    best = SelectBetter(root, best, snapshot, node.Actions, expanded);
                continue;
            }

            foreach (JointActionCandidate candidate in JointActionExpander.Expand(node.Simulator, node.Turns))
            {
                if (!request.EffectivePotionPolicy.Allows(candidate.Action, node.Actions))
                    continue;
                CombatPredictionSimulator child = node.Simulator.Fork();
                ForkableSet<uint> deaths = node.ProcessedEnemyDeaths.Fork();
                JointTurnState turns = JointActionTransition.Apply(
                    child, node.Turns, candidate.Action, deaths);
                open.Enqueue(new Node(child, deaths, turns, [.. node.Actions, candidate.Action]));
            }
        }
        return best ?? throw new InvalidOperationException("联合 BFS 没有到达终局或回合屏障。");
    }

    /// <summary>
    /// Bounded production-oriented member. Expansion is layer-ordered and candidate admission is
    /// deterministic; all Actors share the same state budget and transposition set.
    /// </summary>
    internal static JointOfflineSearchResult SolveBeam(
        CombatRootSnapshot root,
        JointOfflineSearchRequest request,
        int beamWidth,
        CancellationToken cancellationToken = default,
        int degreeOfParallelism = 1)
    {
        ValidateRequest(root, request);
        if (beamWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(beamWidth));
        if (degreeOfParallelism <= 0)
            throw new ArgumentOutOfRangeException(nameof(degreeOfParallelism));
        List<Node> frontier = [ReplayFixedPrefix(root, request)];
        HashSet<StateFingerprint> seen = [];
        JointOfflineSearchResult? best = null;
        int expanded = 0;
        bool budgetReached = false;
        while (frontier.Count > 0 && !budgetReached)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<(Node Node, JointCombatSnapshot Snapshot, JointObjectiveScore Score)> next = [];
            List<Node> expandable = [];
            for (int frontierIndex = 0; frontierIndex < frontier.Count; frontierIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (expanded >= request.MaximumStates)
                {
                    budgetReached = true;
                    for (int remaining = frontierIndex; remaining < frontier.Count; remaining++)
                    {
                        Node pending = frontier[remaining];
                        JointCombatSnapshot pendingSnapshot = JointCombatSnapshot.Capture(
                            root,
                            pending.Simulator,
                            pending.Turns);
                        next.Add((
                            pending,
                            pendingSnapshot,
                            JointObjective.Capture(root, pendingSnapshot, pending.Actions)));
                    }
                    break;
                }
                Node node = frontier[frontierIndex];
                expanded++;
                JointCombatSnapshot snapshot = JointCombatSnapshot.Capture(root, node.Simulator, node.Turns);
                if (!seen.Add(snapshot.StateKey))
                    continue;
                if (IsBoundary(node, request.MaximumActions))
                {
                    if (request.EffectivePotionPolicy.IsBoundaryEligible(node.Actions))
                        best = SelectBetter(root, best, snapshot, node.Actions, expanded);
                    continue;
                }
                expandable.Add(node);
            }
            IReadOnlyList<(Node Node, JointCombatSnapshot Snapshot, JointObjectiveScore Score)>[]
                expandedParents = new IReadOnlyList<(Node, JointCombatSnapshot, JointObjectiveScore)>[expandable.Count];
            int laneCount = Math.Min(degreeOfParallelism, expandable.Count);
            if (laneCount == 1)
            {
                for (int parentIndex = 0; parentIndex < expandable.Count; parentIndex++)
                    expandedParents[parentIndex] = ExpandParent(expandable[parentIndex]);
            }
            else if (laneCount > 1)
            {
                Task[] lanes = new Task[laneCount];
                for (int laneIndex = 0; laneIndex < laneCount; laneIndex++)
                {
                    int fixedLane = laneIndex;
                    lanes[laneIndex] = Task.Run(() =>
                    {
                        for (int parentIndex = fixedLane;
                             parentIndex < expandable.Count;
                             parentIndex += laneCount)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            expandedParents[parentIndex] = ExpandParent(expandable[parentIndex]);
                        }
                    }, cancellationToken);
                }
                Task.WhenAll(lanes).GetAwaiter().GetResult();
            }
            foreach (IReadOnlyList<(Node Node, JointCombatSnapshot Snapshot, JointObjectiveScore Score)> children
                     in expandedParents)
                next.AddRange(children);
            frontier = JointBeamRetentionPolicy.Select(
                    next.Select(static item => new JointBeamRetentionCandidate<Node>(
                        item.Node,
                        item.Score,
                        item.Node.Actions)).ToArray(),
                    beamWidth)
                .Select(static candidate => candidate.Value)
                .ToList();

            IReadOnlyList<(Node Node, JointCombatSnapshot Snapshot, JointObjectiveScore Score)>
                ExpandParent(Node node)
            {
                List<(Node, JointCombatSnapshot, JointObjectiveScore)> children = [];
                foreach (JointActionCandidate candidate in JointActionExpander.Expand(
                             node.Simulator,
                             node.Turns))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!request.EffectivePotionPolicy.Allows(candidate.Action, node.Actions))
                        continue;
                    CombatPredictionSimulator child = node.Simulator.Fork();
                    ForkableSet<uint> deaths = node.ProcessedEnemyDeaths.Fork();
                    JointTurnState turns = JointActionTransition.Apply(
                        child,
                        node.Turns,
                        candidate.Action,
                        deaths);
                    PlanAction[] actions = [.. node.Actions, candidate.Action];
                    Node childNode = new(child, deaths, turns, actions);
                    JointCombatSnapshot childSnapshot = JointCombatSnapshot.Capture(root, child, turns);
                    children.Add((
                        childNode,
                        childSnapshot,
                        JointObjective.Capture(root, childSnapshot, actions)));
                }
                return children;
            }
        }
        if (best is null)
        {
            foreach (Node node in frontier)
            {
                JointCombatSnapshot snapshot = JointCombatSnapshot.Capture(root, node.Simulator, node.Turns);
                if (request.EffectivePotionPolicy.IsBoundaryEligible(node.Actions))
                    best = SelectBetter(root, best, snapshot, node.Actions, expanded);
            }
        }
        return (best ?? throw new InvalidOperationException("联合 Beam 没有可评分的终局或边界。")) with
        {
            ExpandedStates = expanded,
            Termination = budgetReached ? JointSearchTermination.StateBudget : JointSearchTermination.Completed,
        };
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
        Node seed = ReplayFixedPrefix(root, request);
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
                if (request.EffectivePotionPolicy.IsBoundaryEligible(actions))
                    best = SelectBetter(root, best, snapshot, actions, expanded);
                return;
            }
            foreach (JointActionCandidate candidate in JointActionExpander.Expand(simulator, turns))
            {
                if (!request.EffectivePotionPolicy.Allows(candidate.Action, actions))
                    continue;
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
        JointOfflineSearchRequest request)
    {
        CombatPredictionSimulator simulator = root.ForkSimulator();
        ForkableSet<uint> deaths = JointActionTransition.CaptureProcessedEnemyDeaths(root, simulator);
        JointTurnState turns = JointTurnState.Start(root.Actors.Count, root.StartTurnNumber);
        List<PlanAction> applied = [];
        foreach (PlanAction action in request.FixedPrefix)
        {
            if (action.Turn != turns.Turn)
            {
                if (action.Turn != turns.Turn + 1 || !turns.IsBarrierReached)
                {
                    throw new InvalidOperationException(
                        $"联合固定前缀不能从 turn={turns.Turn} 推进到 actionTurn={action.Turn}：" +
                        $"barrier={turns.IsBarrierReached}。");
                }
                IReadOnlyList<PlanCardChoice> turnStartChoices = applied
                    .LastOrDefault(prior => prior.Turn == turns.Turn
                        && prior.Kind == PlanActionKind.EndTurn
                        && prior.TurnStartChoices is { Count: > 0 })
                    ?.TurnStartChoices ?? [];
                JointRoundTransition.CompletePlayerSide(simulator, turns, deaths);
                JointRoundTransition.CompleteBasicEnemySide(simulator, deaths);
                turns = JointRoundTransition.StartBasicPlayerSide(
                    simulator,
                    turns,
                    deaths,
                    turnStartChoices);
            }
            if (!request.EffectivePotionPolicy.Allows(action, applied))
                throw new InvalidOperationException($"联合固定前缀违反药水政策：{action}。");
            turns = JointActionTransition.Apply(simulator, turns, action, deaths);
            applied.Add(action);
        }
        return new Node(simulator, deaths, turns, applied.ToArray());
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

    private sealed class JointObjectiveScoreComparer : IComparer<JointObjectiveScore>
    {
        internal static JointObjectiveScoreComparer Instance { get; } = new();

        public int Compare(JointObjectiveScore? left, JointObjectiveScore? right)
            => left is null
                ? right is null ? 0 : -1
                : right is null ? 1 : JointObjectiveScore.Compare(left, right);
    }

    private sealed class JointActionSequenceComparer : IComparer<IReadOnlyList<PlanAction>>
    {
        internal static JointActionSequenceComparer Instance { get; } = new();

        public int Compare(IReadOnlyList<PlanAction>? left, IReadOnlyList<PlanAction>? right)
            => left is null
                ? right is null ? 0 : -1
                : right is null ? 1 : CompareActions(left, right);
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
        request.EffectivePotionPolicy.Validate(root.Actors.Count);
    }
}
