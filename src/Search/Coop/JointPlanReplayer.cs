using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed record JointReplayResult(
    JointCombatSnapshot Snapshot,
    IReadOnlyList<PlanAction> AppliedActions);

internal static class JointPlanReplayer
{
    internal static JointReplayResult Replay(CombatRootSnapshot root, JointPlan plan)
    {
        plan.Validate();
        CombatPredictionSimulator simulator = root.ForkSimulator();
        JointTurnState turnState = JointTurnState.Start(root.Actors.Count, root.StartTurnNumber);
        List<PlanAction> applied = [];
        foreach (PlanAction action in plan.Actions)
        {
            while (action.Turn > turnState.Turn)
            {
                if (!turnState.IsBarrierReached)
                    throw new InvalidOperationException(
                        $"联合回放在回合 {turnState.Turn} 未完成全员屏障。");
                turnState = turnState.AdvanceTurn();
            }
            if (action.Turn < turnState.Turn)
                throw new InvalidOperationException($"联合回放回合倒退：{action.Turn} < {turnState.Turn}。");
            if (!turnState.IsActionable(action.Actor))
                throw new InvalidOperationException($"联合回放 Actor {action.Actor} 当前不可行动。");

            if (action.Kind == PlanActionKind.EndTurn || action.EndsPlayerTurn)
            {
                turnState = turnState.EndTurn(action.Actor);
                applied.Add(action);
                continue;
            }
            if (action.Kind != PlanActionKind.PlayCard)
                throw new InvalidOperationException($"联合回放暂不支持动作类型 {action.Kind}。");

            Player player = simulator.State.Players[action.Actor.Index];
            SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(player);
            PredictedCard? card = FindCard(playerState.Hand.Cards, action);
            if (card is null)
                throw new InvalidOperationException(
                    $"联合回放找不到 Actor {action.Actor} 的手牌 {action.CardId}#{action.CardOccurrence}。");
            if (!simulator.CanPlay(card))
                throw new InvalidOperationException($"联合回放动作不可支付：{action.CardId}。");
            Creature? target = FindTarget(simulator, action.TargetCombatId);
            if (!simulator.ManualPlay(card, target, out _))
                throw new InvalidOperationException(
                    $"联合回放动作产生未解决的选择：Actor {action.Actor} card={action.CardId}。");
            if (simulator.HasPendingChoice)
                throw new InvalidOperationException(
                    $"联合回放动作留下待处理选择：Actor {action.Actor} card={action.CardId}。");
            if (simulator.State.GetCreature(player.Creature).IsDead)
                turnState = turnState.MarkDead(action.Actor);
            applied.Add(action);
        }

        JointCombatSnapshot snapshot = JointCombatSnapshot.Capture(
            root,
            simulator,
            turnState);
        return new JointReplayResult(snapshot, applied.AsReadOnly());
    }

    private static PredictedCard? FindCard(
        IReadOnlyList<PredictedCard> hand,
        PlanAction action)
    {
        int occurrence = 0;
        foreach (PredictedCard card in hand)
        {
            if (!string.Equals(card.Preview.Id.Entry, action.CardId, StringComparison.Ordinal))
                continue;
            if (occurrence == action.CardOccurrence)
                return card;
            occurrence++;
        }
        return null;
    }

    private static Creature? FindTarget(
        CombatPredictionSimulator simulator,
        uint? combatId)
    {
        if (combatId is not uint id)
            return null;
        foreach (Creature creature in simulator.State.Creatures)
            if (creature.CombatId == id)
                return creature;
        throw new InvalidOperationException($"联合回放找不到目标 CombatId={id}。");
    }
}
