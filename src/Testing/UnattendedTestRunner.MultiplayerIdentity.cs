using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Block;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private static void AssertMultiplayerIdentityContracts(CombatState source)
    {
        CombatActorId sourceActor = new(0);
        CombatActorId decisionActor = new(1);
        PlanCardChoice choice = new(
            PlanChoiceEffect.MoveToHand,
            PileType.Draw,
            [],
            Actor: decisionActor);
        PlanAction action = new(
            PlanActionKind.PlayCard,
            Turn: 1,
            CardId: "IDENTITY_SENTINEL",
            Choice: choice,
            Actor: sourceActor);
        JointPendingChoiceFrame frame = new(decisionActor, action, "IDENTITY_SENTINEL",
            new CardChoiceSpec(PlanChoiceEffect.MoveToHand, PileType.Draw, 0, 0, [], [], 0));
        if (action.SourceActor != sourceActor
            || choice.DecisionActor != decisionActor
            || frame.SourceActor != sourceActor
            || frame.DecisionActor != decisionActor
            || action == action with { Actor = decisionActor }
            || choice == choice with { Actor = sourceActor })
        {
            throw new InvalidOperationException(
                "SourceActor 与 DecisionActor 没有保持独立计划身份。 ");
        }

        OfflineJointCombat native = CreateOfflineJointCombat(source, actorCount: 2);
        CombatRootSnapshot root = CombatRootSnapshot.Capture(native.State);
        CombatPredictionSimulator parent = root.ForkSimulator();
        SimulatedCombatState parentCombat = (SimulatedCombatState)parent.State.CombatState;
        Player first = parent.State.Players[0];
        Player second = parent.State.Players[1];
        KnockdownPower knockdown = parentCombat.AddPowerInstance<KnockdownPower>(
            native.Enemy,
            2,
            first.Creature);
        ImitationLearningPower imitation = parentCombat.AddPowerInstance<ImitationLearningPower>(
            first.Creature,
            1,
            first.Creature);
        imitation.PlayerTarget = second;
        InterceptPower intercept = parentCombat.AddPowerInstance<InterceptPower>(
            first.Creature,
            1,
            first.Creature);
        InterceptPredictionState interceptState = parent.StateStore.Get(
            intercept,
            () => new InterceptPredictionState(intercept));
        interceptState.CoveredCreatures.Add(second.Creature);
        CacophonyPower cacophony = parentCombat.AddPowerInstance<CacophonyPower>(
            first.Creature,
            3,
            first.Creature);
        JointTurnState turns = JointTurnState.Start(2, root.StartTurnNumber);
        JointCombatSnapshot baseline = JointCombatSnapshot.Capture(root, parent, turns);

        CombatPredictionSimulator applierFork = parent.Fork();
        SimulatedCombatState applierCombat = (SimulatedCombatState)applierFork.State.CombatState;
        KnockdownPower applierPower = applierCombat.EffectivePowers().OfType<KnockdownPower>().Single();
        applierPower._applier = applierFork.State.Players[1].Creature;
        AssertIdentityChanged(
            baseline,
            JointCombatSnapshot.Capture(root, applierFork, turns),
            "Applier");

        CombatPredictionSimulator targetFork = parent.Fork();
        SimulatedCombatState targetCombat = (SimulatedCombatState)targetFork.State.CombatState;
        KnockdownPower targetPower = targetCombat.EffectivePowers().OfType<KnockdownPower>().Single();
        targetPower._target = targetFork.State.Players[0].Creature;
        AssertIdentityChanged(
            baseline,
            JointCombatSnapshot.Capture(root, targetFork, turns),
            "Target");

        CombatPredictionSimulator playerTargetFork = parent.Fork();
        SimulatedCombatState playerTargetCombat =
            (SimulatedCombatState)playerTargetFork.State.CombatState;
        ImitationLearningPower playerTargetPower = playerTargetCombat.EffectivePowers()
            .OfType<ImitationLearningPower>().Single();
        playerTargetPower.PlayerTarget = playerTargetFork.State.Players[0];
        AssertIdentityChanged(
            baseline,
            JointCombatSnapshot.Capture(root, playerTargetFork, turns),
            "ImitationLearning.PlayerTarget");

        CombatPredictionSimulator cardOwnerFork = parent.Fork();
        Player oldOwner = cardOwnerFork.State.Players[0];
        Player newOwner = cardOwnerFork.State.Players[1];
        PredictedCard transferred = cardOwnerFork.State.GetPlayerCombatState(oldOwner).Hand.Cards[0];
        cardOwnerFork.GiveToAnotherPlayer(
            transferred,
            oldOwner,
            newOwner,
            PileType.Draw,
            CardPilePosition.Bottom);
        AssertIdentityChanged(
            baseline,
            JointCombatSnapshot.Capture(root, cardOwnerFork, turns),
            "CardOwner");

        CombatPredictionSimulator coveredFork = parent.Fork();
        SimulatedCombatState coveredCombat = (SimulatedCombatState)coveredFork.State.CombatState;
        InterceptPower coveredPower = coveredCombat.EffectivePowers().OfType<InterceptPower>().Single();
        InterceptPredictionState coveredState = coveredFork.StateStore.Get(
            coveredPower,
            () => new InterceptPredictionState(coveredPower));
        coveredState.CoveredCreatures.Add(coveredFork.State.Players[0].Creature);
        AssertIdentityChanged(
            baseline,
            JointCombatSnapshot.Capture(root, coveredFork, turns),
            "Intercept.Covering");

        CombatPredictionSimulator cacophonyFork = parent.Fork();
        SimulatedCombatState cacophonyCombat = (SimulatedCombatState)cacophonyFork.State.CombatState;
        CacophonyPower cacophonyPower = cacophonyCombat.EffectivePowers().OfType<CacophonyPower>().Single();
        cacophonyPower.DynamicVars.Cards.BaseValue--;
        AssertIdentityChanged(
            baseline,
            JointCombatSnapshot.Capture(root, cacophonyFork, turns),
            "Cacophony.CardsDrawn");

        AssertTransientPowerForkBoundaries(root);
        AssertTutorDecisionActor(source);

        JointCombatSnapshot parentAgain = JointCombatSnapshot.Capture(root, parent, turns);
        if (parentAgain.StateKey != baseline.StateKey
            || parentAgain.Continuation != baseline.Continuation
            || !ReferenceEquals(knockdown.Applier, first.Creature)
            || !ReferenceEquals(imitation.PlayerTarget, second)
            || interceptState.CoveredCreatures.Count != 1
            || !ReferenceEquals(interceptState.CoveredCreatures[0], second.Creature)
            || cacophony.DynamicVars.Cards.IntValue != 33)
        {
            throw new InvalidOperationException(
                "多人身份兄弟 Fork 修改污染了父状态。 ");
        }
    }

    private static void AssertTutorDecisionActor(CombatState source)
    {
        OfflineJointCombat native = CreateOfflineJointCombat(source, actorCount: 2);
        Player sourcePlayer = native.Players[0];
        Player decisionPlayer = native.Players[1];
        sourcePlayer.PlayerCombatState!.Energy = 99;
        CardModel tutor = native.State.CreateCard(ModelDb.Card<Tutor>(), sourcePlayer);
        sourcePlayer.PlayerCombatState.Hand.AddInternal(tutor, silent: true);
        CombatRootSnapshot root = CombatRootSnapshot.Capture(native.State);
        PlanAction action = new(
            PlanActionKind.PlayCard,
            root.StartTurnNumber,
            CardId: tutor.Id.Entry,
            TargetCombatId: decisionPlayer.Creature.CombatId,
            Actor: new CombatActorId(0));
        CombatPredictionSimulator probe = root.ForkSimulator();
        JointPendingChoiceFrame frame;
        try
        {
            _ = JointActionTransition.Apply(
                probe,
                JointTurnState.Start(2, root.StartTurnNumber),
                action,
                JointActionTransition.CaptureProcessedEnemyDeaths(root, probe));
            throw new InvalidOperationException("Tutor did not suspend for its target player's choice.");
        }
        catch (JointPendingActionChoiceException pending)
        {
            frame = pending.Frame;
        }
        if (frame.SourceActor != new CombatActorId(0)
            || frame.DecisionActor != new CombatActorId(1)
            || frame.Spec.SourcePile != PileType.Draw
            || frame.Spec.Options.Count == 0)
        {
            throw new InvalidOperationException("Tutor did not preserve SourceActor/DecisionActor and target draw options.");
        }

        PlanCardChoice choice = CardChoiceSupport.BuildAutomaticPolicyChoice(frame.Spec) with
        {
            Actor = frame.DecisionActor,
            SourceId = frame.SourceId,
            ContextId = frame.ContextId,
        };
        CombatPredictionSimulator resolved = root.ForkSimulator();
        int sourceHandBefore = resolved.State.GetPlayerCombatState(sourcePlayer).Hand.Cards.Count;
        int targetHandBefore = resolved.State.GetPlayerCombatState(decisionPlayer).Hand.Cards.Count;
        _ = JointActionTransition.Apply(
            resolved,
            JointTurnState.Start(2, root.StartTurnNumber),
            action with { Choice = choice },
            JointActionTransition.CaptureProcessedEnemyDeaths(root, resolved));
        if (resolved.State.GetPlayerCombatState(sourcePlayer).Hand.Cards.Count != sourceHandBefore - 1
            || resolved.State.GetPlayerCombatState(decisionPlayer).Hand.Cards.Count != targetHandBefore + 1)
        {
            throw new InvalidOperationException("Tutor moved the selected card to the source actor instead of DecisionActor.");
        }
    }

    private static void AssertTransientPowerForkBoundaries(CombatRootSnapshot root)
    {
        CombatPredictionSimulator beaconSimulator = root.ForkSimulator();
        SimulatedCombatState beaconCombat = (SimulatedCombatState)beaconSimulator.State.CombatState;
        Player beaconOwner = beaconSimulator.State.Players[0];
        BeaconOfHopePower beacon = beaconCombat.AddPowerInstance<BeaconOfHopePower>(
            beaconOwner.Creature,
            1,
            beaconOwner.Creature);
        beaconSimulator.StateStore.Get(beacon, static () => new BeaconOfHopePredictionState())
            .HasAlreadyBeenGivenBlock = true;
        AssertTransientForkRejected(beaconSimulator, "BeaconOfHope.distributing");

        CombatPredictionSimulator soulSimulator = root.ForkSimulator();
        SimulatedCombatState soulCombat = (SimulatedCombatState)soulSimulator.State.CombatState;
        Player soulOwner = soulSimulator.State.Players[0];
        SoulboundPower soulbound = soulCombat.AddPowerInstance<SoulboundPower>(
            soulOwner.Creature,
            1,
            soulOwner.Creature);
        soulSimulator.StateStore.Get(soulbound, () => new SoulboundPredictionState(soulbound))
            .IsAddingSoul = true;
        AssertTransientForkRejected(soulSimulator, "Soulbound.addingSoul");
    }

    private static void AssertTransientForkRejected(CombatPredictionSimulator simulator, string field)
    {
        try
        {
            _ = simulator.Fork();
        }
        catch (InvalidOperationException error) when (
            error.Message.Contains("Cannot fork", StringComparison.Ordinal))
        {
            return;
        }
        throw new InvalidOperationException($"事务状态 {field} 没有拒绝 Fork。 ");
    }

    private static void AssertIdentityChanged(
        JointCombatSnapshot baseline,
        JointCombatSnapshot changed,
        string field)
    {
        if (changed.StateKey == baseline.StateKey || changed.Continuation == baseline.Continuation)
            throw new InvalidOperationException($"多人身份 {field} 未进入状态键或 ContinuationStamp。 ");
    }
}
