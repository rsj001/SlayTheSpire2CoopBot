namespace CombatSolver;

using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

internal sealed partial class UnattendedTestRunner
{
    private static void AssertCoopActorPlanContract()
    {
        new JointPlan(1, [new PlanAction(PlanActionKind.EndTurn, 1)]).Validate();

        CombatActorId second = new(1);
        PlanCardChoice secondChoice = new(
            PlanChoiceEffect.Discard,
            MegaCrit.Sts2.Core.Entities.Cards.PileType.Hand,
            [],
            Actor: second);
        new JointPlan(2,
        [
            new PlanAction(
                PlanActionKind.PlayCard,
                1,
                CardId: "TEST.COOP.ACTOR.ONE",
                Choice: secondChoice,
                Actor: second),
        ]).Validate();

        AssertInvalidJointPlan(
            new JointPlan(2, [new PlanAction(PlanActionKind.EndTurn, 1, Actor: new CombatActorId(2))]),
            "范围外 Actor 未被拒绝。");
        AssertInvalidJointPlan(
            new JointPlan(2,
            [
                new PlanAction(
                    PlanActionKind.PlayCard,
                    1,
                    CardId: "TEST.COOP.CHOICE.OWNER",
                    Choice: secondChoice with { Actor = default },
                    Actor: second),
            ]),
            "跨 Actor 选牌归属未被拒绝。");
    }

    private static void AssertInvalidJointPlan(JointPlan plan, string message)
    {
        try
        {
            plan.Validate();
        }
        catch (InvalidOperationException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static void AssertCoopJointTurnBarrier()
    {
        CombatActorId actor0 = new(0);
        CombatActorId actor1 = new(1);
        JointTurnState state = JointTurnState.Start(2);
        if (!state.IsActionable(actor0) || !state.IsActionable(actor1) || state.IsBarrierReached)
            throw new InvalidOperationException("联合回合初始可行动状态错误。");
        state = state.EndTurn(actor1);
        if (state.IsBarrierReached || !state.IsActionable(actor0))
            throw new InvalidOperationException("单个 Actor 结束回合错误地越过了全员屏障。");
        state = state.EndTurn(actor0);
        if (!state.IsBarrierReached)
            throw new InvalidOperationException("全员结束回合后未到达屏障。");
        state = state.AdvanceTurn();
        if (state.Turn != 2 || !state.IsActionable(actor0) || !state.IsActionable(actor1))
            throw new InvalidOperationException("联合回合屏障推进后阶段错误。");
        state = state.EndTurn(actor0);
        state = state.MarkDead(actor1);
        if (!state.IsBarrierReached)
            throw new InvalidOperationException("死亡 Actor 未被视为已通过屏障。");

        JointPlan valid = new(2,
        [
            new PlanAction(PlanActionKind.PlayCard, 1, CardId: "A0", Actor: actor0),
            new PlanAction(PlanActionKind.EndTurn, 1, Actor: actor1),
            new PlanAction(PlanActionKind.EndTurn, 1, Actor: actor0),
            new PlanAction(PlanActionKind.EndTurn, 2, Actor: actor0),
            new PlanAction(PlanActionKind.EndTurn, 2, Actor: actor1),
        ]);
        valid.ValidateComplete();

        AssertInvalidJointPlan(
            new JointPlan(2,
            [
                new PlanAction(PlanActionKind.EndTurn, 1, Actor: actor1),
                new PlanAction(PlanActionKind.PlayCard, 2, CardId: "EARLY", Actor: actor0),
            ]),
            "未完成全员屏障就进入下一回合。");
    }

    private static void AssertCoopActorCandidates(CombatState combatState, Player localPlayer)
    {
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combatState);
        CombatPredictionSimulator simulator = root.ForkSimulator();
        JointTurnState turns = JointTurnState.Start(simulator.State.Players.Count, root.StartTurnNumber);
        IReadOnlyList<JointActionCandidate> candidates = JointActionExpander.Expand(simulator, turns);
        IReadOnlyList<JointActionCandidate> repeated = JointActionExpander.Expand(simulator, turns);
        if (candidates.Count == 0 || candidates.Count != repeated.Count
            || candidates.Where(static candidate => candidate.Action.Kind == PlanActionKind.EndTurn).Count()
                != simulator.State.Players.Count)
            throw new InvalidOperationException("联合候选未为每个 Actor 生成 EndTurn 或结果不稳定。");
        if (candidates.Any(candidate => candidate.Action.Actor.Index < 0
            || candidate.Action.Actor.Index >= simulator.State.Players.Count))
            throw new InvalidOperationException("联合候选包含越界 Actor。");
        string first = string.Join('|', candidates.Select(candidate =>
            $"{candidate.Action.Actor.Index}:{candidate.Action.Kind}:{candidate.Action.CardId}:{candidate.Action.CardOccurrence}:{candidate.Action.TargetCombatId}"));
        string second = string.Join('|', repeated.Select(candidate =>
            $"{candidate.Action.Actor.Index}:{candidate.Action.Kind}:{candidate.Action.CardId}:{candidate.Action.CardOccurrence}:{candidate.Action.TargetCombatId}"));
        if (!string.Equals(first, second, StringComparison.Ordinal))
            throw new InvalidOperationException("联合候选展开不是确定性的。");
        if (candidates.Any(candidate => candidate.Action.Actor != root.LocalActorId
            && ReferenceEquals(candidate.Card?.Preview.Owner, localPlayer)))
            throw new InvalidOperationException("联合候选的卡牌所有者与 Actor 身份不一致。");
    }

    private static void AssertCoopJointSnapshotKey(CombatState combatState)
    {
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combatState);
        CombatPredictionSimulator basis = root.ForkSimulator();
        JointTurnState turns = JointTurnState.Start(basis.State.Players.Count, root.StartTurnNumber);
        JointCombatSnapshot before = JointCombatSnapshot.Capture(root, basis, turns);
        CombatPredictionSimulator fork = basis.Fork();
        JointCombatSnapshot same = JointCombatSnapshot.Capture(root, fork, turns);
        if (before.StateKey != same.StateKey || before.Continuation.StateText != same.Continuation.StateText)
            throw new InvalidOperationException("等价联合 Fork 的快照或续用文本不一致。");

        SimulatedCombatState combat = (SimulatedCombatState)fork.State.CombatState;
        Player player = fork.State.Players[0];
        fork.GainBlock(player.Creature, 7, default);
        JointCombatSnapshot changed = JointCombatSnapshot.Capture(root, fork, turns);
        if (changed.StateKey == before.StateKey || changed.Continuation.StateText == before.Continuation.StateText)
            throw new InvalidOperationException("Actor 分支变更未进入联合状态键。");
        if (before.Actors[0].Block != same.Actors[0].Block || before.Actors[0].Block == changed.Actors[0].Block)
            throw new InvalidOperationException("联合 Actor 快照未保持 Fork 隔离。");

        JointTurnState barrierChanged = turns.EndTurn(new CombatActorId(0));
        JointCombatSnapshot barrier = JointCombatSnapshot.Capture(root, basis, barrierChanged);
        if (barrier.StateKey == before.StateKey)
            throw new InvalidOperationException("联合回合屏障变化未进入状态键。");
    }

    private static void AssertCoopJointReplay(CombatState combatState)
    {
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combatState);
        CombatPredictionSimulator simulator = root.ForkSimulator();
        JointTurnState turns = JointTurnState.Start(root.Actors.Count, root.StartTurnNumber);
        IReadOnlyList<JointActionCandidate> candidates = JointActionExpander.Expand(simulator, turns);
        JointActionCandidate? card = candidates.FirstOrDefault(candidate =>
            candidate.Card != null && CardChoiceSupport.GetSpec(simulator, candidate.Card) == null);
        PlanAction[] actions = card is { } selected
            ? [selected.Action, new PlanAction(PlanActionKind.EndTurn, root.StartTurnNumber, Actor: selected.Action.Actor)]
            : [new PlanAction(PlanActionKind.EndTurn, root.StartTurnNumber, Actor: root.LocalActorId)];
        JointReplayResult replay = JointPlanReplayer.Replay(
            root,
            new JointPlan(root.Actors.Count, actions));
        if (replay.AppliedActions.Count != actions.Length
            || replay.Snapshot.TurnState.Phases[replay.Snapshot.TurnState.Phases.Count - 1]
                == JointActorTurnPhase.Dead)
            throw new InvalidOperationException("联合回放未应用完整动作序列或错误标记 Actor 死亡。");
        if (card is { } && replay.Snapshot.StateKey == JointCombatSnapshot.Capture(
                root,
                root.ForkSimulator(),
                turns).StateKey)
            throw new InvalidOperationException("联合回放的卡牌动作未改变严格状态键。");

        if (card is { } selectedCard)
        {
            CombatPredictionSimulator direct = root.ForkSimulator();
            JointTurnState directTurns = JointTurnState.Start(root.Actors.Count, root.StartTurnNumber);
            ForkableSet<uint> directDeaths =
                JointActionTransition.CaptureProcessedEnemyDeaths(root, direct);
            directTurns = JointActionTransition.Apply(
                direct, directTurns, selectedCard.Action, directDeaths);
            JointCombatSnapshot directSnapshot = JointCombatSnapshot.Capture(root, direct, directTurns);
            JointReplayResult singleReplay = JointPlanReplayer.Replay(
                root,
                new JointPlan(root.Actors.Count, [selectedCard.Action]));
            if (directSnapshot.StateKey != singleReplay.Snapshot.StateKey
                || directSnapshot.Continuation.StateText != singleReplay.Snapshot.Continuation.StateText)
                throw new InvalidOperationException("联合搜索单步与计划回放没有产生相同状态。");
        }

        SolverDisplayNames names = SolverDisplayNames.Capture(combatState);
        JointActionCandidate? choiceCard = candidates.FirstOrDefault(candidate =>
            candidate.Card != null && CardChoiceSupport.GetSpec(simulator, candidate.Card) != null);
        if (choiceCard is { Card: { } predictedChoiceCard } selectedChoiceCard)
        {
            CardChoiceSpec spec = CardChoiceSupport.GetSpec(simulator, predictedChoiceCard)
                ?? throw new InvalidOperationException("联合选择卡牌的 spec 在同一根发生变化。");
            PlanCardChoice choice = CardChoiceSupport.BuildChoices(spec, names, 32, 32)
                .First() with { Actor = selectedChoiceCard.Action.Actor };
            PlanAction resolved = selectedChoiceCard.Action with { Choice = choice };
            JointReplayResult choiceReplay = JointPlanReplayer.Replay(
                root,
                new JointPlan(root.Actors.Count, [resolved]));
            if (choiceReplay.Snapshot.Simulator.HasPendingChoice)
                throw new InvalidOperationException("联合 transition 没有消费指定 Actor 的卡牌选择。");
        }

        JointActionCandidate? nestedCandidate = candidates.FirstOrDefault(candidate =>
            candidate.Action.NestedChoices is { Count: > 0 });
        if (nestedCandidate is { } nested)
        {
            JointReplayResult nestedReplay = JointPlanReplayer.Replay(
                root,
                new JointPlan(root.Actors.Count, [nested.Action]));
            if (nestedReplay.Snapshot.Simulator.HasPendingChoice)
                throw new InvalidOperationException("联合 transition 没有消费动态嵌套选择链。");
        }

        SimulatedCombatState rootCombat = (SimulatedCombatState)simulator.State.CombatState;
        Player potionOwner = simulator.State.Players[root.LocalActorId.Index];
        for (int slot = 0; slot < root.PotionSlotCount; slot++)
        {
            PotionModel? potion = rootCombat.GetPotionAtSlot(potionOwner, slot);
            if (potion == null || PotionChoiceSupport.RequiresChoice(potion))
                continue;
            PlanAction potionAction = new(
                PlanActionKind.UsePotion,
                root.StartTurnNumber,
                PotionSlot: slot,
                PotionId: potion.Id.Entry,
                Actor: root.LocalActorId);
            JointReplayResult potionReplay = JointPlanReplayer.Replay(
                root,
                new JointPlan(root.Actors.Count, [potionAction]));
            SimulatedCombatState replayCombat =
                (SimulatedCombatState)potionReplay.Snapshot.Simulator.State.CombatState;
            if (!replayCombat.PotionUses.Any(use => use.PotionId == potion.Id.Entry))
                throw new InvalidOperationException("联合 transition 未记录药水动作。");
            break;
        }
    }
}
