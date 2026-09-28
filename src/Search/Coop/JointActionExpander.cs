using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
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

            SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
            for (int slot = 0; slot < player.PotionSlots.Count; slot++)
            {
                PotionModel? potion = combat.GetPotionAtSlot(player, slot);
                if (potion == null
                    || !combat.IsPotionAvailable(player, slot)
                    || !PotionOnUseSupport.CanSearch(potion))
                {
                    continue;
                }
                foreach (Creature? target in ResolvePotionTargets(simulator, potion, player))
                {
                    AddResolvedCandidates(
                        candidates,
                        simulator,
                        turnState,
                        new JointActionCandidate(
                            new PlanAction(
                                PlanActionKind.UsePotion,
                                turnState.Turn,
                                TargetCombatId: target?.CombatId,
                                PotionSlot: slot,
                                PotionId: potion.Id.Entry,
                                Actor: actor),
                            Card: null,
                            Target: target));
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
                JointPendingChoiceFrame frame = pending.Frame;
                if (frame.SourceAction != item.Candidate.Action
                    || frame.OwnerActor != item.Candidate.Action.Actor)
                {
                    throw new InvalidOperationException(
                        "联合选择帧的 owner 或 SourceAction 与探测动作不一致。",
                        pending);
                }
                if (item.Depth >= MaximumNestedChoiceDepth)
                {
                    throw new InvalidOperationException(
                        $"联合动作选择深度超过 {MaximumNestedChoiceDepth}：" +
                        $"Actor {frame.OwnerActor} source={frame.SourceId}。",
                        pending);
                }
                IReadOnlyList<PlanCardChoice> branches = CardChoiceSupport.BuildChoices(
                    frame.Spec,
                    static _ => string.Empty,
                    maxPileBranches: 32,
                    maxHandBranches: 32);
                if (branches.Count == 0)
                    throw new InvalidOperationException(
                        $"联合动作选择没有合法分支：Actor {frame.OwnerActor} source={frame.SourceId}。",
                        pending);
                foreach (PlanCardChoice branch in branches)
                {
                    PlanCardChoice owned = branch with
                    {
                        Actor = frame.OwnerActor,
                        SourceId = frame.SourceId,
                        ContextId = frame.ContextId,
                        Timing = frame.Timing,
                    };
                    PlanAction expanded = frame.Placement switch
                    {
                        JointPendingChoicePlacement.Primary when item.Candidate.Action.Choice == null
                            => item.Candidate.Action with { Choice = owned },
                        JointPendingChoicePlacement.Primary
                            => throw new InvalidOperationException(
                                $"联合动作重复请求主选择：Actor {frame.OwnerActor} source={frame.SourceId}。",
                                pending),
                        JointPendingChoicePlacement.Nested
                            => AppendNestedChoice(item.Candidate.Action, owned),
                        _ => throw new ArgumentOutOfRangeException(nameof(frame.Placement)),
                    };
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

    private static IEnumerable<Creature?> ResolvePotionTargets(
        CombatPredictionSimulator simulator,
        PotionModel potion,
        Player owner)
    {
        IEnumerable<Creature?> candidates = potion.TargetType switch
        {
            TargetType.AnyEnemy => simulator.State.HittableEnemies.Cast<Creature?>(),
            TargetType.AnyPlayer => simulator.State.PlayerCreatures
                .Where(simulator.State.IsHittable).Cast<Creature?>(),
            TargetType.AnyAlly => simulator.State.GetTeammatesOf(owner.Creature)
                .Where(simulator.State.IsHittable).Cast<Creature?>(),
            TargetType.Self => new Creature?[] { null },
            TargetType.None or TargetType.AllEnemies or TargetType.AllAllies
                or TargetType.RandomEnemy or TargetType.TargetedNoCreature or TargetType.Osty
                => new Creature?[] { null },
            _ => throw new NotSupportedException(
                $"联合药水目标类型未登记：Actor={owner.NetId} potion={potion.Id.Entry} " +
                $"target={potion.TargetType}。"),
        };
        foreach (Creature? target in candidates)
        {
            if (target == null || potion.IsValidTarget(target))
                yield return target;
        }
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
