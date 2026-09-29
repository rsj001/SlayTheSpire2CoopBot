using CoopBot.Capture;
using CoopBot.Host;
using CoopBot.Protocol;
using CoopBot.UI;
using CoopBot.NativeAdapter;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Runs;
using CoopBot.Session;

namespace CoopBot.Runtime;

internal sealed class CoopBotRuntime
{
    private HostCombatRecorder? _hostRecorder;
    private HostSearchCoordinator? _hostSearch;
    private HostSearchResult? _lastPlan;
    private LocalActorAgent? _localAgent;
    private HostDeploymentCoordinator? _hostDeployment;
    private CoopPeerController? _peer;

    internal event Action<CoopUiSnapshot>? UiSnapshotChanged;
    internal event Action? UiCleared;

    internal void BeginCombat(CombatState combat)
    {
        StopCombat("combat_replaced");
        INetGameService netService = RunManager.Instance.NetService;
        if (netService.Type == NetGameType.Singleplayer || combat.Players.Count < 2)
            return;
        string combatIdentity = CombatManager.Instance.CurrentCombatId?.ToString()
            ?? throw new InvalidOperationException("Multiplayer combat has no CombatId.");
        _localAgent = new LocalActorAgent();
        ulong hostNetworkPlayerId = netService.Type == NetGameType.Host
            ? netService.NetId
            : netService is NetClientGameService client
                ? client.HostNetId
                : throw new InvalidOperationException(
                    $"Unsupported client net service {netService.GetType().FullName}.");
        _peer = new CoopPeerController(
            combat,
            new NativeCoopTransport(netService),
            combatIdentity,
            hostNetworkPlayerId,
            _localAgent);
        _peer.PlanReceived += OnRemotePlanReceived;
        _peer.SessionFailed += OnSessionFailed;
        if (netService.Type != NetGameType.Host)
            return;
        ActorAssignmentPayload assignment = CoopSession.BuildActorAssignment(
            combat.Players.Select(player => (
                player.NetId,
                player.Character.Id.Entry)).ToArray(),
            hostNetworkPlayerId);
        _peer.StartHost(assignment);
        _hostRecorder = new HostCombatRecorder(
            combat,
            combatIdentity,
            () => _localAgent?.BlocksRootCapture == true);
        _hostSearch = new HostSearchCoordinator(_hostRecorder);
        _hostSearch.PlanPublished += OnPlanPublished;
        _hostDeployment = new HostDeploymentCoordinator(
            combat,
            _hostRecorder,
            _hostSearch,
            _localAgent,
            _peer);
    }

    internal void Poll()
    {
        _hostRecorder?.Poll();
        _hostSearch?.Poll();
        _hostDeployment?.Poll();
    }

    internal void StopCombat(string reason)
    {
        _hostDeployment?.Dispose();
        _hostDeployment = null;
        if (_hostSearch is not null)
            _hostSearch.PlanPublished -= OnPlanPublished;
        _hostSearch?.Dispose();
        _hostSearch = null;
        _lastPlan = null;
        if (_peer is not null)
        {
            _peer.PlanReceived -= OnRemotePlanReceived;
            _peer.SessionFailed -= OnSessionFailed;
            _peer.Dispose();
            _peer = null;
        }
        _localAgent = null;
        _hostRecorder?.Dispose(reason);
        _hostRecorder = null;
        UiCleared?.Invoke();
    }

    internal void ExecuteNext()
        => _hostDeployment?.ExecuteNext();

    internal void CancelSearch()
        => _hostSearch?.Cancel("ui_cancel");

    internal void RefreshUi()
    {
        if (_lastPlan is not null)
            PublishUi(_lastPlan);
    }

    private void OnPlanPublished(HostSearchResult result)
    {
        _lastPlan = result;
        _peer?.PublishPlan(result.Published);
        PublishUi(result);
    }

    private void OnRemotePlanReceived(PlanPublishedPayload plan)
    {
        if (_peer?.Session.State != CoopSessionState.Active)
            return;
        UiSnapshotChanged?.Invoke(CoopUiSnapshot.Capture(
            plan,
            CoopUiRole.Client,
            _peer.Session.LocalActor.ActorId,
            CoopAutomationMode.Suggest,
            "计划就绪",
            currentActionIndex: -1,
            LocManager.Instance.Language));
    }

    private void OnSessionFailed(string detail)
    {
        _hostSearch?.Cancel("session_failed");
        UiCleared?.Invoke();
    }

    private void PublishUi(HostSearchResult result)
        => UiSnapshotChanged?.Invoke(CoopUiSnapshot.Capture(
            result.Published,
            CoopUiRole.Host,
            result.RecordedRoot.Root.LocalActorId.Index,
            CoopAutomationMode.Suggest,
            "计划就绪",
            currentActionIndex: -1,
            LocManager.Instance.Language));
}

internal partial class CoopBotRuntimeNode : Node
{
    private readonly CoopBotRuntime _runtime = new();
    private CoopBotOverlayRenderer? _overlay;

    internal CoopBotRuntime Runtime => _runtime;

    public override void _Ready()
    {
        _overlay = new CoopBotOverlayRenderer { Name = "CoopBotOverlay" };
        AddChild(_overlay);
        _runtime.UiSnapshotChanged += _overlay.Render;
        _runtime.UiCleared += _overlay.HideOverlay;
        _overlay.ExecuteNextRequested += _runtime.ExecuteNext;
        _overlay.CancelSearchRequested += _runtime.CancelSearch;
        LocManager.Instance.SubscribeToLocaleChange(OnLocaleChanged);
    }

    public override void _Process(double delta)
        => _runtime.Poll();

    public override void _ExitTree()
    {
        LocManager.Instance.UnsubscribeToLocaleChange(OnLocaleChanged);
        if (_overlay is not null)
        {
            _runtime.UiSnapshotChanged -= _overlay.Render;
            _runtime.UiCleared -= _overlay.HideOverlay;
            _overlay.ExecuteNextRequested -= _runtime.ExecuteNext;
            _overlay.CancelSearchRequested -= _runtime.CancelSearch;
        }
        _runtime.StopCombat("runtime_exit");
    }

    private void OnLocaleChanged()
        => Callable.From(_runtime.RefreshUi).CallDeferred();
}
