using MegaCrit.Sts2.Core.Entities.Players;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed record JointActorSnapshot(
    CombatActorId Id,
    Player PlayerIdentity,
    int Turn,
    JointActorTurnPhase Phase,
    int Hp,
    int MaxHp,
    int Block,
    int Energy,
    int Stars,
    int Gold,
    int HandCount,
    int DrawCount,
    int DiscardCount,
    int ExhaustCount,
    int PlayCount);

/// <summary>
/// Complete offline joint search projection. The simulator remains the mutable branch owner;
/// this object only retains immutable actor values, the strict continuation text and a key.
/// </summary>
internal sealed record JointCombatSnapshot(
    int Turn,
    JointTurnState TurnState,
    IReadOnlyList<JointActorSnapshot> Actors,
    ContinuationStamp Continuation,
    StateFingerprint StateKey,
    CombatPredictionSimulator Simulator)
{
    internal static JointCombatSnapshot Capture(
        CombatRootSnapshot root,
        CombatPredictionSimulator simulator,
        JointTurnState turnState)
    {
        if (turnState.ActorCount != simulator.State.Players.Count)
            throw new InvalidOperationException(
                $"联合快照 ActorCount={turnState.ActorCount} 与模拟器玩家数={simulator.State.Players.Count} 不一致。");

        JointActorSnapshot[] actors = new JointActorSnapshot[simulator.State.Players.Count];
        for (int index = 0; index < actors.Length; index++)
        {
            Player player = simulator.State.Players[index];
            SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(player);
            SimCreatureState creature = simulator.State.GetCreature(player.Creature);
            JointActorTurnPhase phase = turnState.Phases[index];
            if (creature.IsDead)
                phase = JointActorTurnPhase.Dead;
            actors[index] = new JointActorSnapshot(
                new CombatActorId(index),
                player,
                ((SimulatedCombatState)simulator.State.CombatState).GetPlayerTurnNumber(player),
                phase,
                creature.CurrentHp,
                creature.MaxHp,
                creature.Block,
                playerState.Energy,
                playerState.Stars,
                ((SimulatedCombatState)simulator.State.CombatState).GetPlayerGold(player),
                playerState.Hand.Cards.Count,
                playerState.DrawPile.Cards.Count,
                playerState.DiscardPile.Cards.Count,
                playerState.ExhaustPile.Cards.Count,
                playerState.PlayPile.Cards.Count);
        }

        ContinuationStamp continuation = ContinuationStamp.CapturePredicted(
            root.PlayerIdentity,
            simulator,
            turnState.Turn,
            root.Forecast,
            root.StartTurnNumber);
        StateFingerprintBuilder key = new();
        key.Add(turnState.Turn);
        key.Add(turnState.ActorCount);
        foreach (JointActorTurnPhase phase in turnState.Phases)
            key.Add((int)phase);
        key.Add(continuation.StateText);
        return new JointCombatSnapshot(
            turnState.Turn,
            turnState,
            Array.AsReadOnly(actors),
            continuation,
            key.Finish(),
            simulator);
    }
}
