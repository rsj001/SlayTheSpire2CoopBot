using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes;
using STS2RitsuLib;
using STS2RitsuLib.Interop;
using CoopBot.Runtime;

namespace CoopBot;

[ModInitializer(nameof(Initialize))]
public static class Entry
{
    public const string ModId = "CoopBot";
    private static CoopBotRuntimeNode? _runtimeNode;

    public static void Initialize()
    {
        NGame host = NGame.Instance
            ?? throw new InvalidOperationException("CoopBot requires an initialized NGame host.");
        _runtimeNode = new CoopBotRuntimeNode { Name = "CoopBotRuntime" };
        host.AddChild(_runtimeNode);
        RitsuLibFramework.SubscribeLifecycle<CombatStartingEvent>(
            evt => _runtimeNode.Runtime.BeginCombat(
                evt.CombatState as CombatState
                ?? throw new InvalidOperationException(
                    $"CoopBot requires native CombatState, got " +
                    $"{evt.CombatState?.GetType().FullName ?? "<null>"}.")));
        RitsuLibFramework.SubscribeLifecycle<CombatEndedEvent>(
            _ => _runtimeNode.Runtime.StopCombat("combat_ended"));
    }
}
