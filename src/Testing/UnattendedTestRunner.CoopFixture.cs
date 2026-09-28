using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Singleton;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private static void AssertCoopMultiActorRoots(CombatState source)
    {
        AssertActorCount(2);
        AssertActorCount(4);

        void AssertActorCount(int actorCount)
        {
            CombatRootSnapshot root = CreateOfflineJointRoot(source, actorCount);
            if (root.Actors.Count != actorCount || root.PlayerCount != actorCount
                || root.LocalActorId.Index != 0)
                throw new InvalidOperationException($"离线联合根未捕获 {actorCount} 个稳定 Actor。 ");

            CombatPredictionSimulator simulator = root.ForkSimulator();
            JointTurnState turns = JointTurnState.Start(actorCount, root.StartTurnNumber);
            IReadOnlyList<JointActionCandidate> candidates = JointActionExpander.Expand(simulator, turns);
            for (int index = 0; index < actorCount; index++)
            {
                CombatActorId actor = new(index);
                if (!candidates.Any(candidate => candidate.Action.Actor == actor
                        && candidate.Action.Kind == PlanActionKind.EndTurn)
                    || !candidates.Any(candidate => candidate.Action.Actor == actor
                        && candidate.Action.Kind == PlanActionKind.PlayCard))
                    throw new InvalidOperationException($"离线联合根缺少 {actor} 的出牌或结束候选。");
            }

            JointCombatSnapshot before = JointCombatSnapshot.Capture(root, simulator, turns);
            AssertRemoteActorStateKeyCoverage(root, before, turns, actorCount - 1);
            AssertMultiplayerBlockScaling(root, actorCount);
            JointActionCandidate selected = candidates.First(candidate =>
                candidate.Action.Actor.Index == actorCount - 1
                && candidate.Action.Kind == PlanActionKind.PlayCard);
            JointReplayResult replay = JointPlanReplayer.Replay(
                root,
                new JointPlan(actorCount, [selected.Action]));
            if (replay.Snapshot.StateKey == before.StateKey
                || replay.AppliedActions.Count != 1
                || replay.AppliedActions[0].Actor.Index != actorCount - 1)
                throw new InvalidOperationException($"离线 {actorCount} Actor 的非本地动作未被严格回放。");

            if (actorCount == 2)
            {
                JointOfflineSearchResult breadthFirst = JointOfflineSearch.SolveBreadthFirst(
                    root,
                    maximumActions: 2,
                    maximumStates: 2_000);
                JointOfflineSearchResult oracle = JointOfflineSearch.SolveDepthFirstOracle(
                    root,
                    maximumActions: 2,
                    maximumStates: 2_000);
                if (JointObjectiveScore.Compare(breadthFirst.Score, oracle.Score) != 0
                    || breadthFirst.Snapshot.StateKey != oracle.Snapshot.StateKey
                    || ComparePlanActions(breadthFirst.Actions, oracle.Actions) != 0)
                    throw new InvalidOperationException(
                        "联合 BFS 与独立 DFS oracle 的最优值、动作序或终局状态不一致。");

                PlanAction prefix = candidates.First(candidate =>
                    candidate.Action.Actor.Index == 1
                    && candidate.Action.Kind == PlanActionKind.PlayCard).Action;
                JointOfflineSearchRequest prefixedRequest = new(
                    [prefix],
                    MaximumActions: 2,
                    MaximumStates: 2_000);
                JointOfflineSearchResult prefixedBfs =
                    JointOfflineSearch.SolveBreadthFirst(root, prefixedRequest);
                JointOfflineSearchResult prefixedOracle =
                    JointOfflineSearch.SolveDepthFirstOracle(root, prefixedRequest);
                if (prefixedBfs.Actions.Count == 0
                    || prefixedBfs.Actions[0] != prefix
                    || JointObjectiveScore.Compare(prefixedBfs.Score, prefixedOracle.Score) != 0
                    || ComparePlanActions(prefixedBfs.Actions, prefixedOracle.Actions) != 0)
                {
                    throw new InvalidOperationException(
                        "联合固定前缀没有由 BFS/DFS 从同一严格状态继续搜索。");
                }
            }
        }
    }

    private static void AssertMultiplayerBlockScaling(CombatRootSnapshot root, int actorCount)
    {
        CombatPredictionSimulator simulator = root.ForkSimulator();
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        Creature enemy = combat.Enemies.First(candidate =>
            candidate.IsPrimaryEnemy || candidate.IsSecondaryEnemy);
        decimal actual = simulator.GainBlock(enemy, 10m, ValueProp.Move);
        decimal expectedMultiplier = actorCount <= 2
            ? actorCount
            : actorCount * MultiplayerScalingModel.GetMultiplayerScaling(
                combat.Encounter,
                combat.CurrentActIndex);
        if (actual != 10m * expectedMultiplier)
        {
            throw new InvalidOperationException(
                $"{actorCount} Actor 敌人格挡缩放错误：actual={actual} " +
                $"expected={10m * expectedMultiplier}。");
        }
    }

    private static void AssertRemoteActorStateKeyCoverage(
        CombatRootSnapshot root,
        JointCombatSnapshot baseline,
        JointTurnState turns,
        int actorIndex)
    {
        AssertMutation("block", (simulator, player) =>
            simulator.GainBlock(player.Creature, 7, default));
        AssertMutation("energy", (simulator, player) =>
            simulator.State.GetPlayerCombatState(player).LoseEnergy(1));
        AssertMutation("stars", (simulator, player) =>
            simulator.State.GetPlayerCombatState(player).GainStars(1));
        AssertMutation("gold", (simulator, player) =>
            ((SimulatedCombatState)simulator.State.CombatState).GainPlayerGold(player, 1));
        AssertMutation("card_instance", (simulator, player) =>
        {
            PredictedCard card = simulator.State.GetPlayerCombatState(player).Hand.Cards[0];
            card.Upgrade();
        });

        void AssertMutation(
            string field,
            Action<CombatPredictionSimulator, Player> mutate)
        {
            CombatPredictionSimulator fork = root.ForkSimulator();
            Player actor = fork.State.Players[actorIndex];
            mutate(fork, actor);
            JointCombatSnapshot changed = JointCombatSnapshot.Capture(root, fork, turns);
            if (changed.StateKey == baseline.StateKey
                || changed.Continuation.StateText == baseline.Continuation.StateText)
            {
                throw new InvalidOperationException(
                    $"Actor{actorIndex} 的 {field} 变化未进入联合状态键和续用戳。");
            }
            for (int index = 0; index < baseline.Actors.Count; index++)
            {
                if (index == actorIndex)
                    continue;
                if (changed.Actors[index] != baseline.Actors[index])
                    throw new InvalidOperationException(
                        $"Actor{actorIndex} 的 {field} 变化污染了兄弟 Actor{index} 快照。");
            }
        }
    }

    private static int ComparePlanActions(
        IReadOnlyList<PlanAction> left,
        IReadOnlyList<PlanAction> right)
    {
        if (left.Count != right.Count)
            return left.Count.CompareTo(right.Count);
        for (int index = 0; index < left.Count; index++)
        {
            if (left[index].Actor != right[index].Actor
                || left[index].Kind != right[index].Kind
                || !string.Equals(left[index].CardId, right[index].CardId, StringComparison.Ordinal)
                || left[index].CardOccurrence != right[index].CardOccurrence
                || left[index].TargetCombatId != right[index].TargetCombatId)
                return 1;
        }
        return 0;
    }

    private static CombatRootSnapshot CreateOfflineJointRoot(CombatState source, int actorCount)
    {
        if (actorCount is < 1 or > 4)
            throw new ArgumentOutOfRangeException(nameof(actorCount));
        Player liveLocal = source.Players.First(player => player.NetId
            == (MegaCrit.Sts2.Core.Context.LocalContext.GetMe(source)?.NetId
                ?? throw new InvalidOperationException("当前测试战斗没有本地玩家。")));
        HashSet<ulong> netIds = [];
        Player[] players = new Player[actorCount];
        for (int index = 0; index < actorCount; index++)
        {
            ulong netId = index == 0 ? liveLocal.NetId : checked(liveLocal.NetId + (ulong)index);
            while (!netIds.Add(netId))
                netId++;
            players[index] = Player.CreateForNewRun(liveLocal.Character, liveLocal.UnlockState, netId);
        }

        RunState run = RunState.CreateForTest(players, seed: $"COOP-OFFLINE-{actorCount}");
        CombatState state = new(
            runState: run,
            modifiers: run.Modifiers,
            badgeModels: run.BadgeModels,
            multiplayerScalingModel: run.MultiplayerScalingModel);
        foreach (Player player in players)
            state.AddPlayer(player);
        foreach (Player player in players)
        {
            player.ResetCombatState();
            player.PopulateCombatState(run.Rng.Shuffle, state);
            PlayerCombatState combat = player.PlayerCombatState
                ?? throw new InvalidOperationException("离线 Actor 没有战斗状态。");
            CardModel card = combat.DrawPile.Cards.First(candidate => candidate.Type == CardType.Attack);
            combat.DrawPile.RemoveInternal(card, silent: true);
            combat.Hand.AddInternal(card, silent: true);
            combat.Energy = player.MaxEnergy;
            combat.Phase = PlayerTurnPhase.Play;
        }

        MonsterModel sourceMonster = source.Enemies.FirstOrDefault()?.Monster
            ?? throw new InvalidOperationException("当前测试战斗没有可复用的怪物模型。");
        MonsterModel monster = ModelDb.GetById<MonsterModel>(sourceMonster.Id).ToMutable();
        Creature enemy = state.CreateCreature(monster, CombatSide.Enemy, slot: null);
        state.AddCreature(enemy);
        monster.SetUpForCombat();
        monster.RollMove(players.Select(static player => player.Creature));
        return CombatRootSnapshot.Capture(state);
    }
}
