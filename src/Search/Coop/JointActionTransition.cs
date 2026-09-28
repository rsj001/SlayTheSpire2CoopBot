using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

/// <summary>
/// Authoritative one-action transition shared by offline joint search and strict replay.
/// Enumeration policy is intentionally kept out of this type.
/// </summary>
internal static class JointActionTransition
{
    internal static JointTurnState Apply(
        CombatPredictionSimulator simulator,
        JointTurnState turnState,
        PlanAction action)
    {
        if (simulator.TerminalStamp.HasValue)
            throw new InvalidOperationException("联合终局后不能继续执行动作。");
        if (action.Turn != turnState.Turn)
            throw new InvalidOperationException(
                $"联合动作回合 {action.Turn} 与当前回合 {turnState.Turn} 不一致。");
        if (!turnState.IsActionable(action.Actor))
            throw new InvalidOperationException($"联合动作 Actor {action.Actor} 当前不可行动。");

        if (action.Kind == PlanActionKind.EndTurn || action.EndsPlayerTurn)
            return turnState.EndTurn(action.Actor);
        if (action.Kind != PlanActionKind.PlayCard)
            throw new InvalidOperationException($"联合单步执行暂不支持动作类型 {action.Kind}。");

        Player player = simulator.State.Players[action.Actor.Index];
        SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(player);
        PredictedCard card = FindCard(playerState.Hand.Cards, action)
            ?? throw new InvalidOperationException(
                $"联合动作找不到 Actor {action.Actor} 的手牌 {action.CardId}#{action.CardOccurrence}。");
        if (!simulator.CanPlay(card))
            throw new InvalidOperationException($"联合动作不可支付：{action.CardId}。");
        Creature? target = FindTarget(simulator, action.TargetCombatId);
        if (!simulator.ManualPlay(card, target, out _))
            throw new InvalidOperationException(
                $"联合动作产生未解决的选择：Actor {action.Actor} card={action.CardId}。");
        if (simulator.HasPendingChoice)
            throw new InvalidOperationException(
                $"联合动作留下待处理选择：Actor {action.Actor} card={action.CardId}。");

        simulator.CheckWinCondition(turnState.Turn);
        for (int index = 0; index < simulator.State.Players.Count; index++)
        {
            if (simulator.State.GetCreature(simulator.State.Players[index].Creature).IsDead)
                turnState = turnState.MarkDead(new CombatActorId(index));
        }
        return turnState;
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
        throw new InvalidOperationException($"联合动作找不到目标 CombatId={id}。");
    }
}
