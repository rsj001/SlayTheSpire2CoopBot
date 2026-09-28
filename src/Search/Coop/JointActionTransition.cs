using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed class JointPendingActionChoiceException(
    CombatActorId actor,
    string sourceId,
    CardChoiceSpec spec) : InvalidOperationException(
        $"联合动作等待选择：Actor {actor} source={sourceId} effect={spec.Effect}。")
{
    internal CombatActorId Actor { get; } = actor;
    internal string SourceId { get; } = sourceId;
    internal CardChoiceSpec Spec { get; } = spec;
}

/// <summary>
/// Authoritative one-action transition shared by offline joint search and strict replay.
/// Enumeration policy is intentionally kept out of this type.
/// </summary>
internal static class JointActionTransition
{
    internal static ForkableSet<uint> CaptureProcessedEnemyDeaths(
        CombatRootSnapshot root,
        CombatPredictionSimulator simulator)
    {
        ForkableSet<uint> deaths = [];
        foreach (Creature enemy in root.Enemies)
        {
            if (enemy.CombatId is uint combatId && simulator.State.GetCreature(enemy).IsDead)
                deaths.Add(combatId);
        }
        return deaths;
    }

    internal static ForkableSet<uint> CaptureProcessedEnemyDeaths(
        CombatPredictionSimulator simulator)
    {
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        ForkableSet<uint> deaths = [];
        foreach (Creature enemy in combat.KnownEnemies)
        {
            if (enemy.CombatId is uint combatId && simulator.State.GetCreature(enemy).IsDead)
                deaths.Add(combatId);
        }
        return deaths;
    }

    internal static JointTurnState Apply(
        CombatPredictionSimulator simulator,
        JointTurnState turnState,
        PlanAction action,
        ForkableSet<uint> processedEnemyDeaths)
    {
        if (simulator.TerminalStamp.HasValue)
            throw new InvalidOperationException("联合终局后不能继续执行动作。");
        if (action.Turn != turnState.Turn)
            throw new InvalidOperationException(
                $"联合动作回合 {action.Turn} 与当前回合 {turnState.Turn} 不一致。");
        if (!turnState.IsActionable(action.Actor))
            throw new InvalidOperationException($"联合动作 Actor {action.Actor} 当前不可行动。");

        if (action.Kind == PlanActionKind.EndTurn)
            return turnState.EndTurn(action.Actor);

        Player player = simulator.State.Players[action.Actor.Index];
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        JointTurnState result = action.Kind switch
        {
            PlanActionKind.PlayCard => ApplyCard(
                simulator, combat, player, turnState, action, processedEnemyDeaths),
            PlanActionKind.UsePotion => ApplyPotion(
                simulator, combat, player, turnState, action, processedEnemyDeaths),
            _ => throw new InvalidOperationException(
                $"联合单步执行不支持 Actor {action.Actor} 的动作类型 {action.Kind}。"),
        };
        simulator.CheckWinCondition(turnState.Turn);
        for (int index = 0; index < simulator.State.Players.Count; index++)
        {
            if (simulator.State.GetCreature(simulator.State.Players[index].Creature).IsDead)
                result = result.MarkDead(new CombatActorId(index));
        }
        return result;
    }

    private static JointTurnState ApplyCard(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        Player player,
        JointTurnState turnState,
        PlanAction action,
        ForkableSet<uint> processedEnemyDeaths)
    {
        SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(player);
        PredictedCard card = CombatBeamSolver.FindCardForReplay(playerState.Hand.Cards, action)
            ?? throw new InvalidOperationException(
                $"联合动作找不到 Actor {action.Actor} 的手牌 {action.CardId}#{action.CardOccurrence}。");
        if (!simulator.CanPlay(card))
            throw new InvalidOperationException(
                $"联合动作不可支付：Actor {action.Actor} card={action.CardId}#{action.CardOccurrence}。");
        Creature? target = FindTarget(simulator, action.TargetCombatId);
        combat.BeginActionChoices(ActionChoices(action));
        using IDisposable cardScope = combat.BeginCardExecutionScope(processedEnemyDeaths);
        try
        {
            if (!simulator.ManualPlay(card, target, out _))
                throw PendingChoice(simulator, combat, player, action);
            if (!CorePowerSupport.ApplyEnemyDeathPowers(
                    simulator, combat, combat.KnownEnemies, processedEnemyDeaths)
                || !CombatBeamSolver.SettleReplayActionBoundary(simulator, combat)
                || simulator.HasPendingChoice)
            {
                if (combat.PendingTurnStartChoice != null)
                    throw PendingChoice(simulator, combat, player, action);
                throw new InvalidOperationException(
                    $"联合动作产生未解决的选择：Actor {action.Actor} card={action.CardId}。" );
            }
        }
        finally
        {
            combat.EndActionChoices();
        }
        return action.EndsPlayerTurn || combat.ConsumePlayerTurnEndRequest()
            ? turnState.EndTurn(action.Actor)
            : turnState;
    }

    private static JointPendingActionChoiceException PendingChoice(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        Player player,
        PlanAction action)
    {
        TurnStartChoiceRequest request = combat.PendingTurnStartChoice
            ?? throw new InvalidOperationException(
                $"联合动作挂起但没有选择请求：Actor {action.Actor} card={action.CardId}。");
        CardChoiceSpec spec = TurnStartChoiceSupport.BuildSpec(simulator, player, request);
        return new JointPendingActionChoiceException(action.Actor, request.SourceId, spec);
    }

    private static JointTurnState ApplyPotion(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        Player player,
        JointTurnState turnState,
        PlanAction action,
        ForkableSet<uint> processedEnemyDeaths)
    {
        PotionModel potion = combat.GetPotionAtSlot(player, action.PotionSlot)
            ?? throw new InvalidOperationException(
                $"联合动作药水槽为空：Actor {action.Actor} slot={action.PotionSlot}。");
        if (!string.Equals(potion.Id.Entry, action.PotionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"联合动作药水身份变化：Actor {action.Actor} slot={action.PotionSlot} " +
                $"actual={potion.Id.Entry} expected={action.PotionId}。");
        }
        Creature? target = FindTarget(simulator, action.TargetCombatId);
        int historyStart = simulator.History.Entries.Count;
        combat.BeginActionChoices(action.NestedChoices);
        try
        {
            if (!PotionExecutionSupport.Prepare(
                    simulator, combat, potion, action.PotionSlot, target)
                || !PotionExecutionSupport.Complete(
                    simulator, combat, potion, target, action.Choice,
                    historyStart, processedEnemyDeaths)
                || !CombatBeamSolver.SettleReplayActionBoundary(simulator, combat)
                || simulator.HasPendingChoice)
            {
                if (combat.PendingTurnStartChoice != null)
                    throw PendingChoice(simulator, combat, player, action);
                throw new InvalidOperationException(
                    $"联合药水动作产生未解决的选择：Actor {action.Actor} potion={action.PotionId}。" );
            }
        }
        finally
        {
            combat.EndActionChoices();
        }
        return action.EndsPlayerTurn || combat.ConsumePlayerTurnEndRequest()
            ? turnState.EndTurn(action.Actor)
            : turnState;
    }

    private static IReadOnlyList<PlanCardChoice>? ActionChoices(PlanAction action)
    {
        IReadOnlyList<PlanCardChoice> choices = action.GetActionChoicesInExecutionOrder();
        return choices.Count == 0 ? null : choices;
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
