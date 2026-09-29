using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal static class JointSearchLifetimeDiagnostics
{
    internal static Action<CombatPredictionSimulator>? TestNodeCreated { get; set; }

    internal static void Observe(CombatPredictionSimulator simulator)
        => TestNodeCreated?.Invoke(simulator);
}
