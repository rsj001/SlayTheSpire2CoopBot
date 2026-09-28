using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Models.Singleton;
using MegaCrit.Sts2.Core.Modding;
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
        AssertRemoteActorRelicTrigger(source);
        AssertRemoteActorTeamPower(source);
        AssertRemoteActorRelicConsumption(source);
        AssertCharacterOwnedGeneratedState(source);
        AssertThirdPartySubscriberBoundary();

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
            AssertCharacterResources(root, before, turns);
            AssertMultiplayerBlockScaling(root, actorCount);
            if (actorCount == 2)
            {
                CombatRootSnapshot choiceRoot = CreateOfflineJointRoot(
                    source,
                    2,
                    CanonicalModels.Potion<GamblersBrew>(),
                    localPotion: CanonicalModels.Potion<GamblersBrew>());
                AssertJointChoiceContinuation(
                    choiceRoot,
                    JointTurnState.Start(2, choiceRoot.StartTurnNumber));
                AssertJointPlayerEndBarrier(root);
                AssertDeadActorBarrier(root);
                AssertBasicEnemySide(root);
                AssertBasicNextPlayerSide(root);
            }
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
                JointOfflineSearchResult beam = JointOfflineSearch.SolveBeam(
                    root,
                    JointOfflineSearchRequest.Default(maximumActions: 2, maximumStates: 2_000),
                    beamWidth: 2_000);
                if (beam.Termination != JointSearchTermination.Completed
                    || JointObjectiveScore.Compare(beam.Score, oracle.Score) != 0
                    || beam.Snapshot.StateKey != oracle.Snapshot.StateKey
                    || ComparePlanActions(beam.Actions, oracle.Actions) != 0)
                {
                    throw new InvalidOperationException(
                        "联合 Beam 与 DFS oracle 的最优值、动作序或终局状态不一致。");
                }
                JointOfflineSearchResult bfws = JointOfflineSearch.SolveBfws(
                    root,
                    JointOfflineSearchRequest.Default(maximumActions: 2, maximumStates: 2_000),
                    maximumOpen: 2_000);
                if (bfws.Termination != JointSearchTermination.Completed
                    || JointObjectiveScore.Compare(bfws.Score, oracle.Score) != 0
                    || bfws.Snapshot.StateKey != oracle.Snapshot.StateKey
                    || ComparePlanActions(bfws.Actions, oracle.Actions) != 0)
                {
                    throw new InvalidOperationException(
                        "联合 BFWS 与 DFS oracle 的最优值、动作序或终局状态不一致。");
                }
                JointOfflineSearchResult repeatedBeam = JointOfflineSearch.SolveBeam(
                    root,
                    JointOfflineSearchRequest.Default(maximumActions: 2, maximumStates: 2_000),
                    beamWidth: 2_000,
                    degreeOfParallelism: 4);
                if (JointObjectiveScore.Compare(beam.Score, repeatedBeam.Score) != 0
                    || beam.Snapshot.StateKey != repeatedBeam.Snapshot.StateKey
                    || ComparePlanActions(beam.Actions, repeatedBeam.Actions) != 0
                    || beam.ExpandedStates != repeatedBeam.ExpandedStates)
                {
                    throw new InvalidOperationException("联合 Beam 串行与固定 lane 运行不一致。");
                }
                JointOfflineSearchResult budgeted = JointOfflineSearch.SolveBeam(
                    root,
                    JointOfflineSearchRequest.Default(maximumActions: 2, maximumStates: 1),
                    beamWidth: 8);
                if (budgeted.Termination != JointSearchTermination.StateBudget
                    || budgeted.ExpandedStates != 1)
                {
                    throw new InvalidOperationException("联合 Beam 未严格遵守共享状态预算。");
                }
                using (CancellationTokenSource cancelled = new())
                {
                    cancelled.Cancel();
                    try
                    {
                        _ = JointOfflineSearch.SolveBeam(
                            root,
                            JointOfflineSearchRequest.Default(maximumActions: 2, maximumStates: 2_000),
                            beamWidth: 8,
                            cancelled.Token);
                        throw new InvalidOperationException("联合 Beam 忽略了预取消请求。");
                    }
                    catch (OperationCanceledException) when (cancelled.IsCancellationRequested)
                    {
                    }
                }

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
            else if (actorCount == 4)
            {
                JointOfflineSearchRequest oneAction = JointOfflineSearchRequest.Default(
                    maximumActions: 1,
                    maximumStates: 2_000);
                JointOfflineSearchResult fourActorOracle =
                    JointOfflineSearch.SolveDepthFirstOracle(root, oneAction);
                JointOfflineSearchResult fourActorBeam =
                    JointOfflineSearch.SolveBeam(root, oneAction, beamWidth: 2_000);
                JointOfflineSearchResult fourActorBfws =
                    JointOfflineSearch.SolveBfws(root, oneAction, maximumOpen: 2_000);
                if (JointObjectiveScore.Compare(fourActorBeam.Score, fourActorOracle.Score) != 0
                    || fourActorBeam.Snapshot.StateKey != fourActorOracle.Snapshot.StateKey
                    || ComparePlanActions(fourActorBeam.Actions, fourActorOracle.Actions) != 0)
                {
                    throw new InvalidOperationException(
                        "四 Actor 一层 Beam 与 DFS oracle 的最优值、动作序或状态不一致。");
                }
                if (JointObjectiveScore.Compare(fourActorBfws.Score, fourActorOracle.Score) != 0
                    || fourActorBfws.Snapshot.StateKey != fourActorOracle.Snapshot.StateKey
                    || ComparePlanActions(fourActorBfws.Actions, fourActorOracle.Actions) != 0)
                {
                    throw new InvalidOperationException(
                        "四 Actor 一层 BFWS 与 DFS oracle 的最优值、动作序或状态不一致。");
                }
            }
        }
    }

    private static void AssertBasicEnemySide(CombatRootSnapshot root)
    {
        CombatPredictionSimulator simulator = root.ForkSimulator();
        JointTurnState turns = JointTurnState.Start(2, root.StartTurnNumber)
            .EndTurn(new CombatActorId(0))
            .EndTurn(new CombatActorId(1));
        ForkableSet<uint> deaths = JointActionTransition.CaptureProcessedEnemyDeaths(root, simulator);
        JointRoundTransition.CompletePlayerSide(simulator, turns, deaths);
        int[] hpBefore = simulator.State.Players
            .Select(player => simulator.State.GetCreature(player.Creature).CurrentHp)
            .ToArray();
        JointRoundTransition.CompleteBasicEnemySide(simulator, deaths);
        int[] hpAfter = simulator.State.Players
            .Select(player => simulator.State.GetCreature(player.Creature).CurrentHp)
            .ToArray();
        if (hpAfter.Where((hp, index) => hp >= hpBefore[index]).Any()
            || hpBefore[0] - hpAfter[0] != hpBefore[1] - hpAfter[1])
        {
            throw new InvalidOperationException(
                $"纯攻击敌方轮未对全部 Actor 同序结算：before={string.Join(',', hpBefore)} " +
                $"after={string.Join(',', hpAfter)}。");
        }
    }

    private static void AssertBasicNextPlayerSide(CombatRootSnapshot root)
    {
        CombatPredictionSimulator simulator = root.ForkSimulator();
        JointTurnState turns = JointTurnState.Start(2, root.StartTurnNumber)
            .EndTurn(new CombatActorId(0))
            .EndTurn(new CombatActorId(1));
        ForkableSet<uint> deaths = JointActionTransition.CaptureProcessedEnemyDeaths(root, simulator);
        JointRoundTransition.CompletePlayerSide(simulator, turns, deaths);
        JointRoundTransition.CompleteBasicEnemySide(simulator, deaths);
        int roundBefore = ((SimulatedCombatState)simulator.State.CombatState).RoundNumber;
        int[] turnsBefore = simulator.State.Players
            .Select(player => ((SimulatedCombatState)simulator.State.CombatState).GetPlayerTurnNumber(player))
            .ToArray();
        JointTurnState next = JointRoundTransition.StartBasicPlayerSide(simulator, turns, deaths);
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        if (next.Turn != turns.Turn + 1
            || !next.Phases.All(static phase => phase == JointActorTurnPhase.Playing)
            || combat.RoundNumber != roundBefore + 1)
        {
            throw new InvalidOperationException("基础下一玩家轮未推进联合屏障或共享轮数。");
        }
        for (int index = 0; index < simulator.State.Players.Count; index++)
        {
            Player player = simulator.State.Players[index];
            SimPlayerCombatState state = simulator.State.GetPlayerCombatState(player);
            if (combat.GetPlayerTurnNumber(player) != turnsBefore[index] + 1
                || state.Hand.Cards.Count == 0
                || state.Energy <= 0)
            {
                throw new InvalidOperationException(
                    $"Actor{index} 下一轮资源未恢复：turn={combat.GetPlayerTurnNumber(player)} " +
                    $"hand={state.Hand.Cards.Count} energy={state.Energy}。");
            }
        }
    }

    private static void AssertDeadActorBarrier(CombatRootSnapshot root)
    {
        CombatPredictionSimulator simulator = root.ForkSimulator();
        JointTurnState turns = JointTurnState.Start(2, root.StartTurnNumber);
        Player dead = simulator.State.Players[1];
        if (!simulator.Kill(dead.Creature, force: true))
            throw new InvalidOperationException("死亡 Actor 屏障夹具没有完成强制死亡结算。");
        turns = turns.MarkDead(new CombatActorId(1));
        if (JointActionExpander.Expand(simulator, turns)
            .Any(static candidate => candidate.Action.Actor.Index == 1))
        {
            throw new InvalidOperationException("死亡 Actor 仍产生联合候选。");
        }
        ForkableSet<uint> deaths = JointActionTransition.CaptureProcessedEnemyDeaths(root, simulator);
        turns = JointActionTransition.Apply(
            simulator,
            turns,
            new PlanAction(PlanActionKind.EndTurn, turns.Turn, Actor: new CombatActorId(0)),
            deaths);
        if (!turns.IsBarrierReached)
            throw new InvalidOperationException("死亡 Actor 仍阻塞联合结束屏障。");
        JointRoundTransition.CompletePlayerSide(simulator, turns, deaths);
        if (simulator.State.GetPlayerCombatState(simulator.State.Players[0]).Phase
                != PlayerTurnPhase.None
            || simulator.State.GetCreature(dead.Creature).IsAlive)
        {
            throw new InvalidOperationException("死亡 Actor 屏障后的玩家侧状态错误。");
        }
    }

    private static void AssertJointPlayerEndBarrier(CombatRootSnapshot root)
    {
        CombatPredictionSimulator simulator = root.ForkSimulator();
        JointTurnState turns = JointTurnState.Start(2, root.StartTurnNumber);
        ForkableSet<uint> deaths = JointActionTransition.CaptureProcessedEnemyDeaths(root, simulator);
        turns = JointActionTransition.Apply(
            simulator,
            turns,
            new PlanAction(PlanActionKind.EndTurn, turns.Turn, Actor: new CombatActorId(0)),
            deaths);
        if (turns.IsBarrierReached
            || simulator.State.GetPlayerCombatState(simulator.State.Players[0]).Phase
                != PlayerTurnPhase.Play)
        {
            throw new InvalidOperationException("单个 Actor EndTurn 提前触发了共享玩家侧结算。");
        }
        turns = JointActionTransition.Apply(
            simulator,
            turns,
            new PlanAction(PlanActionKind.EndTurn, turns.Turn, Actor: new CombatActorId(1)),
            deaths);
        JointRoundTransition.CompletePlayerSide(simulator, turns, deaths);
        if (simulator.State.Players.Any(player =>
                simulator.State.GetPlayerCombatState(player).Phase != PlayerTurnPhase.None))
        {
            throw new InvalidOperationException("全员屏障后的玩家侧 PhaseTwo 未完整结束。");
        }
    }

    private static void AssertJointChoiceContinuation(
        CombatRootSnapshot root,
        JointTurnState turns)
    {
        JointPendingChoiceFrame[] frames = new JointPendingChoiceFrame[2];
        for (int index = 0; index < frames.Length; index++)
        {
            PlanAction action = new(
                PlanActionKind.UsePotion,
                turns.Turn,
                PotionSlot: 0,
                PotionId: "GAMBLERS_BREW",
                Actor: new CombatActorId(index));
            CombatPredictionSimulator probe = root.ForkSimulator();
            try
            {
                _ = JointActionTransition.Apply(
                    probe,
                    turns,
                    action,
                    JointActionTransition.CaptureProcessedEnemyDeaths(root, probe));
                throw new InvalidOperationException("缺少选择的联合药水没有产生 pending frame。");
            }
            catch (JointPendingActionChoiceException pending)
            {
                frames[index] = pending.Frame;
            }
        }

        JointChoiceContinuation continuation = JointChoiceContinuation.Empty
            .Enqueue(frames[0])
            .Enqueue(frames[1]);
        try
        {
            _ = continuation.Enqueue(frames[0]);
            throw new InvalidOperationException("同 Actor 的第二个 pending frame 未被拒绝。");
        }
        catch (InvalidOperationException exception) when (
            exception.Message.Contains("已有待处理", StringComparison.Ordinal))
        {
        }
        try
        {
            _ = continuation.Consume(frames[1].OwnerActor, frames[1].SourceAction, out _);
            throw new InvalidOperationException("联合选择允许后置 Actor 抢先消费。");
        }
        catch (InvalidOperationException exception) when (
            exception.Message.Contains("按原序", StringComparison.Ordinal))
        {
        }
        continuation = continuation.Consume(
            frames[0].OwnerActor,
            frames[0].SourceAction,
            out JointPendingChoiceFrame first);
        continuation = continuation.Consume(
            frames[1].OwnerActor,
            frames[1].SourceAction,
            out JointPendingChoiceFrame second);
        if (first.OwnerActor.Index != 0 || second.OwnerActor.Index != 1
            || continuation.Frames.Count != 0)
            throw new InvalidOperationException("联合选择 continuation 未按 Actor 原序耗尽。");
    }

    private static void AssertThirdPartySubscriberBoundary()
    {
        var previousMocks = AssemblyInfo.MockTypes;
        AssemblyInfo.MockTypes = previousMocks == null ? [] : new(previousMocks);
        try
        {
            PredictionModHookSubscriberCapture.ValidateSubscriberForTesting(
                (AbstractModel)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
                    typeof(MapOnlySubscriber)),
                "coop-run");
            AssemblyInfo.MockTypes[typeof(MapAndCombatSubscriber)] = (null, false);
            PredictionModHookSubscriberCapture.ValidateSubscriberForTesting(
                (AbstractModel)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
                    typeof(MapAndCombatSubscriber)),
                "coop-combat");
            throw new InvalidOperationException("未知 gameplay subscriber 被联合根静默放行。");
        }
        catch (PredictionUnsupportedException exception)
        {
            if (!exception.Message.Contains(nameof(MapAndCombatSubscriber), StringComparison.Ordinal)
                || !exception.Message.Contains("coop-combat", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "未知 gameplay subscriber 拒绝缺少类型或 scope 上下文。",
                    exception);
            }
        }
        finally
        {
            AssemblyInfo.MockTypes = previousMocks;
        }
    }

    private static void AssertCharacterOwnedGeneratedState(CombatState source)
    {
        CharacterModel[] roster =
        [
            ModelDb.Character<Ironclad>(),
            ModelDb.Character<Silent>(),
            ModelDb.Character<Necrobinder>(),
        ];
        CombatRootSnapshot root = CreateOfflineJointRoot(
            source,
            roster.Length,
            includeCharacterMechanismFixture: true,
            characterRoster: roster);
        JointTurnState turns = JointTurnState.Start(roster.Length, root.StartTurnNumber);

        CombatPredictionSimulator silentProbe = root.ForkSimulator();
        PlanAction bladeDance = JointActionExpander.Expand(silentProbe, turns)
            .Select(static candidate => candidate.Action)
            .Single(static action => action.Actor.Index == 1
                && action.Kind == PlanActionKind.PlayCard
                && action.CardId == "BLADE_DANCE");
        JointReplayResult shivs = JointPlanReplayer.Replay(
            root,
            new JointPlan(roster.Length, [bladeDance]));
        for (int index = 0; index < roster.Length; index++)
        {
            int count = shivs.Snapshot.Simulator.State
                .GetPlayerCombatState(shivs.Snapshot.Simulator.State.Players[index])
                .Hand.Cards.Count(card => card.Preview is Shiv);
            if ((index == 1 && count == 0) || (index != 1 && count != 0))
                throw new InvalidOperationException("远端 Silent 生成的 Shiv 污染了其他 Actor 手牌。");
        }

        CombatPredictionSimulator necroProbe = root.ForkSimulator();
        PlanAction afterlife = JointActionExpander.Expand(necroProbe, turns)
            .Select(static candidate => candidate.Action)
            .Single(static action => action.Actor.Index == 2
                && action.Kind == PlanActionKind.PlayCard
                && action.CardId == "AFTERLIFE");
        JointReplayResult summoned = JointPlanReplayer.Replay(
            root,
            new JointPlan(roster.Length, [afterlife]));
        SimulatedCombatState combat =
            (SimulatedCombatState)summoned.Snapshot.Simulator.State.CombatState;
        if (combat.GetOsty(summoned.Snapshot.Simulator.State.Players[2]) is not { } osty
            || summoned.Snapshot.Simulator.State.GetCreature(osty).IsDead
            || combat.GetOsty(summoned.Snapshot.Simulator.State.Players[0]) != null
            || combat.GetOsty(summoned.Snapshot.Simulator.State.Players[1]) != null)
        {
            throw new InvalidOperationException("远端 Necrobinder 的实际召唤没有保持 Osty 所有权。");
        }
    }

    private static void AssertRemoteActorRelicConsumption(CombatState source)
    {
        CombatRootSnapshot root = CreateOfflineJointRoot(
            source,
            2,
            includeRelicConsumptionFixture: true);
        CombatPredictionSimulator parent = root.ForkSimulator();
        SimulatedCombatState parentCombat = (SimulatedCombatState)parent.State.CombatState;
        ThrowingAxe parentLocal = parentCombat.RelicsOf(parent.State.Players[0])
            .OfType<ThrowingAxe>().Single();
        ThrowingAxe parentRemote = parentCombat.RelicsOf(parent.State.Players[1])
            .OfType<ThrowingAxe>().Single();
        JointTurnState turns = JointTurnState.Start(2, root.StartTurnNumber);
        PlanAction attack = JointActionExpander.Expand(parent, turns)
            .Select(static candidate => candidate.Action)
            .First(static action => action.Actor.Index == 1
                && action.Kind == PlanActionKind.PlayCard);
        JointReplayResult replay = JointPlanReplayer.Replay(root, new JointPlan(2, [attack]));
        CombatPredictionSimulator child = replay.Snapshot.Simulator;
        SimulatedCombatState childCombat = (SimulatedCombatState)child.State.CombatState;
        ThrowingAxe childLocal = childCombat.RelicsOf(child.State.Players[0])
            .OfType<ThrowingAxe>().Single();
        ThrowingAxe childRemote = childCombat.RelicsOf(child.State.Players[1])
            .OfType<ThrowingAxe>().Single();
        if (!RelicPredictionStateSupport.IsThrowingAxeUsed(child, childRemote)
            || RelicPredictionStateSupport.IsThrowingAxeUsed(child, childLocal)
            || RelicPredictionStateSupport.IsThrowingAxeUsed(parent, parentLocal)
            || RelicPredictionStateSupport.IsThrowingAxeUsed(parent, parentRemote))
        {
            throw new InvalidOperationException(
                "远端 Actor 投掷斧消耗污染了同型队友遗物或父 Fork。");
        }
    }

    private static void AssertRemoteActorTeamPower(CombatState source)
    {
        CombatRootSnapshot root = CreateOfflineJointRoot(
            source,
            4,
            includeRemoteTeamPowerFixture: true);
        CombatPredictionSimulator beforeSimulator = root.ForkSimulator();
        JointTurnState turns = JointTurnState.Start(4, root.StartTurnNumber);
        JointCombatSnapshot before = JointCombatSnapshot.Capture(root, beforeSimulator, turns);
        PlanAction action = JointActionExpander.Expand(beforeSimulator, turns)
            .Select(static candidate => candidate.Action)
            .Single(static action => action.Actor.Index == 1
                && action.Kind == PlanActionKind.PlayCard
                && action.CardId == "ONE_FOR_ALL");
        JointReplayResult replay = JointPlanReplayer.Replay(root, new JointPlan(4, [action]));
        SimulatedCombatState combat =
            (SimulatedCombatState)replay.Snapshot.Simulator.State.CombatState;
        int expected = ModelDb.Card<OneForAll>().DynamicVars["OneForAllPower"].IntValue;
        Player applier = replay.Snapshot.Simulator.State.Players[1];
        foreach (Player player in replay.Snapshot.Simulator.State.Players)
        {
            OneForAllPower power = combat.GetPower<OneForAllPower>(player.Creature)
                ?? throw new InvalidOperationException("万众一心没有为全部 Actor 创建 Power。");
            if (power.Amount != expected || !ReferenceEquals(power.Applier, applier.Creature))
            {
                throw new InvalidOperationException(
                    $"万众一心 Actor Power 来源/数值错误：amount={power.Amount} expected={expected}。");
            }
        }
        if (replay.Snapshot.StateKey == before.StateKey
            || replay.Snapshot.Continuation.StateText == before.Continuation.StateText)
        {
            throw new InvalidOperationException("万众一心的全队 Power 未进入联合状态。");
        }
    }

    private static void AssertRemoteActorRelicTrigger(CombatState source)
    {
        CombatRootSnapshot root = CreateOfflineJointRoot(
            source,
            2,
            includeRemoteRelicTriggerFixture: true);
        CombatPredictionSimulator parent = root.ForkSimulator();
        JointTurnState turns = JointTurnState.Start(2, root.StartTurnNumber);
        SimulatedCombatState parentCombat = (SimulatedCombatState)parent.State.CombatState;
        Player remote = parent.State.Players[1];
        Shuriken parentRelic = parentCombat.RelicsOf(remote).OfType<Shuriken>().Single();
        JointCombatSnapshot before = JointCombatSnapshot.Capture(root, parent, turns);
        CombatPredictionSimulator child = parent.Fork();
        JointTurnState childTurns = turns;
        ForkableSet<uint> deaths = JointActionTransition.CaptureProcessedEnemyDeaths(root, child);
        List<PlanAction> attacks = [];
        for (int attack = 0; attack < 3; attack++)
        {
            PlanAction action = JointActionExpander.Expand(child, childTurns)
                .Select(static candidate => candidate.Action)
                .FirstOrDefault(static action => action.Actor.Index == 1
                    && action.Kind == PlanActionKind.PlayCard)
                ?? throw new InvalidOperationException(
                    $"远端 Actor 手里剑夹具缺少第 {attack + 1} 张可回放攻击牌。");
            attacks.Add(action);
            childTurns = JointActionTransition.Apply(child, childTurns, action, deaths);
        }
        JointCombatSnapshot after = JointCombatSnapshot.Capture(root, child, childTurns);
        JointReplayResult fullReplay = JointPlanReplayer.Replay(root, new JointPlan(2, attacks));
        if (fullReplay.Snapshot.StateKey != after.StateKey
            || fullReplay.Snapshot.Continuation.StateText != after.Continuation.StateText
            || attacks.Any(static action => action.CardOccurrence != 0
                || action.CardStateOccurrence != 0))
        {
            throw new InvalidOperationException(
                "同名卡牌的前缀相对 occurrence 未能从原根严格回放到增量状态。");
        }
        SimulatedCombatState childCombat = (SimulatedCombatState)child.State.CombatState;
        Player childRemote = child.State.Players[1];
        Shuriken childRelic = childCombat.RelicsOf(childRemote).OfType<Shuriken>().Single();
        int remoteStrength = childCombat.GetAmount<StrengthPower>(childRemote.Creature);
        int localStrength = childCombat.GetAmount<StrengthPower>(child.State.Players[0].Creature);
        int childCounter = RelicPredictionStateSupport.GetCounterValue(
            child,
            childRelic,
            childRelic._attacksPlayedThisTurn);
        bool stateChanged = after.StateKey != before.StateKey;
        bool continuationChanged = after.Continuation.StateText != before.Continuation.StateText;
        if (remoteStrength != childRelic.DynamicVars.Strength.IntValue
            || localStrength != 0
            || childCounter != 3
            || after.StateKey == before.StateKey
            || after.Continuation.StateText == before.Continuation.StateText)
        {
            throw new InvalidOperationException(
                $"远端 Actor 手里剑错误：remoteStrength={remoteStrength} " +
                $"localStrength={localStrength} counter={childCounter} " +
                $"stateChanged={stateChanged} continuationChanged={continuationChanged}。");
        }
        if (parentCombat.GetAmount<StrengthPower>(remote.Creature) != 0
            || RelicPredictionStateSupport.GetCounterValue(
                parent,
                parentRelic,
                parentRelic._attacksPlayedThisTurn) != 0)
        {
            throw new InvalidOperationException("远端 Actor 遗物回放污染了父 Fork。");
        }
    }

    private static void AssertCharacterResources(
        CombatRootSnapshot root,
        JointCombatSnapshot baseline,
        JointTurnState turns)
    {
        AssertMutation(1, "orb", simulator =>
            simulator.OrbChannel<LightningOrb>(simulator.State.Players[1]));
        if (root.Actors.Count >= 3)
        {
            AssertMutation(2, "stars", simulator =>
                simulator.GainStars(simulator.State.Players[2], 2));
        }
        if (root.Actors.Count >= 4)
        {
            AssertMutation(3, "osty", simulator =>
            {
                SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
                combat.SummonOsty(simulator, simulator.State.Players[3], 5);
                PowerLifecycleSupport.ResolvePowerAmountChanges(simulator, combat);
            });
        }

        void AssertMutation(
            int actorIndex,
            string resource,
            Action<CombatPredictionSimulator> mutate)
        {
            CombatPredictionSimulator fork = root.ForkSimulator();
            mutate(fork);
            JointCombatSnapshot changed = JointCombatSnapshot.Capture(root, fork, turns);
            if (changed.StateKey == baseline.StateKey
                || changed.Continuation.StateText == baseline.Continuation.StateText)
            {
                throw new InvalidOperationException(
                    $"Actor{actorIndex} 的角色资源 {resource} 未进入联合状态键和续用戳。");
            }
            for (int index = 0; index < changed.Actors.Count; index++)
            {
                if (index != actorIndex && changed.Actors[index] != baseline.Actors[index])
                    throw new InvalidOperationException(
                        $"Actor{actorIndex} 的角色资源 {resource} 污染了 Actor{index}。");
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
        PotionModel? remotePotion = null,
        bool includeRemoteRelicTriggerFixture = false,
        bool includeRemoteTeamPowerFixture = false,
        bool includeRelicConsumptionFixture = false,
        bool includeCharacterMechanismFixture = false,
        IReadOnlyList<CharacterModel>? characterRoster = null,
        PotionModel? localPotion = null)
    {
        if (actorCount is < 1 or > 4)
            throw new ArgumentOutOfRangeException(nameof(actorCount));
        if (characterRoster != null && characterRoster.Count != actorCount)
            throw new ArgumentException("显式角色 roster 数量必须与 Actor 数一致。", nameof(characterRoster));
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
            CharacterModel character = characterRoster?[index] ?? index switch
            {
                0 => ModelDb.Character<Ironclad>(),
                1 => ModelDb.Character<Defect>(),
                2 => ModelDb.Character<Regent>(),
                3 => ModelDb.Character<Necrobinder>(),
                _ => throw new ArgumentOutOfRangeException(nameof(index)),
            };
            players[index] = Player.CreateForNewRun(character, liveLocal.UnlockState, netId);
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
            CardModel card = combat.DrawPile.Cards.First(candidate =>
                candidate.Type == CardType.Attack
                && candidate.Id.Entry.StartsWith("STRIKE_", StringComparison.Ordinal));
            combat.DrawPile.RemoveInternal(card, silent: true);
            combat.Hand.AddInternal(card, silent: true);
            CardModel discard = combat.DrawPile.Cards.First();
            combat.DrawPile.RemoveInternal(discard, silent: true);
            combat.DiscardPile.AddInternal(discard, silent: true);
            combat.Energy = player.MaxEnergy;
            combat.Phase = PlayerTurnPhase.Play;
            PotionModel potion = PredictionUtils.CreatePotion(
                index == 0
                    ? localPotion ?? CanonicalModels.Potion<BlockPotion>()
                    : remotePotion ?? CanonicalModels.Potion<GamblersBrew>(),
                player);
            if (!player.AddPotionInternal(potion, 0, silent: true).success)
                throw new InvalidOperationException($"无法为离线 Actor{index} 注入药水。");
            if (index == 1)
            {
                player.AddRelicInternal(ModelDb.Relic<PetrifiedToad>().ToMutable(), silent: true);
                if (includeRemoteRelicTriggerFixture)
                {
                    player.AddRelicInternal(ModelDb.Relic<Shuriken>().ToMutable(), silent: true);
                    CardModel canonicalAttack = ModelDb.GetById<CardModel>(card.Id);
                    for (int copy = 0; copy < 2; copy++)
                        combat.Hand.AddInternal(state.CreateCard(canonicalAttack, player), silent: true);
                }
                if (includeRemoteTeamPowerFixture)
                    combat.Hand.AddInternal(state.CreateCard(ModelDb.Card<OneForAll>(), player), silent: true);
            }
            if (includeRelicConsumptionFixture)
                player.AddRelicInternal(ModelDb.Relic<ThrowingAxe>().ToMutable(), silent: true);
            if (includeCharacterMechanismFixture && index == 1)
                combat.Hand.AddInternal(state.CreateCard(ModelDb.Card<BladeDance>(), player), silent: true);
            if (includeCharacterMechanismFixture && index == 2)
                combat.Hand.AddInternal(state.CreateCard(ModelDb.Card<Afterlife>(), player), silent: true);
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
