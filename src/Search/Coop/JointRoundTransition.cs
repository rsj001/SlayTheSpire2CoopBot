using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.InCombat.Mirrors;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal static class JointRoundTransition
{
    internal static JointTurnState StartBasicPlayerSide(
        CombatPredictionSimulator simulator,
        JointTurnState turns,
        ForkableSet<uint> processedEnemyDeaths,
        IReadOnlyList<PlanCardChoice>? turnStartChoices = null)
    {
        if (!turns.IsBarrierReached)
            throw new InvalidOperationException("联合玩家侧尚未完成上一轮屏障。");
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        Player[] players = simulator.State.Players
            .Where(player => simulator.State.GetCreature(player.Creature).IsAlive)
            .ToArray();
        Creature[] participants = players.Select(static player => player.Creature).ToArray();
        combat.BeginActionChoices(turnStartChoices);
        try
        {
            combat.SetActionChoiceTiming(PlanChoiceTiming.PlayerTurnStart);
            combat.CurrentSide = CombatSide.Player;
            combat.RoundNumber++;
            foreach (Player player in players)
            {
                combat.AdvancePlayerTurn(player);
                combat.BeginSideTurn(player.Creature);
            }
            combat.SnapshotPowerAmountsAtTurnStart(participants);
            RequireNoChoice(
                HookMirrors.BeforeSideTurnStart(simulator, CombatSide.Player, participants),
                combat,
                "BeforeSideTurnStart");

            foreach (Player player in players)
            {
                SimCreatureState creature = simulator.State.GetCreature(player.Creature);
                if (creature.Block > 0)
                {
                    if (combat.ShouldClearBlock(player.Creature, out AbstractModel? preventer))
                        creature.DamageBlock(creature.Block, ValueProp.Move);
                    else
                        PersistentRelicSupport.TriggerAfterPreventingBlockClear(
                            simulator,
                            preventer,
                            player.Creature);
                }
                RequireNoChoice(
                    CorePowerSupport.TriggerAfterBlockCleared(simulator, combat, player.Creature),
                    combat,
                    "AfterBlockCleared");
            }

            foreach (Player player in players)
            {
                CombatActorId actor = ActorOf(combat, player);
                SimPlayerCombatState state = simulator.State.GetPlayerCombatState(player);
                if (PersistentRelicSupport.ShouldPlayerResetEnergy(combat, player))
                    state.LoseEnergy(state.Energy);
                state.GainEnergy(PersistentPowerSupport.GetModifiedMaxEnergy(combat, player)
                    + combat.ConsumeEnergyNextTurn(player));
                RequireNoChoice(
                    !combat.HasPendingChoice
                    && PersistentPowerSupport.TriggerAfterEnergyReset(simulator, combat, player),
                    combat,
                    "AfterEnergyReset",
                    actor,
                    turns);
                TurnStartRelicSupport.TriggerAfterEnergyReset(simulator, combat, player);
                RequireNoChoice(!combat.HasPendingChoice, combat, "AfterEnergyReset relic", actor, turns);
                TurnStartRelicSupport.TriggerAfterEnergyResetLate(simulator, combat, player);
                RequireNoChoice(!combat.HasPendingChoice, combat, "AfterEnergyResetLate", actor, turns);

                TurnStartChoiceCursor choices = combat.ActiveExecutionChoices;
                combat.PrepareBeforeHandDraw(simulator, player, choices);
                RequireNoChoice(!combat.HasPendingChoice, combat, "BeforeHandDraw", actor, turns);
                using (simulator.BeginExecutionDispatch())
                    PowerLifecycleSupport.ResolvePowerAmountChanges(simulator, combat);
                RequireNoChoice(!combat.HasPendingChoice, combat, "BeforeHandDraw power resolution", actor, turns);
                int drawCount = PersistentPowerSupport.ConsumeModifiedHandDraw(
                    combat,
                    player,
                    CombatManager.baseHandDrawCount);
                int historyStart = simulator.History.Entries.Count;
                simulator.Draw(player, drawCount, fromHandDraw: true);
                RequireNoChoice(!combat.HasPendingChoice, combat, "hand draw", actor, turns);
                using (simulator.BeginExecutionDispatch())
                    TriggeredPowerSupport.CompensateHistorySince(
                        simulator,
                        combat,
                        historyStart);
                RequireNoChoice(!combat.HasPendingChoice, combat, "hand draw compensation", actor, turns);
                combat.TriggerAfterPlayerTurnStart(simulator, player.Creature, choices);
                RequireNoChoice(!combat.HasPendingChoice, combat, "AfterPlayerTurnStart", actor, turns);
            }

            RequireNoChoice(
                combat.TriggerSideTurnStart(
                    simulator,
                    CombatSide.Player,
                    participants,
                    decrementPlating: players.Any(player => combat.GetPlayerTurnNumber(player) != 1)),
                combat,
                "AfterSideTurnStart");
            RequireNoChoice(
                CorePowerSupport.ApplyEnemyDeathPowers(
                    simulator,
                    combat,
                    combat.KnownEnemies,
                    processedEnemyDeaths),
                combat,
                "player-side enemy deaths");
            foreach (Player player in players)
            {
                EnchantmentLifecycleSupport.TriggerAfterTurnStartOrbs(simulator, player);
                CombatActorId actor = ActorOf(combat, player);
                RequireNoChoice(!combat.HasPendingChoice, combat, "turn-start orbs", actor, turns);
                combat.TriggerAutoPrePlayEarly(
                    simulator,
                    player,
                    combat.GetPlayerTurnNumber(player),
                    combat.ActiveExecutionChoices,
                    processedEnemyDeaths);
                RequireNoChoice(!combat.HasPendingChoice, combat, "auto pre-play", actor, turns);
            }
            combat.ActiveExecutionChoices.AssertConsumed();
            combat.NormalizeAeonglassWithers(simulator);
            combat.NormalizeCardAfflictions(simulator);
            IReadOnlyList<ForecastMove> moves = combat.CurrentMonsterMoves();
            combat.SetPredictedEnemyIntents(
                moves.Where(move => move.AttackHits.Count > 0).Select(move => move.Owner));
            simulator.CheckWinCondition(combat.GetPlayerTurnNumber(players[0]));
            return turns.AdvanceTurn();
        }
        finally
        {
            combat.EndActionChoices();
        }
    }

    internal static void CompleteBasicEnemySide(
        CombatPredictionSimulator simulator,
        ForkableSet<uint> processedEnemyDeaths)
    {
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        Creature[] players = simulator.State.Players
            .Select(static player => player.Creature)
            .Where(creature => simulator.State.GetCreature(creature).IsAlive)
            .ToArray();
        Creature[] actingEnemies = combat.Enemies.ToArray();
        combat.CurrentSide = CombatSide.Enemy;
        foreach (Creature enemy in combat.Enemies)
            combat.BeginSideTurn(enemy);
        combat.SnapshotPowerAmountsAtTurnStart(combat.Enemies);
        if (!HookMirrors.BeforeSideTurnStart(simulator, CombatSide.Enemy, combat.Enemies))
            throw PendingEnemyChoice(combat);
        foreach (Creature enemy in combat.Enemies)
        {
            SimCreatureState state = simulator.State.GetCreature(enemy);
            if (state.Block > 0)
            {
                if (combat.ShouldClearBlock(enemy, out AbstractModel? preventer))
                    state.DamageBlock(state.Block, ValueProp.Move);
                else
                    PersistentRelicSupport.TriggerAfterPreventingBlockClear(simulator, preventer, enemy);
            }
            if (!CorePowerSupport.TriggerAfterBlockCleared(simulator, combat, enemy))
                throw PendingEnemyChoice(combat);
        }
        if (!combat.TriggerSideTurnStart(
                simulator,
                CombatSide.Enemy,
                combat.Enemies,
                decrementPlating: combat.RoundNumber > 1))
        {
            throw PendingEnemyChoice(combat);
        }
        if (!CorePowerSupport.TriggerPoison(simulator, combat, combat.Enemies.ToArray())
            || !CorePowerSupport.ApplyEnemyDeathPowers(
                simulator, combat, combat.KnownEnemies, processedEnemyDeaths))
        {
            throw PendingEnemyChoice(combat);
        }

        Dictionary<Creature, MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine.MoveState>
            performedMoves = new(actingEnemies.Length);
        foreach (Creature enemy in actingEnemies)
        {
            if (!combat.CanPerformMonsterMove(simulator, enemy))
                continue;
            ForecastMove move = combat.CurrentMonsterMove(enemy);
            MonsterMoveSemantics.ApplyBasicForecastMoveToPlayers(
                simulator,
                combat,
                move,
                players,
                processedEnemyDeaths);
            performedMoves[enemy] = move.Move;
            if (combat.HasPendingChoice)
                throw PendingEnemyChoice(combat);
            if (simulator.CheckWinCondition(combat.GetPlayerTurnNumber(simulator.State.Players[0])))
                return;
        }
        if (!CorePowerSupport.TriggerEnemySideTurnEndEffects(
                simulator,
                combat,
                combat.Enemies.ToArray()))
        {
            throw PendingEnemyChoice(combat);
        }
        if (!CorePowerSupport.ApplyEnemyDeathPowers(
                simulator, combat, combat.KnownEnemies, processedEnemyDeaths)
            || !CorePowerSupport.TriggerPoison(simulator, combat, players))
        {
            throw PendingEnemyChoice(combat);
        }
        foreach (Creature player in players)
            combat.ClearNoDraw(player);
        combat.PrepareMonsterMovesForNextRound(simulator, performedMoves);
    }

    internal static void CompletePlayerSide(
        CombatPredictionSimulator simulator,
        JointTurnState turns,
        ForkableSet<uint> processedEnemyDeaths,
        IReadOnlyList<PlanCardChoice>? choices = null)
    {
        if (!turns.IsBarrierReached)
            throw new InvalidOperationException("联合玩家侧尚未到达全员结束屏障。");
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        Player[] players = simulator.State.Players
            .Where(player => simulator.State.GetCreature(player.Creature).IsAlive)
            .ToArray();
        Creature[] participants = players.Select(static player => player.Creature).ToArray();
        combat.BeginActionChoices(choices);
        try
        {
            combat.SetActionChoiceTiming(PlanChoiceTiming.PlayerTurnEnd);
            int etherealExhaustCount = players.Sum(player =>
                combat.CountEtherealCardsInHand(simulator, player));
            foreach (Player player in players)
            {
                if (!PlayerTurnEndLifecycle.RunPhaseOne(
                        simulator,
                        combat,
                        player,
                        participants))
                {
                    throw PendingChoice(combat, player, turns);
                }
                combat.CommitHistoryCourseTurn(player);
                combat.NormalizeAeonglassWithers(simulator);
                combat.NormalizeCardAfflictions(simulator);
                if (!CorePowerSupport.ApplyEnemyDeathPowers(
                        simulator,
                        combat,
                        combat.KnownEnemies,
                        processedEnemyDeaths))
                {
                    throw PendingChoice(combat, player, turns);
                }
                if (!simulator.IsInProgress)
                    return;
                CorePowerSupport.FlushPlayerHandAtTurnEnd(simulator, combat, player);
            }
            if (!PlayerTurnEndLifecycle.RunPhaseTwo(
                    simulator,
                    combat,
                    participants,
                    etherealExhaustCount))
            {
                throw PendingChoice(combat, turns);
            }
            if (!CorePowerSupport.ApplyEnemyDeathPowers(
                    simulator,
                    combat,
                    combat.KnownEnemies,
                    processedEnemyDeaths))
            {
                throw PendingChoice(combat, turns);
            }
        }
        finally
        {
            combat.EndActionChoices();
        }
    }

    private static JointPendingActionChoiceException PendingChoice(
        SimulatedCombatState combat,
        Player owner,
        JointTurnState turns)
        => PendingChoice(combat, turns, owner);

    private static JointPendingActionChoiceException PendingChoice(
        SimulatedCombatState combat,
        JointTurnState turns,
        Player? knownOwner = null)
    {
        TurnStartChoiceRequest request = combat.PendingTurnStartChoice
            ?? throw new InvalidOperationException("联合回合尾结算挂起但没有选择请求。");
        Player owner = request.Owner ?? knownOwner
            ?? throw new InvalidOperationException(
                $"联合回合尾选择 {request.SourceId} 没有记录 owner。");
        int actorIndex = -1;
        for (int index = 0; index < combat.Players.Count; index++)
        {
            if (ReferenceEquals(combat.Players[index], owner))
            {
                actorIndex = index;
                break;
            }
        }
        if (actorIndex < 0)
            throw new InvalidOperationException("联合回合尾选择 owner 不在 Actor 目录中。");
        CombatActorId actor = new(actorIndex);
        PlanAction source = new(PlanActionKind.EndTurn, turns.Turn, Actor: actor);
        return new JointPendingActionChoiceException(new(
            actor,
            source,
            request.SourceId,
            request.Spec ?? throw new InvalidOperationException("联合回合尾选择缺少 spec。"),
            JointPendingChoicePlacement.TurnStart,
            request.ContextId,
            request.Timing));
    }

    private static InvalidOperationException PendingEnemyChoice(SimulatedCombatState combat)
        => new($"联合敌方轮产生待处理选择：{combat.PendingTurnStartChoice?.SourceId ?? "unknown"}。");

    private static void RequireNoChoice(bool completed, SimulatedCombatState combat, string stage)
    {
        if (completed && !combat.HasPendingChoice)
            return;
        throw new InvalidOperationException(
            $"联合下一玩家轮在 {stage} 产生待处理选择：" +
            $"{combat.PendingTurnStartChoice?.SourceId ?? "unknown"}。");
    }

    private static void RequireNoChoice(
        bool completed,
        SimulatedCombatState combat,
        string stage,
        CombatActorId actor,
        JointTurnState turns)
    {
        if (completed && !combat.HasPendingChoice)
            return;
        TurnStartChoiceRequest request = combat.PendingTurnStartChoice
            ?? throw new InvalidOperationException(
                $"联合下一玩家轮在 {stage} 挂起但没有选择请求。");
        PlanAction source = new(PlanActionKind.EndTurn, turns.Turn, Actor: actor);
        throw new JointPendingActionChoiceException(new(
            actor,
            source,
            request.SourceId,
            request.Spec ?? throw new InvalidOperationException("联合回合开始选择缺少 spec。"),
            JointPendingChoicePlacement.TurnStart,
            request.ContextId,
            request.Timing));
    }

    private static CombatActorId ActorOf(SimulatedCombatState combat, Player player)
    {
        for (int index = 0; index < combat.Players.Count; index++)
            if (ReferenceEquals(combat.Players[index], player))
                return new CombatActorId(index);
        throw new InvalidOperationException("联合回合开始选择 owner 不在 Actor 目录中。");
    }
}
