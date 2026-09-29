using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Encounters;
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
using System.Reflection;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private static string _coopWorkloadEvidence = "JointWorkload:NotRun";

    private sealed record OfflineJointCombat(
        CombatState State,
        IReadOnlyList<Player> Players,
        Creature Enemy);

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
        AssertTargetOnlyEnemyMove(source);
        AssertMixedEnemyMove(source);
        AssertSplitMixedEnemyMove(source);
        AssertOwnerRemovalEnemyMove(source);
        AssertPreAttackSummonEnemyMove(source);
        AssertEnemyRevive(source);
        AssertEnemyEscape(source);
        AssertRemoteActorExtraTurn(source);
        AssertOwnerOnlyEnemyRng(source);
        AssertAllActorsDeadTerminal(source);
        AssertCompleteStrictReplay(source, 2);
        AssertCompleteStrictReplay(source, 4);
        AssertFourActorBoundedWorkload(source);
        AssertJointSearchLifetime(source);

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
                || root.PotionRewardOutlook != root.Actors[0].PotionRewardOutlook
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
                AssertOwnerOnlyEnemyMove(root);
                AssertBasicNextPlayerSide(root);
                AssertTurnStartChoiceContinuation(root);
                AssertEndTurnPowerChoiceContinuation(root);
                AssertEndTurnRelicChoiceContinuation(root);
                AssertRepeatedAutoPlayChoiceContinuation(root);
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

                PlanAction endActor0 = candidates.Single(candidate =>
                    candidate.Action.Actor == new CombatActorId(0)
                    && candidate.Action.Kind == PlanActionKind.EndTurn).Action;
                PlanAction endActor1 = candidates.Single(candidate =>
                    candidate.Action.Actor == new CombatActorId(1)
                    && candidate.Action.Kind == PlanActionKind.EndTurn).Action;
                PlanAction nextTurnEndActor0 = endActor0 with { Turn = endActor0.Turn + 1 };
                JointOfflineSearchRequest crossTurnRequest = new(
                    [endActor0, endActor1, nextTurnEndActor0],
                    MaximumActions: 3,
                    MaximumStates: 2_000);
                JointOfflineSearchResult crossTurnBfs =
                    JointOfflineSearch.SolveBreadthFirst(root, crossTurnRequest);
                JointOfflineSearchResult crossTurnOracle =
                    JointOfflineSearch.SolveDepthFirstOracle(root, crossTurnRequest);
                if (crossTurnBfs.Snapshot.Turn != root.StartTurnNumber + 1
                    || crossTurnBfs.Actions.Count != 3
                    || crossTurnBfs.Actions[2] != nextTurnEndActor0
                    || JointObjectiveScore.Compare(crossTurnBfs.Score, crossTurnOracle.Score) != 0
                    || crossTurnBfs.Snapshot.StateKey != crossTurnOracle.Snapshot.StateKey)
                {
                    throw new InvalidOperationException(
                        "联合跨回合固定前缀没有按 F7 生命周期由 BFS/DFS 严格继续。");
                }
                JointOfflineSearchRequest automaticCrossTurnRequest = new(
                    [endActor0, endActor1],
                    MaximumActions: 3,
                    MaximumStates: 10_000);
                JointOfflineSearchResult automaticBfs =
                    JointOfflineSearch.SolveBreadthFirst(root, automaticCrossTurnRequest);
                JointOfflineSearchResult automaticOracle =
                    JointOfflineSearch.SolveDepthFirstOracle(root, automaticCrossTurnRequest);
                JointOfflineSearchResult automaticBeam = JointOfflineSearch.SolveBeam(
                    root,
                    automaticCrossTurnRequest,
                    beamWidth: 10_000);
                JointOfflineSearchResult automaticBfws = JointOfflineSearch.SolveBfws(
                    root,
                    automaticCrossTurnRequest,
                    maximumOpen: 10_000);
                if (automaticBfs.Actions.Count != 3
                    || automaticBfs.Actions[2].Turn != root.StartTurnNumber + 1
                    || JointObjectiveScore.Compare(automaticBfs.Score, automaticOracle.Score) != 0
                    || automaticBfs.Snapshot.StateKey != automaticOracle.Snapshot.StateKey
                    || ComparePlanActions(automaticBfs.Actions, automaticOracle.Actions) != 0
                    || JointObjectiveScore.Compare(automaticBeam.Score, automaticOracle.Score) != 0
                    || automaticBeam.Snapshot.StateKey != automaticOracle.Snapshot.StateKey
                    || ComparePlanActions(automaticBeam.Actions, automaticOracle.Actions) != 0
                    || JointObjectiveScore.Compare(automaticBfws.Score, automaticOracle.Score) != 0
                    || automaticBfws.Snapshot.StateKey != automaticOracle.Snapshot.StateKey
                    || ComparePlanActions(automaticBfws.Actions, automaticOracle.Actions) != 0)
                {
                    throw new InvalidOperationException(
                        "联合 BFS/Beam/BFWS 没有从屏障节点自动推进下一轮并与 DFS oracle 对齐。");
                }
                PlanAction actor0Potion = candidates.First(candidate =>
                    candidate.Action.Actor == new CombatActorId(0)
                    && candidate.Action.Kind == PlanActionKind.UsePotion).Action;
                PlanAction nextTurnPotion = actor0Potion with { Turn = actor0Potion.Turn + 1 };
                JointOfflineSearchRequest crossTurnPotionRequest = new(
                    [endActor0, endActor1, nextTurnPotion],
                    MaximumActions: 3,
                    MaximumStates: 2_000);
                JointOfflineSearchResult crossTurnPotionBfs =
                    JointOfflineSearch.SolveBreadthFirst(root, crossTurnPotionRequest);
                JointOfflineSearchResult crossTurnPotionOracle =
                    JointOfflineSearch.SolveDepthFirstOracle(root, crossTurnPotionRequest);
                if (crossTurnPotionBfs.Actions[2] != nextTurnPotion
                    || crossTurnPotionBfs.Score.PotionUses != 1
                    || JointObjectiveScore.Compare(
                        crossTurnPotionBfs.Score,
                        crossTurnPotionOracle.Score) != 0
                    || crossTurnPotionBfs.Snapshot.StateKey
                        != crossTurnPotionOracle.Snapshot.StateKey)
                {
                    throw new InvalidOperationException(
                        "联合跨回合药水前缀没有保持 Actor 槽位、消耗和严格状态。");
                }

                JointOfflineSearchRequest disabledRequest = new(
                    [],
                    MaximumActions: 2,
                    MaximumStates: 2_000,
                    PotionPolicy: new JointPotionSearchPolicy(
                        SolverPotionPolicy.Disabled,
                        maximumUses: 0));
                JointOfflineSearchResult disabledResult = AssertPolicySearch(disabledRequest, static actions =>
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
                JointPotionSearchPolicy smartPolicy = new(SolverPotionPolicy.Smart);
                int requiredSaved = PotionUsePolicy.SmartRequiredHpSaved(expectedCost);
                JointOfflineSearchResult smartBaseline = disabledResult with
                {
                    Score = disabledResult.Score with
                    {
                        Outcome = null,
                        TotalHpLost = requiredSaved,
                    },
                };
                JointOfflineSearchResult qualifyingSmart = forcedResult with
                {
                    Score = forcedResult.Score with { Outcome = null, TotalHpLost = 0 },
                };
                JointOfflineSearchResult rejectedSmart = forcedResult with
                {
                    Score = forcedResult.Score with { Outcome = null, TotalHpLost = 1 },
                };
                if (!smartPolicy.RequiresSmartCounterfactual
                    || smartPolicy.ForSmartBaseline().DefaultPolicy != SolverPotionPolicy.Disabled
                    || !smartPolicy.IsSmartCandidateEligible(
                        smartBaseline,
                        qualifyingSmart,
                        root.Actors,
                        root.BossHpRelief)
                    || smartPolicy.IsSmartCandidateEligible(
                        smartBaseline,
                        rejectedSmart,
                        root.Actors,
                        root.BossHpRelief))
                {
                    throw new InvalidOperationException(
                        "联合 Smart 药水没有按同根无药基线和单人 HP 阈值裁决。");
                }
                CombatActorRoot[] creditedActors = root.Actors.ToArray();
                creditedActors[1] = creditedActors[1] with
                {
                    PotionRewardOutlook = new PotionRewardOutlook(
                        1f,
                        true,
                        false,
                        PotionRewardForecast.Drop,
                        "BLOCK_POTION",
                        expectedCost)
                    {
                        Enabled = true,
                    },
                };
                JointOfflineSearchResult oneHpSaved = forcedResult with
                {
                    Score = forcedResult.Score with { Outcome = null, TotalHpLost = requiredSaved - 1 },
                };
                int relievedRequired = PotionUsePolicy.SmartRequiredHpSaved(
                    expectedCost,
                    BossHpRelief.ActClearHeal);
                JointOfflineSearchResult bossReliefBaseline = disabledResult with
                {
                    Score = disabledResult.Score with
                    {
                        Outcome = null,
                        TotalHpLost = requiredSaved,
                    },
                };
                JointOfflineSearchResult bossReliefCandidate = forcedResult with
                {
                    Score = forcedResult.Score with { Outcome = null, TotalHpLost = 0 },
                };
                bool replacementEligible = smartPolicy.IsSmartCandidateEligible(
                    smartBaseline,
                    oneHpSaved,
                    creditedActors,
                    BossHpRelief.None);
                bool reliefEligible = smartPolicy.IsSmartCandidateEligible(
                    bossReliefBaseline,
                    bossReliefCandidate,
                    root.Actors,
                    BossHpRelief.ActClearHeal);
                bool normalEligible = smartPolicy.IsSmartCandidateEligible(
                    bossReliefBaseline,
                    bossReliefCandidate,
                    root.Actors,
                    BossHpRelief.None);
                if (!replacementEligible
                    || relievedRequired <= requiredSaved
                    || reliefEligible
                    || !normalEligible)
                {
                    throw new InvalidOperationException(
                        "联合 Smart 奖励/Boss 政策不符：" +
                        $"replacement={replacementEligible} relief={reliefEligible} " +
                        $"normal={normalEligible} required={requiredSaved}/{relievedRequired}。");
                }

                int ambergrisCost = PotionUsePolicy.StrategicHpCost(
                    "AMBERGRIS",
                    root.Actors[1].HasRenewablePotionShapedRock);
                int ambergrisRequired = PotionUsePolicy.EffectiveStrategicHpCost(
                    ambergrisCost,
                    ambergrisCount: 1,
                    root.Actors[1].InitialMaxHp);
                PlanAction[] ambergrisActions = forcedResult.Actions
                    .Select(action => action.Kind == PlanActionKind.UsePotion
                        ? action with { PotionId = "AMBERGRIS" }
                        : action)
                    .ToArray();
                JointActorSnapshot[] ambergrisBaselineActors = disabledResult.Snapshot.Actors.ToArray();
                JointActorSnapshot[] ambergrisCandidateActors = disabledResult.Snapshot.Actors.ToArray();
                ambergrisBaselineActors[1] = ambergrisBaselineActors[1] with { Hp = 1 };
                ambergrisCandidateActors[1] = ambergrisCandidateActors[1] with
                {
                    Hp = 1 + ambergrisRequired,
                };
                JointOfflineSearchResult ambergrisBaseline = disabledResult with
                {
                    Snapshot = disabledResult.Snapshot with { Actors = ambergrisBaselineActors },
                    Score = disabledResult.Score with
                    {
                        Outcome = null,
                        TotalHpLost = ambergrisRequired,
                    },
                };
                JointOfflineSearchResult ambergrisCandidate = forcedResult with
                {
                    Actions = ambergrisActions,
                    Snapshot = forcedResult.Snapshot with { Actors = ambergrisCandidateActors },
                    Score = forcedResult.Score with { Outcome = null, TotalHpLost = 0 },
                };
                JointActorSnapshot[] shortAmbergrisActors = ambergrisCandidateActors.ToArray();
                shortAmbergrisActors[1] = shortAmbergrisActors[1] with
                {
                    Hp = ambergrisCandidateActors[1].Hp - 1,
                };
                JointOfflineSearchResult shortAmbergris = ambergrisCandidate with
                {
                    Snapshot = ambergrisCandidate.Snapshot with { Actors = shortAmbergrisActors },
                    Score = ambergrisCandidate.Score with { TotalHpLost = 1 },
                };
                if (!smartPolicy.IsSmartCandidateEligible(
                        ambergrisBaseline,
                        ambergrisCandidate,
                        root.Actors,
                        BossHpRelief.None)
                    || smartPolicy.IsSmartCandidateEligible(
                        ambergrisBaseline,
                        shortAmbergris,
                        root.Actors,
                        BossHpRelief.None))
                {
                    throw new InvalidOperationException(
                        "联合 Ambergris 未按用药 Actor 最大生命和自身 HP 改善裁决。");
                }
                JointOfflineSearchRequest smartRequest = new(
                    [],
                    MaximumActions: 2,
                    MaximumStates: 4_000,
                    PotionPolicy: smartPolicy);
                JointOfflineSearchResult smartBeam = JointOfflineSearch.SolveSmartBeam(
                    root,
                    smartRequest,
                    beamWidth: 64);
                JointOfflineSearchResult smartBfws = JointOfflineSearch.SolveSmartBfws(
                    root,
                    smartRequest,
                    maximumOpen: 128);
                if (smartBeam.ExpandedStates > smartRequest.MaximumStates
                    || smartBfws.ExpandedStates > smartRequest.MaximumStates
                    || smartBeam.Actions.Any(static action =>
                        action.Kind == PlanActionKind.UsePotion)
                    || smartBfws.Actions.Any(static action =>
                        action.Kind == PlanActionKind.UsePotion))
                {
                    throw new InvalidOperationException(
                        "联合 Smart Beam/BFWS 未共享状态预算或错误接受无收益药水。");
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

    private static void AssertAllActorsDeadTerminal(CombatState source)
    {
        OfflineJointCombat offline = CreateOfflineJointCombat(
            source,
            actorCount: 2,
            enemyModel: ModelDb.Monster<SludgeSpinner>());
        foreach (Player player in offline.Players)
            player.Creature.SetCurrentHpInternal(1);
        CombatRootSnapshot root = CombatRootSnapshot.Capture(offline.State);
        CombatPredictionSimulator simulator = root.ForkSimulator();
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        combat.ForceMonsterMove(combat.Enemies.Single(), "OIL_SPRAY_MOVE");
        JointTurnState turns = JointTurnState.Start(2, root.StartTurnNumber)
            .EndTurn(new CombatActorId(0))
            .EndTurn(new CombatActorId(1));
        ForkableSet<uint> deaths = JointActionTransition.CaptureProcessedEnemyDeaths(root, simulator);
        JointRoundTransition.CompletePlayerSide(simulator, turns, deaths);
        JointRoundTransition.CompleteBasicEnemySide(simulator, deaths);
        if (simulator.IsInProgress
            || simulator.State.Players.Any(player => simulator.State.GetCreature(player.Creature).IsAlive)
            || simulator.State.Players.Any(player => combat.GetAmount<WeakPower>(player.Creature) != 0))
        {
            throw new InvalidOperationException(
                "全员死亡没有立即终止联合战斗，或仍错误执行了终止后的 Oil Spray 后效。");
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

    private static void AssertOwnerOnlyEnemyMove(CombatRootSnapshot root)
    {
        CombatPredictionSimulator simulator = root.ForkSimulator();
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        Creature enemy = combat.Enemies.Single();
        combat.ForceMonsterMove(enemy, "INHALE");
        int strengthBefore = combat.GetAmount<StrengthPower>(enemy);
        int[] hpBefore = simulator.State.Players
            .Select(player => simulator.State.GetCreature(player.Creature).CurrentHp)
            .ToArray();
        JointTurnState turns = JointTurnState.Start(2, root.StartTurnNumber)
            .EndTurn(new CombatActorId(0))
            .EndTurn(new CombatActorId(1));
        ForkableSet<uint> deaths = JointActionTransition.CaptureProcessedEnemyDeaths(root, simulator);
        JointRoundTransition.CompletePlayerSide(simulator, turns, deaths);
        JointRoundTransition.CompleteBasicEnemySide(simulator, deaths);
        int[] hpAfter = simulator.State.Players
            .Select(player => simulator.State.GetCreature(player.Creature).CurrentHp)
            .ToArray();
        int strengthAfter = combat.GetAmount<StrengthPower>(enemy);
        if (strengthAfter - strengthBefore != 7 || !hpBefore.SequenceEqual(hpAfter))
        {
            throw new InvalidOperationException(
                $"联合 owner-only 敌方行动重复或漏结算：strength={strengthBefore}->{strengthAfter} " +
                $"hp={string.Join(',', hpBefore)}->{string.Join(',', hpAfter)}。");
        }
    }

    private static void AssertTargetOnlyEnemyMove(CombatState source)
    {
        CombatRootSnapshot root = CreateOfflineJointRoot(
            source,
            2,
            enemyModel: ModelDb.Monster<SludgeSpinner>());
        CombatPredictionSimulator simulator = root.ForkSimulator();
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        Creature enemy = combat.Enemies.Single();
        combat.ForceMonsterMove(enemy, "OIL_SPRAY_MOVE");
        int[] hpBefore = simulator.State.Players
            .Select(player => simulator.State.GetCreature(player.Creature).CurrentHp)
            .ToArray();
        int[] weakBefore = simulator.State.Players
            .Select(player => combat.GetAmount<WeakPower>(player.Creature))
            .ToArray();
        JointTurnState turns = JointTurnState.Start(2, root.StartTurnNumber)
            .EndTurn(new CombatActorId(0))
            .EndTurn(new CombatActorId(1));
        ForkableSet<uint> deaths = JointActionTransition.CaptureProcessedEnemyDeaths(root, simulator);
        JointRoundTransition.CompletePlayerSide(simulator, turns, deaths);
        JointRoundTransition.CompleteBasicEnemySide(simulator, deaths);
        int[] hpAfter = simulator.State.Players
            .Select(player => simulator.State.GetCreature(player.Creature).CurrentHp)
            .ToArray();
        int[] weakAfter = simulator.State.Players
            .Select(player => combat.GetAmount<WeakPower>(player.Creature))
            .ToArray();
        if (weakAfter.Where((amount, index) => amount - weakBefore[index] != 1).Any()
            || hpAfter.Where((hp, index) => hp >= hpBefore[index]).Any()
            || hpBefore[0] - hpAfter[0] != hpBefore[1] - hpAfter[1])
        {
            throw new InvalidOperationException(
                $"联合 target-only 敌方行动未逐 Actor 精确结算：" +
                $"weak={string.Join(',', weakBefore)}->{string.Join(',', weakAfter)} " +
                $"hp={string.Join(',', hpBefore)}->{string.Join(',', hpAfter)}。");
        }
    }

    private static void AssertMixedEnemyMove(CombatState source)
    {
        CombatRootSnapshot root = CreateOfflineJointRoot(
            source,
            2,
            enemyModel: ModelDb.Monster<SludgeSpinner>());
        CombatPredictionSimulator simulator = root.ForkSimulator();
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        Creature enemy = combat.Enemies.Single();
        combat.ForceMonsterMove(enemy, "RAGE_MOVE");
        int strengthBefore = combat.GetAmount<StrengthPower>(enemy);
        int[] hpBefore = simulator.State.Players
            .Select(player => simulator.State.GetCreature(player.Creature).CurrentHp)
            .ToArray();
        JointTurnState turns = JointTurnState.Start(2, root.StartTurnNumber)
            .EndTurn(new CombatActorId(0))
            .EndTurn(new CombatActorId(1));
        ForkableSet<uint> deaths = JointActionTransition.CaptureProcessedEnemyDeaths(root, simulator);
        JointRoundTransition.CompletePlayerSide(simulator, turns, deaths);
        JointRoundTransition.CompleteBasicEnemySide(simulator, deaths);
        int[] hpAfter = simulator.State.Players
            .Select(player => simulator.State.GetCreature(player.Creature).CurrentHp)
            .ToArray();
        int strengthAfter = combat.GetAmount<StrengthPower>(enemy);
        if (strengthAfter - strengthBefore != 3
            || hpAfter.Where((hp, index) => hp >= hpBefore[index]).Any()
            || hpBefore[0] - hpAfter[0] != hpBefore[1] - hpAfter[1])
        {
            throw new InvalidOperationException(
                $"联合 mixed 敌方行动未拆分逐 Actor 攻击与一次性后效：" +
                $"strength={strengthBefore}->{strengthAfter} " +
                $"hp={string.Join(',', hpBefore)}->{string.Join(',', hpAfter)}。");
        }
    }

    private static void AssertOwnerRemovalEnemyMove(CombatState source)
    {
        CombatRootSnapshot root = CreateOfflineJointRoot(
            source,
            2,
            enemyModel: ModelDb.Monster<GasBomb>());
        CombatPredictionSimulator simulator = root.ForkSimulator();
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        Creature enemy = combat.Enemies.Single();
        combat.ForceMonsterMove(enemy, "EXPLODE_MOVE");
        int[] hpBefore = simulator.State.Players
            .Select(player => simulator.State.GetCreature(player.Creature).CurrentHp)
            .ToArray();
        JointTurnState turns = JointTurnState.Start(2, root.StartTurnNumber)
            .EndTurn(new CombatActorId(0))
            .EndTurn(new CombatActorId(1));
        ForkableSet<uint> deaths = JointActionTransition.CaptureProcessedEnemyDeaths(root, simulator);
        JointRoundTransition.CompletePlayerSide(simulator, turns, deaths);
        JointRoundTransition.CompleteBasicEnemySide(simulator, deaths);
        int[] hpAfter = simulator.State.Players
            .Select(player => simulator.State.GetCreature(player.Creature).CurrentHp)
            .ToArray();
        if (simulator.State.GetCreature(enemy).IsAlive
            || hpAfter.Where((hp, index) => hp >= hpBefore[index]).Any()
            || hpBefore[0] - hpAfter[0] != hpBefore[1] - hpAfter[1])
        {
            throw new InvalidOperationException(
                $"联合 owner-removal 行动未逐 Actor 攻击并只移除 owner：" +
                $"enemyAlive={simulator.State.GetCreature(enemy).IsAlive} " +
                $"hp={string.Join(',', hpBefore)}->{string.Join(',', hpAfter)}。");
        }
    }

    private static void AssertPreAttackSummonEnemyMove(CombatState source)
    {
        CombatRootSnapshot root = CreateOfflineJointRoot(
            source,
            2,
            enemyModel: ModelDb.Monster<LivingFog>(),
            encounterModel: ModelDb.Encounter<LivingFogNormal>());
        CombatPredictionSimulator simulator = root.ForkSimulator();
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        Creature enemy = combat.Enemies.Single();
        combat.ForceMonsterMove(enemy, "BLOAT_MOVE");
        int expectedSpawnCount = combat.GetMonsterStaticInt(enemy, "BloatAmount");
        int bombsBefore = combat.Enemies.Count(creature => creature.Monster is GasBomb);
        int[] hpBefore = simulator.State.Players
            .Select(player => simulator.State.GetCreature(player.Creature).CurrentHp)
            .ToArray();
        JointTurnState turns = JointTurnState.Start(2, root.StartTurnNumber)
            .EndTurn(new CombatActorId(0))
            .EndTurn(new CombatActorId(1));
        ForkableSet<uint> deaths = JointActionTransition.CaptureProcessedEnemyDeaths(root, simulator);
        JointRoundTransition.CompletePlayerSide(simulator, turns, deaths);
        JointRoundTransition.CompleteBasicEnemySide(simulator, deaths);
        int bombsAfter = combat.Enemies.Count(creature => creature.Monster is GasBomb);
        int[] hpAfter = simulator.State.Players
            .Select(player => simulator.State.GetCreature(player.Creature).CurrentHp)
            .ToArray();
        if (bombsAfter - bombsBefore != expectedSpawnCount
            || hpAfter.Where((hp, index) => hp >= hpBefore[index]).Any()
            || hpBefore[0] - hpAfter[0] != hpBefore[1] - hpAfter[1])
        {
            throw new InvalidOperationException(
                $"联合 pre-attack summon 行动重复或漏结算：" +
                $"bombs={bombsBefore}->{bombsAfter} expected={expectedSpawnCount} " +
                $"hp={string.Join(',', hpBefore)}->{string.Join(',', hpAfter)}。");
        }
    }

    private static void AssertSplitMixedEnemyMove(CombatState source)
    {
        CombatRootSnapshot root = CreateOfflineJointRoot(
            source,
            2,
            enemyModel: ModelDb.Monster<LagavulinMatriarch>());
        CombatPredictionSimulator simulator = root.ForkSimulator();
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        Creature enemy = combat.Enemies.Single();
        combat.ForceMonsterMove(enemy, "SOUL_SIPHON_MOVE");
        JointTurnState turns = JointTurnState.Start(2, root.StartTurnNumber)
            .EndTurn(new CombatActorId(0))
            .EndTurn(new CombatActorId(1));
        ForkableSet<uint> deaths = JointActionTransition.CaptureProcessedEnemyDeaths(root, simulator);
        JointRoundTransition.CompletePlayerSide(simulator, turns, deaths);
        JointRoundTransition.CompleteBasicEnemySide(simulator, deaths);
        int[] strength = simulator.State.Players
            .Select(player => combat.GetAmount<StrengthPower>(player.Creature))
            .ToArray();
        int[] dexterity = simulator.State.Players
            .Select(player => combat.GetAmount<DexterityPower>(player.Creature))
            .ToArray();
        int enemyStrength = combat.GetAmount<StrengthPower>(enemy);
        if (!strength.SequenceEqual([-2, -2])
            || !dexterity.SequenceEqual([-2, -2])
            || enemyStrength != 2)
        {
            throw new InvalidOperationException(
                $"联合混合敌方效果未按目标部分逐 Actor、owner 部分一次结算：" +
                $"strength={string.Join(',', strength)} dexterity={string.Join(',', dexterity)} " +
                $"enemyStrength={enemyStrength}。");
        }
    }

    private static void AssertEnemyRevive(CombatState source)
    {
        CombatRootSnapshot root = CreateOfflineJointRoot(
            source,
            2,
            enemyModel: ModelDb.Monster<Parafright>());
        CombatPredictionSimulator simulator = root.ForkSimulator();
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        Creature enemy = combat.Enemies.Single();
        SimCreatureState enemyState = simulator.State.GetCreature(enemy);
        combat.BeginIllusionRevive(enemy);
        enemyState.CurrentHp = 0;
        ForkableSet<uint> deaths = JointActionTransition.CaptureProcessedEnemyDeaths(root, simulator);
        if (enemy.CombatId is not uint combatId)
            throw new InvalidOperationException("联合复活夹具的敌人缺少 CombatId。");
        deaths.Add(combatId);
        JointTurnState turns = JointTurnState.Start(2, root.StartTurnNumber)
            .EndTurn(new CombatActorId(0))
            .EndTurn(new CombatActorId(1));
        JointRoundTransition.CompletePlayerSide(simulator, turns, deaths);
        JointRoundTransition.CompleteBasicEnemySide(simulator, deaths);
        if (!enemyState.IsAlive || enemyState.CurrentHp != enemyState.MaxHp || deaths.Contains(combatId))
        {
            throw new InvalidOperationException(
                $"联合敌方复活未恢复 HP 或死亡处理资格：" +
                $"hp={enemyState.CurrentHp}/{enemyState.MaxHp} processed={deaths.Contains(combatId)}。");
        }
    }

    private static void AssertEnemyEscape(CombatState source)
    {
        CombatRootSnapshot root = CreateOfflineJointRoot(
            source,
            2,
            enemyModel: ModelDb.Monster<FatGremlin>());
        CombatPredictionSimulator simulator = root.ForkSimulator();
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        Creature enemy = combat.Enemies.Single();
        combat.ForceMonsterMove(enemy, "FLEE_MOVE");
        JointTurnState turns = JointTurnState.Start(2, root.StartTurnNumber)
            .EndTurn(new CombatActorId(0))
            .EndTurn(new CombatActorId(1));
        ForkableSet<uint> deaths = JointActionTransition.CaptureProcessedEnemyDeaths(root, simulator);
        JointRoundTransition.CompletePlayerSide(simulator, turns, deaths);
        JointRoundTransition.CompleteBasicEnemySide(simulator, deaths);
        if (!combat.EscapedCreatures.Contains(enemy) || combat.Enemies.Contains(enemy))
            throw new InvalidOperationException("联合敌方逃跑没有从活动 roster 移入逃跑集合。");
    }

    private static void AssertRemoteActorExtraTurn(CombatState source)
    {
        CombatRootSnapshot root = CreateOfflineJointRoot(source, 2);
        CombatPredictionSimulator simulator = root.ForkSimulator();
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        Player remote = simulator.State.Players[1];
        combat.Apply<AmbergrisPower>(remote.Creature, 1, remote.Creature);
        int roundBefore = combat.RoundNumber;
        int[] turnsBefore = simulator.State.Players
            .Select(combat.GetPlayerTurnNumber)
            .ToArray();
        int[] hpBefore = simulator.State.Players
            .Select(player => simulator.State.GetCreature(player.Creature).CurrentHp)
            .ToArray();
        JointTurnState turns = JointTurnState.Start(2, root.StartTurnNumber)
            .EndTurn(new CombatActorId(0))
            .EndTurn(new CombatActorId(1));
        ForkableSet<uint> deaths = JointActionTransition.CaptureProcessedEnemyDeaths(root, simulator);
        IReadOnlyList<CombatActorId> extras = JointRoundTransition.CompletePlayerSide(
            simulator,
            turns,
            deaths);
        if (!extras.SequenceEqual([new CombatActorId(1)]))
            throw new InvalidOperationException($"联合额外回合 Actor 子集错误：{string.Join(',', extras)}。");
        JointTurnState next = JointRoundTransition.StartBasicPlayerSide(
            simulator,
            turns,
            deaths,
            extraTurnActors: extras);
        int[] hpAfter = simulator.State.Players
            .Select(player => simulator.State.GetCreature(player.Creature).CurrentHp)
            .ToArray();
        if (combat.RoundNumber != roundBefore
            || combat.GetPlayerTurnNumber(simulator.State.Players[0]) != turnsBefore[0]
            || combat.GetPlayerTurnNumber(remote) != turnsBefore[1] + 1
            || next.Phases[0] != JointActorTurnPhase.Ended
            || next.Phases[1] != JointActorTurnPhase.Playing
            || combat.GetAmount<AmbergrisPower>(remote.Creature) != 0
            || !hpBefore.SequenceEqual(hpAfter))
        {
            throw new InvalidOperationException(
                $"联合远端 Actor 额外回合生命周期错误：round={roundBefore}->{combat.RoundNumber} " +
                $"turns={string.Join(',', turnsBefore)}->{combat.GetPlayerTurnNumber(simulator.State.Players[0])}," +
                $"{combat.GetPlayerTurnNumber(remote)} phases={string.Join(',', next.Phases)} " +
                $"ambergris={combat.GetAmount<AmbergrisPower>(remote.Creature)}。");
        }
    }

    private static void AssertOwnerOnlyEnemyRng(CombatState source)
    {
        CombatRootSnapshot root = CreateOfflineJointRoot(
            source,
            2,
            enemyModel: ModelDb.Monster<ToughEgg>());
        CombatPredictionSimulator simulator = root.ForkSimulator();
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        Creature enemy = combat.Enemies.Single();
        combat.ForceMonsterMove(enemy, "HATCH_MOVE");
        int rngBefore = simulator.Rng.Niche.Counter();
        JointTurnState turns = JointTurnState.Start(2, root.StartTurnNumber)
            .EndTurn(new CombatActorId(0))
            .EndTurn(new CombatActorId(1));
        ForkableSet<uint> deaths = JointActionTransition.CaptureProcessedEnemyDeaths(root, simulator);
        JointRoundTransition.CompletePlayerSide(simulator, turns, deaths);
        JointRoundTransition.CompleteBasicEnemySide(simulator, deaths);
        int rngAfter = simulator.Rng.Niche.Counter();
        SimCreatureState state = simulator.State.GetCreature(enemy);
        if (rngAfter - rngBefore != 1 || !state.IsAlive || state.CurrentHp != state.MaxHp)
        {
            throw new InvalidOperationException(
                $"联合 owner-only Hatch RNG 或生命结算错误：" +
                $"niche={rngBefore}->{rngAfter} hp={state.CurrentHp}/{state.MaxHp}。");
        }
    }

    private static void AssertCompleteStrictReplay(CombatState source, int actorCount)
    {
        CombatRootSnapshot root = CreateOfflineJointRoot(
            source,
            actorCount,
            enemyCurrentHp: 1);
        PlanAction[] endTurnPrefix = Enumerable.Range(0, actorCount)
            .Select(index => new PlanAction(
                PlanActionKind.EndTurn,
                root.StartTurnNumber,
                Actor: new CombatActorId(index)))
            .ToArray();
        JointOfflineSearchRequest request = new(
            endTurnPrefix,
            MaximumActions: actorCount + 1,
            MaximumStates: 10_000);
        JointOfflineSearchResult searched = JointOfflineSearch.SolveBeam(
            root,
            request,
            beamWidth: 10_000);
        if (!searched.Snapshot.Simulator.TerminalStamp.HasValue
            || searched.Actions.Count != actorCount + 1
            || searched.Actions[^1].Turn != root.StartTurnNumber + 1)
        {
            throw new InvalidOperationException(
                $"{actorCount} Actor strict replay 夹具没有搜索到跨轮完整战斗。 ");
        }
        JointReplayResult replay = JointStrictReplayVerifier.Verify(root, searched);
        if (replay.ActionSnapshots.Count != actorCount + 1
            || replay.Checkpoints.Count != actorCount + 2
            || replay.Checkpoints.Count(checkpoint => checkpoint.Stage == "barrier") != 1
            || !replay.Snapshot.Simulator.TerminalStamp.HasValue)
        {
            throw new InvalidOperationException(
                $"{actorCount} Actor strict replay 未保留逐动作/屏障快照或终局。 ");
        }

        JointActorSnapshot changedActor = replay.Snapshot.Actors[0] with
        {
            Block = replay.Snapshot.Actors[0].Block + 1,
        };
        JointCombatSnapshot changed = replay.Snapshot with
        {
            Actors = Array.AsReadOnly(
                replay.Snapshot.Actors
                    .Select((actor, index) => index == 0 ? changedActor : actor)
                    .ToArray()),
        };
        string? difference = JointStrictReplayVerifier.DescribeFirstDifference(
            replay.Snapshot,
            changed);
        if (difference == null || !difference.StartsWith("actor[0]", StringComparison.Ordinal))
            throw new InvalidOperationException("联合 strict diff 未定位首个 Actor 字段差异。");
    }

    private static void AssertFourActorBoundedWorkload(CombatState source)
    {
        CombatRootSnapshot root = CreateOfflineJointRoot(source, 4);
        JointOfflineSearchRequest request = JointOfflineSearchRequest.Default(
            maximumActions: 3,
            maximumStates: 256);
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
        JointOfflineSearchResult serial = JointOfflineSearch.SolveBeam(
            root,
            request,
            beamWidth: 32,
            degreeOfParallelism: 1);
        stopwatch.Stop();
        long coordinatorAllocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        JointOfflineSearchResult repeated = JointOfflineSearch.SolveBeam(
            root,
            request,
            beamWidth: 32,
            degreeOfParallelism: 1);
        JointOfflineSearchResult parallel = JointOfflineSearch.SolveBeam(
            root,
            request,
            beamWidth: 32,
            degreeOfParallelism: 4);
        JointOfflineSearchResult bfws = JointOfflineSearch.SolveBfws(
            root,
            request,
            maximumOpen: 64);
        if (serial.ExpandedStates > request.MaximumStates
            || repeated.ExpandedStates > request.MaximumStates
            || parallel.ExpandedStates > request.MaximumStates
            || bfws.ExpandedStates > request.MaximumStates)
        {
            throw new InvalidOperationException("四 Actor 有界搜索超过共享状态预算。");
        }
        foreach (JointOfflineSearchResult candidate in new[] { repeated, parallel })
        {
            if (JointObjectiveScore.Compare(candidate.Score, serial.Score) != 0
                || !candidate.Score.HpLostByActor.SequenceEqual(serial.Score.HpLostByActor)
                || candidate.Snapshot.StateKey != serial.Snapshot.StateKey
                || ComparePlanActions(candidate.Actions, serial.Actions) != 0
                || candidate.ExpandedStates != serial.ExpandedStates
                || candidate.Termination != serial.Termination)
            {
                throw new InvalidOperationException(
                    "四 Actor 固定预算 Beam 的重复或并行结果不确定。 ");
            }
        }
        _coopWorkloadEvidence =
            $"JointWorkload:Actors=4:Actions=3:Budget={request.MaximumStates}:" +
            $"BeamExpanded={serial.ExpandedStates}:BeamStop={serial.Termination}:" +
            $"BfwsExpanded={bfws.ExpandedStates}:BfwsStop={bfws.Termination}:" +
            $"BeamElapsedMs={stopwatch.ElapsedMilliseconds}:" +
            $"CoordinatorAllocatedBytes={coordinatorAllocated}";
    }

    private static void AssertJointSearchLifetime(CombatState source)
    {
        CombatRootSnapshot root = CreateOfflineJointRoot(source, 4);
        foreach (string mode in new[] { "complete", "cancel", "fault" })
        {
            WeakReference[] references = RunLifetimeProbe(root, mode);
            if (references.Length == 0)
                throw new InvalidOperationException($"联合搜索 {mode} 生命周期探针没有观察到子节点。");
            for (int attempt = 0; attempt < 3 && references.Any(reference => reference.IsAlive); attempt++)
            {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
            }
            int retained = references.Count(reference => reference.IsAlive);
            if (retained != 0)
            {
                throw new InvalidOperationException(
                    $"联合搜索 {mode} 结束后仍保留 {retained}/{references.Length} 个子模拟器。");
            }
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference[] RunLifetimeProbe(CombatRootSnapshot root, string mode)
    {
        List<WeakReference> references = [];
        using CancellationTokenSource cancellation = new();
        int observed = 0;
        JointSearchLifetimeDiagnostics.TestNodeCreated = simulator =>
        {
            references.Add(new WeakReference(simulator));
            observed++;
            if (mode == "cancel" && observed == 4)
                cancellation.Cancel();
            if (mode == "fault" && observed == 4)
                throw new InvalidOperationException("联合搜索生命周期探针注入异常。");
        };
        try
        {
            try
            {
                _ = JointOfflineSearch.SolveBeam(
                    root,
                    JointOfflineSearchRequest.Default(maximumActions: 2, maximumStates: 128),
                    beamWidth: 16,
                    cancellation.Token,
                    degreeOfParallelism: 1);
                if (mode != "complete")
                    throw new InvalidOperationException($"联合搜索 {mode} 探针没有中止。");
            }
            catch (OperationCanceledException) when (mode == "cancel")
            {
            }
            catch (InvalidOperationException exception) when (
                mode == "fault"
                && exception.Message == "联合搜索生命周期探针注入异常。")
            {
            }
        }
        finally
        {
            JointSearchLifetimeDiagnostics.TestNodeCreated = null;
        }
        return references.ToArray();
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

    private static void AssertTurnStartChoiceContinuation(CombatRootSnapshot root)
    {
        CombatPredictionSimulator parent = root.ForkSimulator();
        SimulatedCombatState parentCombat = (SimulatedCombatState)parent.State.CombatState;
        Player owner = parent.State.Players[1];
        parentCombat.Apply<ToolsOfTheTradePower>(owner.Creature, 1, owner.Creature);
        JointTurnState turns = JointTurnState.Start(2, root.StartTurnNumber)
            .EndTurn(new CombatActorId(0))
            .EndTurn(new CombatActorId(1));
        ForkableSet<uint> parentDeaths = JointActionTransition.CaptureProcessedEnemyDeaths(root, parent);
        JointRoundTransition.CompletePlayerSide(parent, turns, parentDeaths);
        JointRoundTransition.CompleteBasicEnemySide(parent, parentDeaths);

        JointPendingChoiceFrame frame;
        CombatPredictionSimulator probe = parent.Fork();
        try
        {
            _ = JointRoundTransition.StartBasicPlayerSide(
                probe,
                turns,
                parentDeaths.Fork());
            throw new InvalidOperationException("Tools of the Trade 未产生联合回合开始选择。");
        }
        catch (JointPendingActionChoiceException pending)
        {
            frame = pending.Frame;
        }
        if (frame.OwnerActor != new CombatActorId(1)
            || frame.SourceAction != new PlanAction(
                PlanActionKind.EndTurn,
                turns.Turn,
                Actor: new CombatActorId(1))
            || frame.Placement != JointPendingChoicePlacement.TurnStart)
        {
            throw new InvalidOperationException("联合回合开始选择未保留 owner、SourceAction 或 placement。");
        }
        PlanCardChoice choice = CardChoiceSupport.BuildChoices(
                frame.Spec,
                static _ => string.Empty,
                maxPileBranches: 32,
                maxHandBranches: 32)
            .First() with
        {
            Actor = frame.OwnerActor,
            SourceId = frame.SourceId,
            ContextId = frame.ContextId,
            Timing = frame.Timing,
        };
        CombatPredictionSimulator resumed = parent.Fork();
        JointTurnState next = JointRoundTransition.StartBasicPlayerSide(
            resumed,
            turns,
            parentDeaths.Fork(),
            [choice]);
        if (next.Turn != turns.Turn + 1
            || ((SimulatedCombatState)resumed.State.CombatState).HasPendingChoice)
        {
            throw new InvalidOperationException("联合回合开始选择前缀未完成下一轮恢复。");
        }
    }

    private static void AssertEndTurnPowerChoiceContinuation(CombatRootSnapshot root)
    {
        CombatPredictionSimulator parent = root.ForkSimulator();
        SimulatedCombatState parentCombat = (SimulatedCombatState)parent.State.CombatState;
        Player owner = parent.State.Players[1];
        SimPlayerCombatState ownerState = parent.State.GetPlayerCombatState(owner);
        parent.RemoveFromCombat(ownerState.AllCards.ToArray());
        _ = parentCombat.AddPowerInstance<HellraiserPower>(
            owner.Creature,
            1,
            owner.Creature);
        _ = parentCombat.AddPowerInstance<DarkEmbracePower>(
            owner.Creature,
            1,
            owner.Creature);
        PredictedCard ethereal = PredictedCard.Create(ModelDb.Card<DefendDefect>(), owner);
        ethereal.MutablePreview.AddKeyword(CardKeyword.Ethereal);
        parent.AddGeneratedCardToCombat(
            ethereal,
            PileType.Hand,
            owner,
            resultKind: CardGenerationResultKind.Fixed);
        parent.AddGeneratedCardToCombat(
            PredictedCard.Create(ModelDb.Card<SeekerStrike>(), owner),
            PileType.Draw,
            owner,
            resultKind: CardGenerationResultKind.Fixed);
        parent.AddGeneratedCardToCombat(
            PredictedCard.Create(ModelDb.Card<DefendDefect>(), owner),
            PileType.Draw,
            owner,
            resultKind: CardGenerationResultKind.Fixed);

        JointTurnState turns = JointTurnState.Start(2, root.StartTurnNumber)
            .EndTurn(new CombatActorId(0))
            .EndTurn(new CombatActorId(1));
        ForkableSet<uint> parentDeaths = JointActionTransition.CaptureProcessedEnemyDeaths(root, parent);
        JointPendingChoiceFrame frame;
        CombatPredictionSimulator probe = parent.Fork();
        try
        {
            JointRoundTransition.CompletePlayerSide(
                probe,
                turns,
                parentDeaths.Fork());
            throw new InvalidOperationException("Actor1 回合结束 Power 未产生联合选择。");
        }
        catch (JointPendingActionChoiceException pending)
        {
            frame = pending.Frame;
        }
        if (frame.OwnerActor != new CombatActorId(1)
            || frame.SourceAction.Actor != new CombatActorId(1)
            || frame.Placement != JointPendingChoicePlacement.TurnStart
            || frame.Timing != PlanChoiceTiming.PlayerTurnEnd)
        {
            throw new InvalidOperationException(
                "联合回合结束 Power 选择未保留 Actor1 owner、placement 或 timing。");
        }
        PlanCardChoice choice = CardChoiceSupport.BuildChoices(
                frame.Spec,
                static _ => string.Empty,
                maxPileBranches: 32,
                maxHandBranches: 32)
            .First() with
        {
            Actor = frame.OwnerActor,
            SourceId = frame.SourceId,
            ContextId = frame.ContextId,
            Timing = frame.Timing,
        };
        CombatPredictionSimulator resumed = parent.Fork();
        JointRoundTransition.CompletePlayerSide(
            resumed,
            turns,
            parentDeaths.Fork(),
            [choice]);
        SimulatedCombatState resumedCombat = (SimulatedCombatState)resumed.State.CombatState;
        if (resumedCombat.HasPendingChoice
            || resumed.State.GetPlayerCombatState(owner).Phase != PlayerTurnPhase.None)
        {
            throw new InvalidOperationException(
                "联合 Actor1 回合结束 Power 选择前缀未恢复到稳定屏障。");
        }
    }

    private static void AssertEndTurnRelicChoiceContinuation(CombatRootSnapshot root)
    {
        CombatPredictionSimulator parent = root.ForkSimulator();
        SimulatedCombatState parentCombat = (SimulatedCombatState)parent.State.CombatState;
        Player owner = parent.State.Players[1];
        SimPlayerCombatState ownerState = parent.State.GetPlayerCombatState(owner);
        parent.RemoveFromCombat(ownerState.AllCards.ToArray());
        _ = parentCombat.AddPowerInstance<HellraiserPower>(
            owner.Creature,
            1,
            owner.Creature);
        JossPaper jossPaper = (JossPaper)PredictionUtils.CreateRelic(
            CanonicalModels.Relic<JossPaper>(),
            owner);
        FieldInfo rootRelicsField = typeof(SimulatedCombatState).GetField(
            "_rootRelics",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("联合遗物选择测试找不到模拟遗物账本。");
        if (rootRelicsField.GetValue(parentCombat)
                is not IDictionary<Player, RelicModel[]> rootRelics)
        {
            throw new InvalidOperationException("联合遗物选择测试无法写入模拟遗物账本。");
        }
        RelicModel[] originalRelics = rootRelics[owner];
        rootRelics[owner] = [jossPaper];
        int exhaustThreshold = jossPaper.DynamicVars[JossPaper._exhaustAmountKey].IntValue;
        if (exhaustThreshold <= 0)
            throw new InvalidOperationException("联合遗物选择测试的 Joss Paper 阈值无效。");
        if (!parentCombat.RelicsOf(owner).Any(relic => ReferenceEquals(relic, jossPaper)))
            throw new InvalidOperationException("联合遗物选择测试注入的 Joss Paper 不在 Actor1 账本中。");
        for (int index = 0; index < exhaustThreshold; index++)
        {
            PredictedCard ethereal = PredictedCard.Create(ModelDb.Card<DefendDefect>(), owner);
            ethereal.MutablePreview.AddKeyword(CardKeyword.Ethereal);
            parent.AddGeneratedCardToCombat(
                ethereal,
                PileType.Hand,
                owner,
                resultKind: CardGenerationResultKind.Fixed);
        }
        parent.AddGeneratedCardToCombat(
            PredictedCard.Create(ModelDb.Card<DefendDefect>(), owner),
            PileType.Draw,
            owner,
            CardPilePosition.Bottom,
            resultKind: CardGenerationResultKind.Fixed);
        parent.AddGeneratedCardToCombat(
            PredictedCard.Create(ModelDb.Card<SeekerStrike>(), owner),
            PileType.Draw,
            owner,
            CardPilePosition.Top,
            resultKind: CardGenerationResultKind.Fixed);

        JointTurnState turns = JointTurnState.Start(2, root.StartTurnNumber)
            .EndTurn(new CombatActorId(0))
            .EndTurn(new CombatActorId(1));
        ForkableSet<uint> parentDeaths = JointActionTransition.CaptureProcessedEnemyDeaths(root, parent);
        JointPendingChoiceFrame frame;
        CombatPredictionSimulator probe = parent.Fork();
        try
        {
            JointRoundTransition.CompletePlayerSide(probe, turns, parentDeaths.Fork());
            SimPlayerCombatState probeOwner = probe.State.GetPlayerCombatState(owner);
            throw new InvalidOperationException(
                $"Actor1 回合结束遗物未产生联合选择：threshold={exhaustThreshold} " +
                $"relics={string.Join(',', ((SimulatedCombatState)probe.State.CombatState).RelicsOf(owner).Select(static relic => relic.Id.Entry))} " +
                $"hand={string.Join(',', probeOwner.Hand.Cards.Select(static card => card.Preview.Id.Entry))} " +
                $"draw={string.Join(',', probeOwner.DrawPile.Cards.Select(static card => card.Preview.Id.Entry))} " +
                $"discard={string.Join(',', probeOwner.DiscardPile.Cards.Select(static card => card.Preview.Id.Entry))} " +
                $"exhaust={string.Join(',', probeOwner.ExhaustPile.Cards.Select(static card => card.Preview.Id.Entry))}。");
        }
        catch (JointPendingActionChoiceException pending)
        {
            frame = pending.Frame;
        }
        if (frame.OwnerActor != new CombatActorId(1)
            || frame.SourceId != ModelDb.Power<HellraiserPower>().Id.Entry
            || frame.Placement != JointPendingChoicePlacement.TurnStart
            || frame.Timing != PlanChoiceTiming.PlayerTurnEnd)
        {
            throw new InvalidOperationException(
                "联合回合结束遗物选择未保留 Actor1 owner、source 或 timing。");
        }
        PlanCardChoice choice = CardChoiceSupport.BuildChoices(
                frame.Spec,
                static _ => string.Empty,
                maxPileBranches: 32,
                maxHandBranches: 32)
            .First() with
        {
            Actor = frame.OwnerActor,
            SourceId = frame.SourceId,
            ContextId = frame.ContextId,
            Timing = frame.Timing,
        };
        CombatPredictionSimulator resumed = parent.Fork();
        JointRoundTransition.CompletePlayerSide(
            resumed,
            turns,
            parentDeaths.Fork(),
            [choice]);
        if (((SimulatedCombatState)resumed.State.CombatState).HasPendingChoice
            || resumed.State.GetPlayerCombatState(owner).Phase != PlayerTurnPhase.None)
        {
            throw new InvalidOperationException(
                "联合 Actor1 回合结束遗物选择前缀未恢复到稳定屏障。");
        }
        rootRelics[owner] = originalRelics;
    }

    private static void AssertRepeatedAutoPlayChoiceContinuation(CombatRootSnapshot root)
    {
        CombatPredictionSimulator parent = root.ForkSimulator();
        Player owner = parent.State.Players[1];
        SimPlayerCombatState ownerState = parent.State.GetPlayerCombatState(owner);
        parent.RemoveFromCombat(ownerState.AllCards.ToArray());
        PredictedCard decisions = PredictedCard.Create(ModelDb.Card<DecisionsDecisions>(), owner);
        PredictedCard prepared = PredictedCard.Create(ModelDb.Card<Prepared>(), owner);
        parent.AddGeneratedCardToCombat(
            decisions,
            PileType.Hand,
            owner,
            resultKind: CardGenerationResultKind.Fixed);
        parent.AddGeneratedCardToCombat(
            prepared,
            PileType.Hand,
            owner,
            resultKind: CardGenerationResultKind.Fixed);
        for (int index = 0; index < 3; index++)
        {
            parent.AddGeneratedCardToCombat(
                PredictedCard.Create(ModelDb.Card<DefendDefect>(), owner),
                PileType.Hand,
                owner,
                resultKind: CardGenerationResultKind.Fixed);
            parent.AddGeneratedCardToCombat(
                PredictedCard.Create(ModelDb.Card<StrikeDefect>(), owner),
                PileType.Draw,
                owner,
                resultKind: CardGenerationResultKind.Fixed);
        }
        ownerState.GainEnergy(20);
        ownerState.GainStars(20);

        JointTurnState turns = JointTurnState.Start(2, root.StartTurnNumber);
        JointActionCandidate candidate = JointActionExpander.Expand(parent, turns)
            .FirstOrDefault(item => item.Action.Actor == new CombatActorId(1)
                && item.Action.CardId == decisions.Preview.Id.Entry
                && item.Action.Choice?.Cards.Any(token =>
                    token.CardId == prepared.Preview.Id.Entry) == true
                && item.Action.NestedChoices is { Count: > 1 })
            ?? throw new InvalidOperationException(
                "联合重复自动出牌未展开 Actor1 Decisions/Prepared 的多层选择。");
        int repeat = decisions.Preview.DynamicVars.Repeat.IntValue;
        if (candidate.Action.NestedChoices!.Count < repeat
            || candidate.Action.NestedChoices.Any(choice =>
                choice.Actor != new CombatActorId(1)
                || choice.SourceId != decisions.Preview.Id.Entry))
        {
            throw new InvalidOperationException(
                "联合重复自动出牌的选择次数、owner 或 source 不正确。");
        }

        CombatPredictionSimulator first = parent.Fork();
        JointTurnState firstTurns = JointActionTransition.Apply(
            first,
            turns,
            candidate.Action,
            JointActionTransition.CaptureProcessedEnemyDeaths(root, first));
        CombatPredictionSimulator second = parent.Fork();
        JointTurnState secondTurns = JointActionTransition.Apply(
            second,
            turns,
            candidate.Action,
            JointActionTransition.CaptureProcessedEnemyDeaths(root, second));
        JointCombatSnapshot firstSnapshot = JointCombatSnapshot.Capture(root, first, firstTurns);
        JointCombatSnapshot secondSnapshot = JointCombatSnapshot.Capture(root, second, secondTurns);
        if (firstSnapshot.StateKey != secondSnapshot.StateKey
            || ((SimulatedCombatState)first.State.CombatState).HasPendingChoice)
        {
            throw new InvalidOperationException(
                "联合重复自动出牌未确定性消费完整选择链。");
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
        PotionModel? localPotion = null,
        MonsterModel? enemyModel = null,
        EncounterModel? encounterModel = null,
        int? enemyCurrentHp = null)
        => CombatRootSnapshot.Capture(CreateOfflineJointCombat(
            source,
            actorCount,
            remotePotion,
            includeRemoteRelicTriggerFixture,
            includeRemoteTeamPowerFixture,
            includeRelicConsumptionFixture,
            includeCharacterMechanismFixture,
            characterRoster,
            localPotion,
            enemyModel,
            encounterModel,
            enemyCurrentHp).State);

    private static OfflineJointCombat CreateOfflineJointCombat(
        CombatState source,
        int actorCount,
        PotionModel? remotePotion = null,
        bool includeRemoteRelicTriggerFixture = false,
        bool includeRemoteTeamPowerFixture = false,
        bool includeRelicConsumptionFixture = false,
        bool includeCharacterMechanismFixture = false,
        IReadOnlyList<CharacterModel>? characterRoster = null,
        PotionModel? localPotion = null,
        MonsterModel? enemyModel = null,
        EncounterModel? encounterModel = null,
        int? enemyCurrentHp = null)
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
        EncounterModel? encounterSource = encounterModel ?? source.Encounter;
        EncounterModel? encounter = encounterSource == null
            ? null
            : ModelDb.GetById<EncounterModel>(encounterSource.Id).ToMutable();
        CombatState state = new(
            encounter: encounter,
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

        MonsterModel sourceMonster = enemyModel ?? source.Enemies.FirstOrDefault()?.Monster
            ?? throw new InvalidOperationException("当前测试战斗没有可复用的怪物模型。");
        MonsterModel monster = ModelDb.GetById<MonsterModel>(sourceMonster.Id).ToMutable();
        string? enemySlot = encounterModel == null ? null : encounter?.Slots.LastOrDefault();
        Creature enemy = state.CreateCreature(monster, CombatSide.Enemy, enemySlot);
        state.AddCreature(enemy);
        monster.SetUpForCombat();
        if (enemyCurrentHp is int hp)
            enemy.SetCurrentHpInternal(Math.Clamp(hp, 1, enemy.MaxHp));
        monster.RollMove(players.Select(static player => player.Creature));
        return new OfflineJointCombat(state, Array.AsReadOnly(players), enemy);
    }
}
