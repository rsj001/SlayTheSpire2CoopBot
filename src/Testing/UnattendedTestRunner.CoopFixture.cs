using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
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
        AssertJointPotionChoiceCoverage(source);

        void AssertActorCount(int actorCount)
        {
            CombatRootSnapshot root = CreateOfflineJointRoot(source, actorCount);
            if (root.Actors.Count != actorCount || root.PlayerCount != actorCount
                || root.LocalActorId.Index != 0)
                throw new InvalidOperationException($"离线联合根未捕获 {actorCount} 个稳定 Actor。 ");
            if (root.Actors[0].HasRenewablePotionShapedRock
                || !root.Actors[1].HasRenewablePotionShapedRock
                || root.HasRenewablePotionShapedRock != root.Actors[0].HasRenewablePotionShapedRock
                || root.HasUnusedCardReplayAllocator != root.Actors[0].HasUnusedCardReplayAllocator
                || root.PostCombatRelicHeal != root.Actors[0].PostCombatRelicHeal
                || !root.Actors[1].SearchablePotions.Any(potion => potion.PotionId == "GAMBLERS_BREW"))
            {
                throw new InvalidOperationException(
                    "联合 Actor 遗物/药水根元数据没有保持逐 Actor 归属或单人兼容字段。");
            }

            CombatPredictionSimulator simulator = root.ForkSimulator();
            JointTurnState turns = JointTurnState.Start(actorCount, root.StartTurnNumber);
            IReadOnlyList<JointActionCandidate> candidates = JointActionExpander.Expand(simulator, turns);
            for (int index = 0; index < actorCount; index++)
            {
                CombatActorId actor = new(index);
                if (!candidates.Any(candidate => candidate.Action.Actor == actor
                        && candidate.Action.Kind == PlanActionKind.EndTurn)
                    || !candidates.Any(candidate => candidate.Action.Actor == actor
                        && candidate.Action.Kind == PlanActionKind.PlayCard)
                    || !candidates.Any(candidate => candidate.Action.Actor == actor
                        && candidate.Action.Kind == PlanActionKind.UsePotion))
                    throw new InvalidOperationException($"离线联合根缺少 {actor} 的出牌、药水或结束候选。");
            }
            PlanAction actorZeroPotion = candidates.First(candidate =>
                candidate.Action.Actor.Index == 0
                && candidate.Action.Kind == PlanActionKind.UsePotion).Action;
            if (actorZeroPotion.PotionId != "BLOCK_POTION" || actorZeroPotion.Choice != null)
                throw new InvalidOperationException("Actor0 的无选择药水候选身份错误。");
            PlanAction actorOnePotion = candidates.First(candidate =>
                candidate.Action.Actor.Index == 1
                && candidate.Action.Kind == PlanActionKind.UsePotion).Action;
            if (actorOnePotion.PotionId != "GAMBLERS_BREW"
                || actorOnePotion.Choice == null
                || actorOnePotion.Choice.Actor.Index != 1)
            {
                throw new InvalidOperationException("Actor1 的药水主选择没有保持槽位和 owner 身份。");
            }

            JointCombatSnapshot before = JointCombatSnapshot.Capture(root, simulator, turns);
            AssertRemoteActorStateKeyCoverage(root, before, turns, actorCount - 1);
            AssertRemoteActorPowerLifecycle(root, before, turns, actorCount - 1);
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

                JointOfflineSearchRequest disabledRequest = new(
                    [],
                    MaximumActions: 2,
                    MaximumStates: 2_000,
                    PotionPolicy: new JointPotionSearchPolicy(
                        SolverPotionPolicy.Disabled,
                        maximumUses: 0));
                AssertPolicySearch(disabledRequest, static actions =>
                    actions.All(action => action.Kind != PlanActionKind.UsePotion),
                    "Disabled 联合药水政策仍使用了药水");

                JointPotionSearchPolicy forcedPolicy = new(
                    SolverPotionPolicy.Disabled,
                    [new JointPotionSlotDirective(
                        new CombatActorId(1),
                        0,
                        "GAMBLERS_BREW",
                        SolverPotionDirective.Force)],
                    minimumUses: 1,
                    maximumUses: 1);
                JointOfflineSearchResult forcedResult = AssertPolicySearch(
                    new JointOfflineSearchRequest(
                        [],
                        MaximumActions: 2,
                        MaximumStates: 2_000,
                        PotionPolicy: forcedPolicy),
                    static actions => actions.Count(action =>
                        action.Kind == PlanActionKind.UsePotion
                        && action.Actor.Index == 1
                        && action.PotionSlot == 0
                        && action.PotionId == "GAMBLERS_BREW") == 1
                        && actions.Count(action => action.Kind == PlanActionKind.UsePotion) == 1,
                    "Actor1 强制药水政策没有精确满足");
                int expectedCost = PotionUsePolicy.StrategicHpCost("GAMBLERS_BREW");
                if (forcedPolicy.StrategicHpCost(
                        forcedResult.Actions,
                        root.Actors[1].HasRenewablePotionShapedRock) != expectedCost)
                {
                    throw new InvalidOperationException("联合药水政策没有复用单人战略成本。" );
                }

                JointOfflineSearchResult AssertPolicySearch(
                    JointOfflineSearchRequest policyRequest,
                    Func<IReadOnlyList<PlanAction>, bool> assertActions,
                    string message)
                {
                    JointOfflineSearchResult bfs = JointOfflineSearch.SolveBreadthFirst(root, policyRequest);
                    JointOfflineSearchResult dfs = JointOfflineSearch.SolveDepthFirstOracle(root, policyRequest);
                    if (!assertActions(bfs.Actions)
                        || JointObjectiveScore.Compare(bfs.Score, dfs.Score) != 0
                        || bfs.Snapshot.StateKey != dfs.Snapshot.StateKey)
                    {
                        throw new InvalidOperationException(message);
                    }
                    return bfs;
                }
            }
        }
    }

    private static void AssertRemoteActorPowerLifecycle(
        CombatRootSnapshot root,
        JointCombatSnapshot baseline,
        JointTurnState turns,
        int targetActorIndex)
    {
        CombatPredictionSimulator applied = root.ForkSimulator();
        SimulatedCombatState appliedCombat = (SimulatedCombatState)applied.State.CombatState;
        Player target = applied.State.Players[targetActorIndex];
        Player applier = applied.State.Players[0];
        appliedCombat.Apply<StrengthPower>(target.Creature, 2, applier.Creature);
        PowerLifecycleSupport.ResolvePowerAmountChanges(applied, appliedCombat);
        JointCombatSnapshot first = JointCombatSnapshot.Capture(root, applied, turns);
        if (first.StateKey == baseline.StateKey
            || first.Continuation.StateText == baseline.Continuation.StateText)
        {
            throw new InvalidOperationException("远端 Actor Power 创建没有进入联合状态键和续用戳。");
        }

        CombatPredictionSimulator stacked = applied.Fork();
        SimulatedCombatState stackedCombat = (SimulatedCombatState)stacked.State.CombatState;
        stackedCombat.Apply<StrengthPower>(target.Creature, 3, applier.Creature);
        PowerLifecycleSupport.ResolvePowerAmountChanges(stacked, stackedCombat);
        JointCombatSnapshot second = JointCombatSnapshot.Capture(root, stacked, turns);
        if (second.StateKey == first.StateKey
            || second.Continuation.StateText == first.Continuation.StateText)
        {
            throw new InvalidOperationException("远端 Actor Power 叠加没有改变联合状态。");
        }

        CombatPredictionSimulator removed = stacked.Fork();
        SimulatedCombatState removedCombat = (SimulatedCombatState)removed.State.CombatState;
        removedCombat.SetAmount<StrengthPower>(target.Creature, 0);
        PowerLifecycleSupport.ResolvePowerAmountChanges(removed, removedCombat);
        JointCombatSnapshot third = JointCombatSnapshot.Capture(root, removed, turns);
        JointCombatSnapshot parentAgain = JointCombatSnapshot.Capture(root, applied, turns);
        if (third.StateKey == second.StateKey
            || parentAgain.StateKey != first.StateKey
            || third.Actors[0] != baseline.Actors[0])
        {
            throw new InvalidOperationException(
                "远端 Actor Power 移除、Fork 父隔离或兄弟 Actor 所有权错误。");
        }
    }

    private static void AssertJointPotionChoiceCoverage(CombatState source)
    {
        PotionModel[] choicePotions =
        [
            CanonicalModels.Potion<AttackPotion>(),
            CanonicalModels.Potion<SkillPotion>(),
            CanonicalModels.Potion<PowerPotion>(),
            CanonicalModels.Potion<ColorlessPotion>(),
            CanonicalModels.Potion<Ashwater>(),
            CanonicalModels.Potion<DropletOfPrecognition>(),
            CanonicalModels.Potion<GamblersBrew>(),
            CanonicalModels.Potion<LiquidMemories>(),
            CanonicalModels.Potion<TouchOfInsanity>(),
        ];
        foreach (PotionModel canonical in choicePotions)
        {
            CombatRootSnapshot root = CreateOfflineJointRoot(source, 2, canonical);
            CombatPredictionSimulator simulator = root.ForkSimulator();
            IReadOnlyList<JointActionCandidate> candidates = JointActionExpander.Expand(
                simulator,
                JointTurnState.Start(2, root.StartTurnNumber));
            PlanAction[] potionActions = candidates
                .Select(static candidate => candidate.Action)
                .Where(action => action.Actor.Index == 1
                    && action.Kind == PlanActionKind.UsePotion
                    && string.Equals(action.PotionId, canonical.Id.Entry, StringComparison.Ordinal))
                .ToArray();
            if (potionActions.Length == 0
                || potionActions.Any(action => action.Choice == null
                    || action.Choice.Actor.Index != 1))
            {
                throw new InvalidOperationException(
                    $"联合药水 {canonical.Id.Entry} 没有生成 Actor1 所有的主选择分支。");
            }
            JointReplayResult replay = JointPlanReplayer.Replay(
                root,
                new JointPlan(2, [potionActions[0]]));
            if (replay.AppliedActions.Count != 1
                || replay.AppliedActions[0].Choice?.Actor.Index != 1)
            {
                throw new InvalidOperationException(
                    $"联合药水 {canonical.Id.Entry} 的主选择不能严格回放。");
            }
        }

        CombatRootSnapshot entropicRoot = CreateOfflineJointRoot(
            source,
            2,
            CanonicalModels.Potion<EntropicBrew>());
        CombatPredictionSimulator beforeSimulator = entropicRoot.ForkSimulator();
        JointTurnState turns = JointTurnState.Start(2, entropicRoot.StartTurnNumber);
        JointCombatSnapshot before = JointCombatSnapshot.Capture(entropicRoot, beforeSimulator, turns);
        PlanAction entropic = JointActionExpander.Expand(beforeSimulator, turns)
            .Select(static candidate => candidate.Action)
            .First(action => action.Actor.Index == 1
                && action.Kind == PlanActionKind.UsePotion
                && action.PotionId == "ENTROPIC_BREW");
        JointReplayResult generated = JointPlanReplayer.Replay(
            entropicRoot,
            new JointPlan(2, [entropic]));
        if (generated.Snapshot.StateKey == before.StateKey
            || generated.Snapshot.Continuation.StateText == before.Continuation.StateText)
        {
            throw new InvalidOperationException("联合 Entropic Brew 没有改变药水槽状态键和续用戳。");
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

    private static CombatRootSnapshot CreateOfflineJointRoot(
        CombatState source,
        int actorCount,
        PotionModel? remotePotion = null)
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
        for (int index = 0; index < players.Length; index++)
        {
            Player player = players[index];
            player.ResetCombatState();
            player.PopulateCombatState(run.Rng.Shuffle, state);
            PlayerCombatState combat = player.PlayerCombatState
                ?? throw new InvalidOperationException("离线 Actor 没有战斗状态。");
            CardModel card = combat.DrawPile.Cards.First(candidate => candidate.Type == CardType.Attack);
            combat.DrawPile.RemoveInternal(card, silent: true);
            combat.Hand.AddInternal(card, silent: true);
            CardModel discard = combat.DrawPile.Cards.First();
            combat.DrawPile.RemoveInternal(discard, silent: true);
            combat.DiscardPile.AddInternal(discard, silent: true);
            combat.Energy = player.MaxEnergy;
            combat.Phase = PlayerTurnPhase.Play;
            PotionModel potion = PredictionUtils.CreatePotion(
                index == 0
                    ? CanonicalModels.Potion<BlockPotion>()
                    : remotePotion ?? CanonicalModels.Potion<GamblersBrew>(),
                player);
            if (!player.AddPotionInternal(potion, 0, silent: true).success)
                throw new InvalidOperationException($"无法为离线 Actor{index} 注入药水。");
            if (index == 1)
                player.AddRelicInternal(ModelDb.Relic<PetrifiedToad>().ToMutable(), silent: true);
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
