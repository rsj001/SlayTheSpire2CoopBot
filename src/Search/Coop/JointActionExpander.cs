using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed record JointActionCandidate(
    PlanAction Action,
    PredictedCard? Card,
    Creature? Target);

/// <summary>
/// Deterministic Actor-aware candidate expansion. It only emits actions owned by Actors that
/// are still in the Playing phase; simulation/replay of a selected candidate is a later boundary.
/// </summary>
internal static class JointActionExpander
{
    internal static IReadOnlyList<JointActionCandidate> Expand(
        CombatPredictionSimulator simulator,
        JointTurnState turnState)
    {
        if (turnState.ActorCount != simulator.State.Players.Count)
            throw new InvalidOperationException(
                $"联合回合 ActorCount={turnState.ActorCount} 与模拟器玩家数={simulator.State.Players.Count} 不一致。");

        List<JointActionCandidate> candidates = [];
        for (int index = 0; index < simulator.State.Players.Count; index++)
        {
            CombatActorId actor = new(index);
            if (!turnState.IsActionable(actor))
                continue;
            Player player = simulator.State.Players[index];
            SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(player);
            Dictionary<string, int> occurrences = new(StringComparer.Ordinal);
            foreach (PredictedCard card in playerState.Hand.Cards)
            {
                string cardId = card.Preview.Id.Entry;
                int occurrence = occurrences.TryGetValue(cardId, out int count) ? count : 0;
                occurrences[cardId] = occurrence + 1;
                if (!simulator.CanPlay(card))
                    continue;

                PlanAction action = new(
                    PlanActionKind.PlayCard,
                    turnState.Turn,
                    CardId: cardId,
                    CardOccurrence: occurrence,
                    TargetIndex: -1,
                    CardUpgradeLevel: card.Preview.CurrentUpgradeLevel,
                    Actor: actor);
                foreach (Creature? target in ResolveTargets(simulator, card, player))
                {
                    candidates.Add(new JointActionCandidate(
                        target is null ? action : action with { TargetCombatId = target.CombatId },
                        card,
                        target));
                }
            }

            candidates.Add(new JointActionCandidate(
                new PlanAction(PlanActionKind.EndTurn, turnState.Turn, Actor: actor),
                Card: null,
                Target: null));
        }
        return candidates;
    }

    private static IEnumerable<Creature?> ResolveTargets(
        CombatPredictionSimulator simulator,
        PredictedCard card,
        Player owner)
    {
        TargetType type = simulator.GetTargetType(card);
        if (type == TargetType.AnyEnemy)
        {
            foreach (Creature enemy in simulator.State.HittableEnemies)
                yield return enemy;
            yield break;
        }
        if (type == TargetType.AnyAlly)
        {
            foreach (Creature ally in simulator.State.GetTeammatesOf(owner.Creature)
                .Where(simulator.State.IsHittable))
                yield return ally;
            yield break;
        }

        // All-target, self-target and targetless cards resolve their target inside the mirror.
        yield return null;
    }
}
