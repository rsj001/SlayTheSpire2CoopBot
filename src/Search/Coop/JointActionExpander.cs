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
    private const int MaximumNestedChoiceDepth = 16;

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
                    ReplayCount: Math.Max(0, card.Preview.GetEnchantedReplayCount()),
                    CardStateKey: CardChoiceSupport.ChoiceCardKey(card),
                    CardStateOccurrence: CountStateOccurrence(
                        playerState.Hand.Cards, card, CardChoiceSupport.ChoiceCardKey(card)),
                    CardEnchantmentId: card.Preview.Enchantment?.Id.Entry ?? string.Empty,
                    Actor: actor);
                foreach (Creature? target in ResolveTargets(simulator, card, player))
                {
                    PlanAction targeted = target is null
                        ? action
                        : action with { TargetCombatId = target.CombatId };
                    CardChoiceSpec? spec = CardChoiceSupport.GetSpec(simulator, card);
                    if (spec == null)
                    {
                        AddResolvedCandidates(
                            candidates,
                            simulator,
                            turnState,
                            new JointActionCandidate(
                            targeted with
                            {
                                Choice = CardChoiceSupport.BuildRequiredEmptyChoice(card.Preview)
                                    is { } empty ? empty with { Actor = actor } : null,
                            },
                            card,
                            target));
                        continue;
                    }
                    foreach (PlanCardChoice choice in CardChoiceSupport.BuildChoices(
                                 spec,
                                 static _ => string.Empty,
                                 maxPileBranches: 32,
                                 maxHandBranches: 32))
                    {
                        AddResolvedCandidates(
                            candidates,
                            simulator,
                            turnState,
                            new JointActionCandidate(
                            targeted with { Choice = choice with { Actor = actor } },
                            card,
                            target));
                    }
                }
            }

            candidates.Add(new JointActionCandidate(
                new PlanAction(PlanActionKind.EndTurn, turnState.Turn, Actor: actor),
                Card: null,
                Target: null));
        }
        return candidates;
    }

    private static void AddResolvedCandidates(
        List<JointActionCandidate> output,
        CombatPredictionSimulator parent,
        JointTurnState turns,
        JointActionCandidate seed)
    {
        Queue<(JointActionCandidate Candidate, int Depth)> open = new();
        open.Enqueue((seed, 0));
        while (open.TryDequeue(out var item))
        {
            CombatPredictionSimulator probe = parent.Fork();
            ForkableSet<uint> deaths = JointActionTransition.CaptureProcessedEnemyDeaths(probe);
            try
            {
                _ = JointActionTransition.Apply(probe, turns, item.Candidate.Action, deaths);
                output.Add(item.Candidate);
            }
            catch (JointPendingActionChoiceException pending)
            {
                if (item.Depth >= MaximumNestedChoiceDepth)
                {
                    throw new InvalidOperationException(
                        $"联合动作选择深度超过 {MaximumNestedChoiceDepth}：" +
                        $"Actor {pending.Actor} source={pending.SourceId}。",
                        pending);
                }
                IReadOnlyList<PlanCardChoice> branches = CardChoiceSupport.BuildChoices(
                    pending.Spec,
                    static _ => string.Empty,
                    maxPileBranches: 32,
                    maxHandBranches: 32);
                if (branches.Count == 0)
                    throw new InvalidOperationException(
                        $"联合动作选择没有合法分支：Actor {pending.Actor} source={pending.SourceId}。",
                        pending);
                foreach (PlanCardChoice branch in branches)
                {
                    PlanCardChoice owned = branch with
                    {
                        Actor = pending.Actor,
                        SourceId = pending.SourceId,
                    };
                    PlanAction expanded = AppendNestedChoice(item.Candidate.Action, owned);
                    open.Enqueue((item.Candidate with { Action = expanded }, item.Depth + 1));
                }
            }
        }
    }

    private static PlanAction AppendNestedChoice(PlanAction action, PlanCardChoice choice)
    {
        List<PlanCardChoice> nested = [.. action.NestedChoices ?? []];
        nested.Add(choice);
        return action with { NestedChoices = nested.AsReadOnly() };
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
        if (type == TargetType.AnyPlayer)
        {
            foreach (Creature player in simulator.State.PlayerCreatures.Where(simulator.State.IsHittable))
                yield return player;
            yield break;
        }
        if (type is TargetType.None
            or TargetType.Self
            or TargetType.AllEnemies
            or TargetType.RandomEnemy
            or TargetType.AllAllies
            or TargetType.TargetedNoCreature
            or TargetType.Osty)
        {
            yield return null;
            yield break;
        }
        throw new NotSupportedException(
            $"联合卡牌目标类型未登记：Actor={owner.NetId} card={card.Preview.Id.Entry} target={type}。");
    }

    private static int CountStateOccurrence(
        IReadOnlyList<PredictedCard> hand,
        PredictedCard selected,
        string stateKey)
    {
        int occurrence = 0;
        foreach (PredictedCard card in hand)
        {
            if (ReferenceEquals(card, selected))
                return occurrence;
            if (string.Equals(CardChoiceSupport.ChoiceCardKey(card), stateKey, StringComparison.Ordinal))
                occurrence++;
        }
        throw new InvalidOperationException("联合候选卡牌不在所属 Actor 的手牌中。");
    }
}
