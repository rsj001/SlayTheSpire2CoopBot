using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertMultiplayerNativeRoundDifferentialAsync(CombatState source, int actorCount)
    {
        OfflineJointCombat native = CreateOfflineJointCombat(
            source,
            actorCount,
            enemyModel: ModelDb.Monster<SludgeSpinner>());
        foreach (Player player in native.Players)
        {
            foreach (RelicModel relic in player.Relics.ToArray())
                player.RemoveRelicInternal(relic, silent: true);
        }
        ForceNativeMove(native.Enemy, "OIL_SPRAY_MOVE");
        CombatRootSnapshot root = CombatRootSnapshot.Capture(native.State);
        CombatPredictionSimulator predicted = root.ForkSimulator();
        JointTurnState turns = JointTurnState.Start(actorCount, root.StartTurnNumber);
        foreach (CombatActorRoot actor in root.Actors)
            turns = turns.EndTurn(actor.Id);
        ForkableSet<uint> deaths = JointActionTransition.CaptureProcessedEnemyDeaths(root, predicted);
        IReadOnlyList<CombatActorId> extraTurns = JointRoundTransition.CompletePlayerSide(
            predicted,
            turns,
            deaths);
        if (extraTurns.Count != 0)
            throw new InvalidOperationException("Native round differential unexpectedly scheduled an extra turn.");
        JointRoundTransition.CompleteBasicEnemySide(predicted, deaths);
        JointTurnState nextTurns = JointRoundTransition.StartBasicPlayerSide(predicted, turns, deaths);

        await CompleteSyntheticNativePlayerSideAsync(native);
        await CompleteSyntheticNativeEnemySideAsync(native);
        await StartSyntheticNativePlayerSideAsync(native);

        AssertMultiplayerLifecycleSnapshots(native, root, predicted, $"FullRound.ActorCount{actorCount}");
        SimulatedCombatState predictedCombat = (SimulatedCombatState)predicted.State.CombatState;
        if (native.State.CurrentSide != CombatSide.Player
            || predictedCombat.CurrentSide != CombatSide.Player
            || native.State.RoundNumber != predictedCombat.RoundNumber
            || nextTurns.Turn != root.StartTurnNumber + 1
            || native.Players.Where((player, index) =>
                    player.PlayerCombatState!.TurnNumber
                    != predictedCombat.GetPlayerTurnNumber(root.Actors[index].PlayerIdentity))
                .Any())
        {
            throw new InvalidOperationException(
                $"{actorCount} Actor full-round boundary did not return to an equivalent player side.");
        }
    }

    private static void ForceNativeMove(Creature enemy, string moveId)
    {
        MonsterMoveStateMachine machine = enemy.Monster?.MoveStateMachine
            ?? throw new InvalidOperationException("Synthetic native enemy has no move state machine.");
        if (!machine.States.TryGetValue(moveId, out MonsterState? state) || state is not MoveState move)
            throw new InvalidOperationException($"Synthetic native enemy has no move {moveId}.");
        machine.ForceCurrentState(move);
    }

    private static async Task CompleteSyntheticNativePlayerSideAsync(OfflineJointCombat native)
    {
        Creature[] participants = native.Players
            .Where(static player => !player.Creature.IsDead)
            .Select(static player => player.Creature)
            .ToArray();
        await Hook.BeforeSideTurnEnd(native.State, CombatSide.Player, participants);
        foreach (Player player in native.Players.Where(static player => !player.Creature.IsDead))
        {
            PlayerCombatState state = player.PlayerCombatState!;
            var context = new BlockingPlayerChoiceContext();
            await state.OrbQueue.BeforeTurnEnd(context);
            foreach (CardModel ethereal in state.Hand.Cards
                         .Where(card => card.Keywords.Contains(CardKeyword.Ethereal)
                             && Hook.ShouldEtherealTrigger(native.State, card))
                         .ToArray())
            {
                await CardCmd.Exhaust(context, ethereal, causedByEthereal: true, skipVisuals: true);
            }
            if (state.Hand.Cards.Any(static card => card.HasTurnEndInHandEffect))
                throw new InvalidOperationException("Full-round fixture requires a hand without turn-end cards.");
            await Hook.BeforeFlush(native.State, player);
            CardModel[] flushed = state.Hand.Cards
                .Where(static card => !card.ShouldRetainThisTurn)
                .ToArray();
            CardModel[] retained = state.Hand.Cards
                .Where(static card => card.ShouldRetainThisTurn)
                .ToArray();
            if (flushed.Length > 0)
                await CardPileCmd.Add(flushed, PileType.Discard);
            await Hook.AfterFlush(native.State, player, context, flushed, retained);
            state.EndOfTurnCleanup();
        }
        await Hook.AfterSideTurnEnd(native.State, CombatSide.Player, participants);
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
    }

    private static async Task CompleteSyntheticNativeEnemySideAsync(OfflineJointCombat native)
    {
        foreach (Creature creature in native.State.Creatures)
            creature.OnSideSwitch();
        native.State.CurrentSide = CombatSide.Enemy;
        Creature[] enemies = native.State.Enemies.ToArray();
        foreach (Creature enemy in enemies)
            enemy.BeforeTurnStart(CombatSide.Enemy);
        await Hook.BeforeSideTurnStart(native.State, CombatSide.Enemy, enemies);
        foreach (Creature enemy in enemies)
        {
            await enemy.AfterTurnStart(CombatSide.Enemy);
            await Hook.AfterBlockCleared(native.State, enemy);
        }
        await Hook.AfterSideTurnStart(native.State, CombatSide.Enemy, enemies);
        foreach (Creature enemy in enemies)
        {
            if (native.State.ContainsCreature(enemy))
                await enemy.TakeTurn();
        }
        await Hook.BeforeSideTurnEnd(native.State, CombatSide.Enemy, native.State.Enemies.ToArray());
        foreach (Player player in native.Players)
            player.PlayerCombatState!.EndOfTurnCleanup();
        await Hook.AfterSideTurnEnd(native.State, CombatSide.Enemy, native.State.Enemies.ToArray());
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
    }

    private static async Task StartSyntheticNativePlayerSideAsync(OfflineJointCombat native)
    {
        foreach (Creature creature in native.State.Creatures)
            creature.OnSideSwitch();
        native.State.CurrentSide = CombatSide.Player;
        native.State.RoundNumber++;
        foreach (Player player in native.Players.Where(static player => !player.Creature.IsDead))
            player.PlayerCombatState!.IncrementTurnNumber();
        foreach (Creature enemy in native.State.Enemies)
            enemy.PrepareForNextTurn(native.State.PlayerCreatures);

        Player[] players = native.Players.Where(static player => !player.Creature.IsDead).ToArray();
        Creature[] participants = players.Select(static player => player.Creature).ToArray();
        foreach (Creature participant in participants)
            participant.BeforeTurnStart(CombatSide.Player);
        await Hook.BeforeSideTurnStart(native.State, CombatSide.Player, participants);
        foreach (Creature participant in participants)
        {
            await participant.AfterTurnStart(CombatSide.Player);
            await Hook.AfterBlockCleared(native.State, participant);
        }
        foreach (Player player in players)
        {
            PlayerCombatState state = player.PlayerCombatState!;
            if (Hook.ShouldPlayerResetEnergy(native.State, player))
                state.ResetEnergy();
            else
                state.AddMaxEnergyToCurrent();
            await Hook.AfterEnergyReset(native.State, player);
            var context = new BlockingPlayerChoiceContext();
            await Hook.BeforeHandDraw(native.State, player, context);
            decimal drawCount = Hook.ModifyHandDraw(
                native.State,
                player,
                CombatManager.baseHandDrawCount,
                out IEnumerable<AbstractModel> modifiers);
            await Hook.AfterModifyingHandDraw(native.State, modifiers);
            await CardPileCmd.Draw(context, drawCount, player, fromHandDraw: true);
            await Hook.AfterPlayerTurnStart(native.State, context, player);
        }
        await Hook.AfterSideTurnStart(native.State, CombatSide.Player, participants);
        foreach (Player player in players)
        {
            var orbContext = new BlockingPlayerChoiceContext();
            await player.PlayerCombatState!.OrbQueue.AfterTurnStart(orbContext);
            var autoContext = new HookPlayerChoiceContext(
                player,
                player.NetId,
                GameActionType.CombatPlayPhaseOnly);
            await Hook.AfterAutoPrePlayPhaseEntered(autoContext, native.State, player);
            player.PlayerCombatState.Phase = PlayerTurnPhase.Play;
        }
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
    }
}
