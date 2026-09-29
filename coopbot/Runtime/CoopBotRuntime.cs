using CoopBot.Capture;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;

namespace CoopBot.Runtime;

internal sealed class CoopBotRuntime
{
    private HostCombatRecorder? _hostRecorder;

    internal void BeginCombat(CombatState combat)
    {
        StopCombat("combat_replaced");
        if (RunManager.Instance.NetService.Type != NetGameType.Host || combat.Players.Count < 2)
            return;
        string combatIdentity = CombatManager.Instance.CurrentCombatId?.ToString()
            ?? throw new InvalidOperationException("Multiplayer combat has no CombatId.");
        _hostRecorder = new HostCombatRecorder(combat, combatIdentity, () => false);
    }

    internal void Poll()
        => _hostRecorder?.Poll();

    internal void StopCombat(string reason)
    {
        _hostRecorder?.Dispose(reason);
        _hostRecorder = null;
    }
}

internal partial class CoopBotRuntimeNode : Node
{
    private readonly CoopBotRuntime _runtime = new();

    internal CoopBotRuntime Runtime => _runtime;

    public override void _Process(double delta)
        => _runtime.Poll();

    public override void _ExitTree()
        => _runtime.StopCombat("runtime_exit");
}
