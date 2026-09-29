using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private void AssertMultiplayerNativeDifferentialSubstrate(CombatState source)
    {
        foreach (int actorCount in new[] { 2, 3, 4 })
        {
            OfflineJointCombat native = CreateOfflineJointCombat(source, actorCount);
            CombatRootSnapshot root = CombatRootSnapshot.Capture(native.State);
            string liveBefore = ContinuationStamp.CaptureLive(native.State).StateText;
            CombatPredictionSimulator first = root.ForkSimulator();
            CombatPredictionSimulator second = root.ForkSimulator();
            SimulatedCombatState firstCombat = (SimulatedCombatState)first.State.CombatState;
            SimulatedCombatState secondCombat = (SimulatedCombatState)second.State.CombatState;

            for (int actorIndex = 0; actorIndex < actorCount; actorIndex++)
            {
                Player nativePlayer = native.Players[actorIndex];
                Player predictedPlayer = root.Actors[actorIndex].PlayerIdentity;
                MoveStateSnapshot actual = CaptureActual(native.State, nativePlayer, native.Enemy);
                MoveStateSnapshot predicted = CaptureSimulated(
                    first,
                    firstCombat,
                    predictedPlayer,
                    native.Enemy,
                    root.PlayerIdentity);
                AssertSnapshotEqual(
                    predicted,
                    actual,
                    "MultiplayerNativeRoot",
                    $"ActorCount{actorCount}.Actor{actorIndex}");
            }

            JointTurnState turns = JointTurnState.Start(actorCount, root.StartTurnNumber);
            JointCombatSnapshot firstSnapshot = JointCombatSnapshot.Capture(root, first, turns);
            JointCombatSnapshot secondSnapshot = JointCombatSnapshot.Capture(root, second, turns);
            if (firstSnapshot.StateKey != secondSnapshot.StateKey
                || firstSnapshot.Continuation != secondSnapshot.Continuation)
            {
                throw new InvalidOperationException(
                    $"{actorCount} Actor 原生根建立的两个 prediction Fork 初始状态不同。 ");
            }

            SimPlayerCombatState remote = second.State.GetPlayerCombatState(root.Actors[^1].PlayerIdentity);
            remote.GainEnergy(1);
            JointCombatSnapshot mutated = JointCombatSnapshot.Capture(root, second, turns);
            if (mutated.StateKey == firstSnapshot.StateKey)
                throw new InvalidOperationException($"{actorCount} Actor 远端能量变化没有进入联合状态键。 ");
            if (ContinuationStamp.CaptureLive(native.State).StateText != liveBefore)
                throw new InvalidOperationException($"{actorCount} Actor prediction Fork 修改了原生根。 ");
        }
    }
}
