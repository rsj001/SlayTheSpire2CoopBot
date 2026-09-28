using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal static partial class JointOfflineSearch
{
    internal static JointOfflineSearchResult SolveSmartBfws(
        CombatRootSnapshot root,
        JointOfflineSearchRequest request,
        int maximumOpen,
        CancellationToken cancellationToken = default)
        => SolveSmartCounterfactual(
            root,
            request,
            adjusted => SolveBfws(root, adjusted, maximumOpen, cancellationToken));

    internal static JointOfflineSearchResult SolveBfws(
        CombatRootSnapshot root,
        JointOfflineSearchRequest request,
        int maximumOpen,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(root, request);
        if (maximumOpen <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumOpen));
        BfwsBoundedOpen<Node> open = new(maximumOpen);
        HashSet<StateFingerprint> seen = [];
        HashSet<(int Kind, int Actor, int Value)> noveltyFacts = [];
        JointOfflineSearchResult? best = null;
        long sequence = 0;
        int expanded = 0;
        Node seed = ReplayFixedPrefix(root, request);
        Enqueue(seed);
        while (open.Count > 0 && expanded < request.MaximumStates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Node node = open.Dequeue();
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
                Enqueue(new Node(child, deaths, turns, [.. node.Actions, candidate.Action]));
            }
        }
        JointSearchTermination termination = open.Count > 0
            ? JointSearchTermination.StateBudget
            : JointSearchTermination.Completed;
        if (best is null)
        {
            foreach (Node node in open.Values)
            {
                JointCombatSnapshot snapshot = JointCombatSnapshot.Capture(root, node.Simulator, node.Turns);
                if (request.EffectivePotionPolicy.IsBoundaryEligible(node.Actions))
                    best = SelectBetter(root, best, snapshot, node.Actions, expanded);
            }
        }
        return (best ?? throw new JointPotionPolicyUnsatisfiedException(
            "联合 BFWS 没有满足药水政策的可评分终局或边界。")) with
        {
            ExpandedStates = expanded,
            Termination = termination,
        };

        void Enqueue(Node node)
        {
            JointCombatSnapshot snapshot = JointCombatSnapshot.Capture(root, node.Simulator, node.Turns);
            int novelty = ObserveNovelty(snapshot, noveltyFacts) ? 1 : 2;
            JointObjectiveScore score = JointObjective.Capture(root, snapshot, node.Actions);
            double secondary = score.DeadActorCount * 1_000_000_000d
                + score.TotalHpLost * 1_000_000d
                + score.PotionStrategicCost * 10_000d
                + score.DeathSaveUses * 1_000d
                + score.OutstandingStolenResource
                - score.GrowthRewards * 100d
                - score.LongTermResourceValue;
            _ = open.Enqueue(node, (novelty, secondary, sequence++), out _);
        }
    }

    private static bool ObserveNovelty(
        JointCombatSnapshot snapshot,
        HashSet<(int Kind, int Actor, int Value)> facts)
    {
        bool novel = false;
        foreach (JointActorSnapshot actor in snapshot.Actors)
        {
            int index = actor.Id.Index;
            novel |= facts.Add((0, index, (int)actor.Phase));
            novel |= facts.Add((1, index, actor.Hp / 5));
            novel |= facts.Add((2, index, actor.Block / 5));
            novel |= facts.Add((3, index, actor.Energy));
            novel |= facts.Add((4, index, actor.Stars));
            novel |= facts.Add((5, index, actor.HandCount));
            novel |= facts.Add((6, index, actor.DrawCount / 3));
            novel |= facts.Add((7, index, actor.DiscardCount / 3));
            novel |= facts.Add((8, index, actor.ExhaustCount / 3));
            novel |= facts.Add((9, index, actor.Turn));
        }
        novel |= facts.Add((10, -1, snapshot.Turn));
        return novel;
    }
}
