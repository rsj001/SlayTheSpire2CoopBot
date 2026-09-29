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
using CoopBot.Diagnostics;

namespace CoopBot.Runtime;

internal sealed class CoopBotRuntime
{
    private HostCombatRecorder? _hostRecorder;
    private HostSearchCoordinator? _hostSearch;
    private HostSearchResult? _lastPlan;
    private LocalActorAgent? _localAgent;
    private HostDeploymentCoordinator? _hostDeployment;
    private CoopPeerController? _peer;
    private PlannedChoiceDriver? _choiceDriver;
    private CombatState? _combat;
    private string? _combatIdentity;
    private PlanPublishedPayload? _remotePlan;
    private CoopEvidenceRecorder? _evidence;
    private string _hostStatusKey = "计划就绪";
    private string _hostDetail = "-";
    private int _hostCurrentActionIndex = -1;

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
        _combat = combat;
        _combatIdentity = combatIdentity;
        _evidence = new CoopEvidenceRecorder(
            combatIdentity,
            CoopEvidenceRecorder.DefaultOutputDirectory);
        _choiceDriver = new PlannedChoiceDriver();
        _localAgent = new LocalActorAgent(_choiceDriver);
        ulong hostNetworkPlayerId = netService.Type == NetGameType.Host
            ? netService.NetId
            : netService is NetClientGameService client
                ? client.HostNetId
                : throw new InvalidOperationException(
                    $"Unsupported client net service {netService.GetType().FullName}.");
        string protocolSessionId = netService.Type == NetGameType.Host
            ? $"{combatIdentity}-{Guid.NewGuid():N}"
            : $"pending-{combatIdentity}-{netService.NetId}";
        _peer = new CoopPeerController(
            combat,
            new NativeCoopTransport(netService),
            protocolSessionId,
            hostNetworkPlayerId,
            _localAgent,
            _choiceDriver);
        _peer.PlanReceived += OnRemotePlanReceived;
        _peer.SessionFailed += OnSessionFailed;
        _peer.SessionActivated += OnSessionActivated;
        _peer.ClientStateChanged += OnClientStateChanged;
        _peer.ClientPauseRequested += OnClientPauseRequested;
        _peer.ProtocolEventObserved += _evidence.RecordProtocol;
        _peer.LocalExecutionStarted += _evidence.RecordLocalExecution;
        if (netService.Type != NetGameType.Host)
        {
            _peer.StartClient();
            return;
        }
        ActorAssignmentPayload assignment = CoopSession.BuildActorAssignment(
            combat.Players.Select(player => (
                player.NetId,
                player.Character.Id.Entry)).ToArray(),
            hostNetworkPlayerId);
        _peer.StartHost(assignment);
    }

    private void OnSessionActivated()
    {
        if (_peer is null)
            throw new InvalidOperationException("Activated session has no peer controller.");
        _evidence?.RecordSession(_peer.Session);
        if (_peer is null || !_peer.Session.IsHost)
            return;
        CombatState combat = _combat
            ?? throw new InvalidOperationException("Activated Host session has no combat.");
        string combatIdentity = _combatIdentity
            ?? throw new InvalidOperationException("Activated Host session has no combat identity.");
        LocalActorAgent localAgent = _localAgent
            ?? throw new InvalidOperationException("Activated Host session has no local Agent.");
        PlannedChoiceDriver choiceDriver = _choiceDriver
            ?? throw new InvalidOperationException("Activated Host session has no choice driver.");
        if (_hostRecorder is not null)
            throw new InvalidOperationException("Host runtime was activated twice.");
        _hostRecorder = new HostCombatRecorder(
            combat,
            combatIdentity,
            () => localAgent.BlocksRootCapture
                || _hostDeployment?.BlocksRootCapture == true);
        _hostRecorder.RootRecorded += OnRootRecorded;
        _hostSearch = new HostSearchCoordinator(_hostRecorder);
        _evidence?.RecordPolicy(_hostSearch.Policy);
        _hostSearch.PlanPublished += OnPlanPublished;
        _hostSearch.SearchFailed += OnSearchFailed;
        _hostDeployment = new HostDeploymentCoordinator(
            combat,
            _hostRecorder,
            _hostSearch,
            localAgent,
            _peer,
            choiceDriver);
        _hostDeployment.EventPublished += OnEvidenceDeploymentEvent;
        _peer.PublishAutomationMode(_hostDeployment.Mode);
    }

    internal void Poll()
    {
        _peer?.Poll(System.Environment.TickCount64);
        _hostRecorder?.Poll();
        _hostSearch?.Poll();
        _hostDeployment?.Poll();
    }

    internal void StopCombat(string reason)
    {
        CoopSessionState finalSessionState = _peer?.Session.State ?? CoopSessionState.Stopped;
        string finalAgentState = _localAgent?.State.ToString() ?? "None";
        bool noResidualSessionState = _peer is null;
        if (_hostDeployment is not null)
            _hostDeployment.EventPublished -= OnEvidenceDeploymentEvent;
        _hostDeployment?.Dispose();
        _hostDeployment = null;
        if (_hostSearch is not null)
        {
            _hostSearch.PlanPublished -= OnPlanPublished;
            _hostSearch.SearchFailed -= OnSearchFailed;
        }
        _hostSearch?.Dispose();
        _hostSearch = null;
        _lastPlan = null;
        if (_peer is not null)
        {
            _peer.PlanReceived -= OnRemotePlanReceived;
            _peer.SessionFailed -= OnSessionFailed;
            _peer.SessionActivated -= OnSessionActivated;
            _peer.ClientStateChanged -= OnClientStateChanged;
            _peer.ClientPauseRequested -= OnClientPauseRequested;
            if (_evidence is not null)
            {
                _peer.ProtocolEventObserved -= _evidence.RecordProtocol;
                _peer.LocalExecutionStarted -= _evidence.RecordLocalExecution;
            }
            _peer.Dispose();
            finalSessionState = _peer.Session.State;
            noResidualSessionState = !_peer.HasResidualSessionState;
            _peer = null;
        }
        _choiceDriver?.Dispose();
        noResidualSessionState = noResidualSessionState
            && _choiceDriver?.IsArmed != true
            && _localAgent?.State is null or LocalActorAgentState.Idle;
        finalAgentState = _localAgent?.State.ToString() ?? finalAgentState;
        _localAgent = null;
        _choiceDriver = null;
        if (_hostRecorder is not null)
            _hostRecorder.RootRecorded -= OnRootRecorded;
        _hostRecorder?.Dispose(reason);
        _hostRecorder = null;
        _combat = null;
        _combatIdentity = null;
        _remotePlan = null;
        _hostStatusKey = "计划就绪";
        _hostDetail = "-";
        _hostCurrentActionIndex = -1;
        if (_evidence is not null)
        {
            _evidence.Complete(
                reason,
                finalSessionState,
                finalAgentState,
                noResidualSessionState);
            _evidence = null;
        }
        UiCleared?.Invoke();
    }

    internal void ExecuteNext()
        => _hostDeployment?.ExecuteNext();

    internal void CancelSearch()
        => _hostSearch?.Cancel("ui_cancel");

    internal void Replan()
        => _hostSearch?.RestartCurrent();

    internal void PauseAutomation()
    {
        if (_peer?.Session is { IsHost: false, State: CoopSessionState.Active })
            _peer.PauseClientAutomation();
        else
            SetAutomationMode(CoopAutomationMode.ConfirmEach);
    }

    internal void AllowOnce()
    {
        if (_peer?.PendingClientAction is not null)
            _peer.AllowPendingClientAction();
    }

    internal void RejectPending()
    {
        if (_peer?.PendingClientAction is not null)
            _peer.RejectPendingClientAction();
    }

    internal void SetAutomationMode(CoopAutomationMode mode)
    {
        _hostDeployment?.SetMode(mode);
        if (_hostDeployment is null
            && _peer?.Session is { IsHost: true, State: CoopSessionState.Active })
            _peer.PublishAutomationMode(mode);
        RefreshUi();
    }

    internal void RefreshUi()
    {
        if (_lastPlan is not null)
            PublishUi(_lastPlan);
    }

    private void OnPlanPublished(HostSearchResult result)
    {
        _lastPlan = result;
        _hostStatusKey = "计划就绪";
        _hostDetail = $"expanded={result.Published.ExpandedStates};{result.Published.Termination}";
        _hostCurrentActionIndex = -1;
        _evidence?.RecordPlan(result.Published);
        _peer?.PublishPlan(result.Published);
        PublishUi(result);
    }

    private void OnSearchFailed(HostSearchFailure failure)
    {
        _hostStatusKey = "执行失败";
        _hostDetail = $"search_failed:{failure.Error.GetType().Name}:{failure.Error.Message}";
        _hostCurrentActionIndex = -1;
        SetAutomationMode(CoopAutomationMode.ConfirmEach);
        if (_lastPlan is not null)
            PublishUi(_lastPlan);
    }

    private void OnRemotePlanReceived(PlanPublishedPayload plan)
    {
        if (_peer?.Session.State != CoopSessionState.Active)
            return;
        _remotePlan = plan;
        _evidence?.RecordPlan(plan);
        PublishClientUi();
    }

    private void OnClientStateChanged()
        => PublishClientUi();

    private void OnClientPauseRequested(ulong senderNetworkPlayerId)
    {
        if (_peer?.Session is not { IsHost: true, State: CoopSessionState.Active })
            return;
        if (!_peer.Session.Actors.Any(actor => actor.NetworkPlayerId == senderNetworkPlayerId))
            throw new InvalidOperationException($"Pause requester {senderNetworkPlayerId} is not an Actor.");
        SetAutomationMode(CoopAutomationMode.ConfirmEach);
    }

    private void PublishClientUi()
    {
        if (_peer?.Session is not { IsHost: false, State: CoopSessionState.Active }
            || _remotePlan is null)
            return;
        CoopPlanActionSnapshot? pending = _peer.PendingClientAction;
        string statusKey = _peer.ClientActivity switch
        {
            CoopClientActivity.Idle or CoopClientActivity.PlanReady => "计划就绪",
            CoopClientActivity.AwaitingConsent => "等待本地确认",
            CoopClientActivity.Prepared => "已准备",
            CoopClientActivity.Executing => "执行中",
            CoopClientActivity.Completed => "执行完成",
            CoopClientActivity.Rejected => "已拒绝",
            CoopClientActivity.Failed => "执行失败",
            CoopClientActivity.Cancelled => "计划已取消",
            _ => throw new ArgumentOutOfRangeException(),
        };
        int hostActorId = _peer.Session.Actors.Single(actor => actor.IsHost).ActorId;
        UiSnapshotChanged?.Invoke(CoopUiSnapshot.Capture(
            _remotePlan,
            CoopUiRole.Client,
            _peer.Session.LocalActor.ActorId,
            _peer.AutomationMode,
            statusKey,
            currentActionIndex: pending?.Index ?? -1,
            LocManager.Instance.Language,
            _peer.Session.Actors,
            hostActorId,
            "已连接",
            _peer.ClientActivityDetail));
    }

    private void OnSessionFailed(string detail)
    {
        _hostSearch?.Cancel("session_failed");
        StopCombat("session_failed:" + detail);
    }

    private void PublishUi(HostSearchResult result)
    {
        int hostActorId = _peer?.Session.Actors.Single(actor => actor.IsHost).ActorId
            ?? result.RecordedRoot.Root.LocalActorId.Index;
        UiSnapshotChanged?.Invoke(CoopUiSnapshot.Capture(
            result.Published,
            CoopUiRole.Host,
            result.RecordedRoot.Root.LocalActorId.Index,
            _hostDeployment?.Mode ?? CoopAutomationMode.ConfirmEach,
            _hostStatusKey,
            _hostCurrentActionIndex,
            LocManager.Instance.Language,
            _peer?.Session.Actors,
            hostActorId,
            "已连接",
            _hostDetail));
    }

    private void OnRootRecorded(RecordedCombatRoot root)
    {
        _evidence?.RecordRoot(root);
        _peer?.PublishRoot(new RootPublishedPayload(
            root.RootRevision,
            root.Fingerprint,
            root.ActorFingerprints));
    }

    private void OnEvidenceDeploymentEvent(HostDeploymentEvent observed)
    {
        _evidence?.RecordDeployment(observed);
        _hostDetail = observed.Kind + ":" + observed.Detail;
        _hostCurrentActionIndex = observed.ActionId == "-" ? -1 : 0;
        _hostStatusKey = observed.Kind switch
        {
            "prepare_sent" or "prepared" => "等待动作",
            "commit_sent" or "ack" => "执行中",
            "verified" => "执行完成",
            "rejected" => "已拒绝",
            "failed" or "timeout" or "diverged" => "执行失败",
            "manual_insertion" => "计划失效",
            "auto_paused" => "已暂停",
            _ => _hostStatusKey,
        };
        if (_lastPlan is not null)
            PublishUi(_lastPlan);
    }
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
        _overlay.ReplanRequested += _runtime.Replan;
        _overlay.PauseRequested += _runtime.PauseAutomation;
        _overlay.AutomationModeRequested += _runtime.SetAutomationMode;
        _overlay.AllowOnceRequested += _runtime.AllowOnce;
        _overlay.RejectRequested += _runtime.RejectPending;
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
            _overlay.ReplanRequested -= _runtime.Replan;
            _overlay.PauseRequested -= _runtime.PauseAutomation;
            _overlay.AutomationModeRequested -= _runtime.SetAutomationMode;
            _overlay.AllowOnceRequested -= _runtime.AllowOnce;
            _overlay.RejectRequested -= _runtime.RejectPending;
        }
        _runtime.StopCombat("runtime_exit");
    }

    private void OnLocaleChanged()
        => Callable.From(_runtime.RefreshUi).CallDeferred();
}
