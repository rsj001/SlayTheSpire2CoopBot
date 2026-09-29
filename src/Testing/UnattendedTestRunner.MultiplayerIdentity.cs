using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models.Powers;
using CombatSolver.Engine.Common;
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

        JointCombatSnapshot parentAgain = JointCombatSnapshot.Capture(root, parent, turns);
        if (parentAgain.StateKey != baseline.StateKey
            || parentAgain.Continuation != baseline.Continuation
            || !ReferenceEquals(knockdown.Applier, first.Creature)
            || !ReferenceEquals(imitation.PlayerTarget, second))
        {
            throw new InvalidOperationException(
                "多人身份兄弟 Fork 修改污染了父状态。 ");
        }
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
