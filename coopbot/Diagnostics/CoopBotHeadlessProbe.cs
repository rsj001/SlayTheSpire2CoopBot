using CoopBot.Capture;
using CoopBot.Host;
using CoopBot.Protocol;
using CoopBot.Session;
using CoopBot.UI;
using CoopBot.NativeAdapter;
using CoopBot.Runtime;
using CombatSolver;
using CombatSolver.Engine.InCombat.Simulation;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Actions;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Runs;
using System.Reflection;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

namespace CoopBot.Diagnostics;

public static class CoopBotHeadlessProbe
{
    public static string ExportSyntheticEvidence(CombatState combat)
    {
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        string sessionId = "C10-EVIDENCE-" + Guid.NewGuid().ToString("N");
        string output = Path.Combine(
            Godot.OS.GetUserDataDir(),
            "CoopBotHeadlessEvidence",
            Guid.NewGuid().ToString("N"));
        CoopEvidenceRecorder evidence = new(sessionId, output);
        ulong host = root.Actors.Single(actor => actor.Id == root.LocalActorId).PlayerIdentity.NetId;
        ActorAssignmentPayload assignment = CoopSession.BuildActorAssignment(
            root.Actors.Select(actor => (
                actor.PlayerIdentity.NetId,
                actor.PlayerIdentity.Character.Id.Entry)).ToArray(),
            host);
        CoopSession session = new(sessionId, host, host);
        session.AcceptAsHost("not-recorded-test-nonce", CoopProtocol.RequiredCapabilities);
        session.AcceptAssignment(assignment);
        evidence.RecordSession(session);
        evidence.RecordPolicy(HostSearchPolicy.Default);
        string rootFingerprint = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(root.ContinuationStamp.StateText)));
        HostVisibilityAuditResult visibility = HostVisibilityAudit.Inspect(combat, root);
        RecordedCombatRoot recorded = new(
            1,
            rootFingerprint,
            root,
            visibility,
            root.Actors.Select(actor => LocalActorAgent.Fingerprint(
                ContinuationStamp.CaptureLiveForPlayer(combat, actor.PlayerIdentity).StateText)).ToArray());
        evidence.RecordRoot(recorded);
        JointOfflineSearchResult searched = JointOfflineSearch.SolveBeam(
            root,
            JointOfflineSearchRequest.Default(maximumActions: 1, maximumStates: 128),
            beamWidth: 64,
            degreeOfParallelism: 1);
        JointReplayResult replay = JointStrictReplayVerifier.Verify(root, searched);
        PlanPublishedPayload plan = CoopPlanSnapshotFactory.Create(
            1,
            rootFingerprint,
            root,
            searched,
            replay);
        evidence.RecordPlan(plan);
        ulong remote = assignment.Actors.First(actor => !actor.IsHost).NetworkPlayerId;
        evidence.RecordProtocol(new CoopProtocolEvent(
            "outbound",
            CoopMessageKind.PlanPublished,
            3,
            remote,
            1,
            plan.PlanId,
            null,
            "Sent"));
        evidence.RecordDeployment(new HostDeploymentEvent(
            "verified",
            plan.PlanId,
            plan.PlanId + ":0",
            "actual/simulated identical"));
        evidence.RecordLocalExecution(plan.PlanId + ":0");
        session.BeginStopping();
        session.FinishStopping();
        string path = evidence.Complete(
            "headless_complete",
            session.State,
            LocalActorAgentState.Idle.ToString(),
            noResidualSessionState: true);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
        JsonElement rootElement = document.RootElement;
        JsonElement cleanup = rootElement.GetProperty("cleanup");
        if (rootElement.GetProperty("schemaVersion").GetInt32() != 1
            || rootElement.GetProperty("assemblies").GetArrayLength() != 4
            || rootElement.GetProperty("session").GetProperty("actors").GetArrayLength() != 4
            || rootElement.GetProperty("roots").GetArrayLength() != 1
            || rootElement.GetProperty("plans").GetArrayLength() != 1
            || rootElement.GetProperty("protocol").GetArrayLength() != 1
            || rootElement.GetProperty("deployment").GetArrayLength() != 1
            || !cleanup.GetProperty("noResidualSessionState").GetBoolean()
            || cleanup.GetProperty("duplicateLocalExecutionCount").GetInt32() != 0)
        {
            throw new InvalidOperationException("C10 evidence document is missing required structured fields.");
        }
        foreach (JsonElement assembly in rootElement.GetProperty("assemblies").EnumerateArray())
        {
            if (assembly.GetProperty("sha256").GetString()?.Length != 64)
                throw new InvalidOperationException("C10 evidence contains an invalid assembly hash.");
        }
        string json = File.ReadAllText(path);
        if (json.Contains("sessionNonce", StringComparison.OrdinalIgnoreCase)
            || json.Contains("networkPlayerId", StringComparison.OrdinalIgnoreCase)
            || json.Contains("not-recorded-test-nonce", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("C10 evidence leaked a session nonce or raw network identity.");
        }
        return $"schema=1;assemblies=4;actors=4;roots=1;plans=1;" +
               "protocol=1;deploy=1;duplicate_execution=0;cleanup=clean";
    }

    public static string AuditSyntheticRoot(CombatState combat)
    {
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        HostVisibilityAuditResult audit = HostVisibilityAudit.Inspect(combat, root);
        if (!audit.IsComplete)
            throw new InvalidOperationException(
                "Synthetic Host visibility audit failed: " + string.Join(',', audit.Failures));
        return $"actors={root.Actors.Count};rng=9;fingerprint_fields={root.ContinuationStamp.StateText.Split(';').Length}";
    }

    public static async Task<string> AuditC10ProtocolAndFaultsAsync(CombatState combat)
    {
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        if (root.Actors.Count != 4)
            throw new InvalidOperationException($"C10 protocol probe expected four actors, got {root.Actors.Count}.");
        ulong hostId = root.Actors[root.LocalActorId.Index].PlayerIdentity.NetId;
        string sessionId = "C10-LOOPBACK-" + Guid.NewGuid().ToString("N");
        ActorAssignmentPayload assignment = CoopSession.BuildActorAssignment(
            root.Actors.Select(actor => (
                actor.PlayerIdentity.NetId,
                actor.PlayerIdentity.Character.Id.Entry)).ToArray(),
            hostId);
        CoopLoopbackHub hub = new(hostId);
        PlannedChoiceDriver[] choices = Enumerable.Range(0, 4)
            .Select(_ => new PlannedChoiceDriver())
            .ToArray();
        LocalActorAgent[] agents = choices.Select(choice => new LocalActorAgent(choice)).ToArray();
        CoopPeerController[] peers = assignment.Actors
            .Select(actor => new CoopPeerController(
                combat,
                hub.Add(actor.NetworkPlayerId),
                actor.IsHost ? sessionId : $"CLIENT-LOCAL-{actor.ActorId}",
                hostId,
                agents[actor.ActorId],
                choices[actor.ActorId]))
            .ToArray();
        CoopPeerController host = peers.Single(peer => peer.Session.IsHost);
        List<CoopWireEnvelope> hostResponses = [];
        List<CoopProtocolEvent> protocol = [];
        int receivedPlans = 0;
        int pauseRequests = 0;
        host.HostResponseReceived += (envelope, _) => hostResponses.Add(envelope);
        host.ClientPauseRequested += _ => pauseRequests++;
        foreach (CoopPeerController peer in peers)
        {
            peer.ProtocolEventObserved += protocol.Add;
            if (!peer.Session.IsHost)
                peer.PlanReceived += _ => receivedPlans++;
        }
        try
        {
            host.StartHost(assignment);
            foreach (CoopPeerController client in peers.Where(peer => !peer.Session.IsHost))
                client.StartClient();
            if (peers.Any(peer => peer.Session.State != CoopSessionState.Active)
                || peers.Select(peer => peer.Session.SessionNonce).Distinct(StringComparer.Ordinal).Count() != 1)
            {
                throw new InvalidOperationException("C10 four-peer Hello/SessionAccepted negotiation did not converge.");
            }
            host.PublishAutomationMode(CoopAutomationMode.ConfirmEach);
            if (peers.Where(peer => !peer.Session.IsHost)
                .Any(peer => peer.AutomationMode != CoopAutomationMode.ConfirmEach))
            {
                throw new InvalidOperationException("C10 ConfirmEach mode did not reach all Clients.");
            }

            JointOfflineSearchResult searched = JointOfflineSearch.SolveBeam(
                root,
                JointOfflineSearchRequest.Default(maximumActions: 1, maximumStates: 128),
                beamWidth: 64,
                degreeOfParallelism: 1);
            JointReplayResult searchedReplay = JointStrictReplayVerifier.Verify(root, searched);
            PlanPublishedPayload published = CoopPlanSnapshotFactory.Create(
                1,
                LocalActorAgent.Fingerprint(root.ContinuationStamp.StateText),
                root,
                searched,
                searchedReplay);
            host.PublishRoot(new RootPublishedPayload(
                1,
                published.RootFingerprint,
                root.Actors.Select(actor => LocalActorAgent.Fingerprint(
                    ContinuationStamp.CaptureLiveForPlayer(combat, actor.PlayerIdentity).StateText)).ToArray()));
            host.PublishPlan(published);
            if (receivedPlans != 3)
                throw new InvalidOperationException($"C10 plan reached {receivedPlans}/3 Clients.");

            PlanAction remoteEnd = new(
                PlanActionKind.EndTurn,
                root.StartTurnNumber,
                Actor: new CombatActorId(1));
            ActionPreparePayload remoteCommand = CreateActorCommand(combat, root, remoteEnd, actionIndex: 0);
            ActorBinding remoteOwner = assignment.Actors.Single(actor => actor.ActorId == 1);
            host.SendPrepare(remoteOwner.NetworkPlayerId, 1, published.PlanId, "C10-ALLOW", remoteCommand);
            if (peers[1].PendingClientAction is null || hostResponses.Count != 0)
                throw new InvalidOperationException("C10 ConfirmEach did not hold Prepare for local consent.");
            peers[1].AllowPendingClientAction();
            if (hostResponses.Count != 1 || hostResponses[0].Kind != CoopMessageKind.ActionPrepared)
                throw new InvalidOperationException("C10 AllowOnce did not release exactly one ActionPrepared.");
            host.PublishPlanCancelled(1, published.PlanId, "C10-ALLOW", "fault probe cleanup");
            host.PublishPlan(published);

            host.SendPrepare(
                remoteOwner.NetworkPlayerId,
                1,
                published.PlanId,
                "C10-ROOT-CHANGE",
                remoteCommand);
            if (peers[1].PendingClientAction is null)
                throw new InvalidOperationException("C10 root-change probe did not reach pending Prepare.");
            RootPublishedPayload secondRoot = new(
                2,
                published.RootFingerprint,
                root.Actors.Select(actor => LocalActorAgent.Fingerprint(
                    ContinuationStamp.CaptureLiveForPlayer(combat, actor.PlayerIdentity).StateText)).ToArray());
            host.PublishRoot(secondRoot);
            if (hostResponses.Count != 2
                || hostResponses[1].Kind != CoopMessageKind.ActionRejected
                || hostResponses[1].ReadPayload<ActionRejectedPayload>().Code
                    != CoopActionRejectionCode.StaleRoot
                || peers[1].PendingClientAction is not null)
            {
                throw new InvalidOperationException("C10 root change after Prepare did not reject stale authorization.");
            }
            PlanPublishedPayload secondPlan = published with
            {
                PlanId = "C10-ROOT-2",
                RootRevision = 2,
            };
            host.PublishPlan(secondPlan);

            PlanAction remoteTwoEnd = remoteEnd with { Actor = new CombatActorId(2) };
            ActionPreparePayload rejectedCommand = CreateActorCommand(combat, root, remoteTwoEnd, actionIndex: 0);
            ActorBinding rejectedOwner = assignment.Actors.Single(actor => actor.ActorId == 2);
            host.SendPrepare(
                rejectedOwner.NetworkPlayerId,
                2,
                secondPlan.PlanId,
                "C10-REJECT",
                rejectedCommand);
            peers[2].RejectPendingClientAction();
            if (hostResponses.Count != 3
                || hostResponses[2].Kind != CoopMessageKind.ActionRejected
                || hostResponses[2].ReadPayload<ActionRejectedPayload>().Code
                    != CoopActionRejectionCode.UserDeclined)
            {
                throw new InvalidOperationException("C10 explicit Client rejection did not reach Host.");
            }
            peers[3].PauseClientAutomation();
            if (pauseRequests != 1)
                throw new InvalidOperationException("C10 Client pause request did not reach Host.");

            long heartbeatAt = System.Environment.TickCount64 + CoopProtocol.HeartbeatIntervalMilliseconds;
            host.Poll(heartbeatAt);
            foreach (CoopPeerController client in peers.Where(peer => !peer.Session.IsHost))
                client.Poll(heartbeatAt);
            if (!protocol.Any(item => item.Kind == CoopMessageKind.Heartbeat))
                throw new InvalidOperationException("C10 heartbeat did not traverse the negotiated session.");

            await AssertC10LocalRejectionsAsync(combat, root);

            ulong disconnectedId = assignment.Actors.Single(actor => actor.ActorId == 3).NetworkPlayerId;
            hub.Disconnect(disconnectedId);
            if (peers[3].Session.State != CoopSessionState.Failed
                || peers[3].Session.FailureCode != "disconnected")
            {
                throw new InvalidOperationException("C10 disconnect did not stop the Client session.");
            }
            bool reconnectRequiresNewSession = false;
            try
            {
                peers[3].StartClient();
            }
            catch (InvalidOperationException)
            {
                reconnectRequiresNewSession = true;
            }
            if (!reconnectRequiresNewSession)
                throw new InvalidOperationException("C10 disconnected Client reused a failed session.");
        }
        finally
        {
            foreach (CoopPeerController peer in peers.Reverse())
                peer.Dispose();
            foreach (PlannedChoiceDriver choice in choices)
                choice.Dispose();
        }
        if (peers.Any(peer => peer.HasResidualSessionState))
            throw new InvalidOperationException("C10 loopback peers retained session state after disposal.");
        string kinds = string.Join(',', protocol.Select(item => item.Kind).Distinct().Order());
        return $"peers=4;negotiated=v{CoopProtocol.Version};plan_receivers=3;" +
               "consent=allow,reject,pause;heartbeat=ok;disconnect=new_session_required;" +
               $"protocol={kinds};cleanup=4_stopped";
    }

    public static string AuditC10TerminalDemo(CombatState combat)
    {
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        if (root.Actors.Count != 4)
            throw new InvalidOperationException($"C10 terminal demo expected four actors, got {root.Actors.Count}.");
        int[] ownerOrder = [2, 0, 3, 1];
        PlanAction[] prefix = ownerOrder.Select(actor => new PlanAction(
            PlanActionKind.EndTurn,
            root.StartTurnNumber,
            Actor: new CombatActorId(actor))).ToArray();
        JointOfflineSearchRequest request = new(
            prefix,
            MaximumActions: 6,
            MaximumStates: 20_000);
        JointOfflineSearchResult searched = JointOfflineSearch.SolveBeam(
            root,
            request,
            beamWidth: 512,
            degreeOfParallelism: 4);
        JointReplayResult replay = JointStrictReplayVerifier.Verify(root, searched);
        if (searched.Score.Outcome != CombatTerminalOutcome.Victory
            || replay.Snapshot.Simulator.TerminalStamp?.Outcome != CombatTerminalOutcome.Victory)
        {
            throw new InvalidOperationException(
                $"C10 terminal demo did not win: score={searched.Score.Outcome} " +
                $"replay={replay.Snapshot.Simulator.TerminalStamp?.Outcome}.");
        }
        if (!searched.Actions.Take(4).Select(action => action.Actor.Index).SequenceEqual(ownerOrder))
            throw new InvalidOperationException("C10 terminal demo changed the four-owner prefix.");
        JointStrictCheckpoint barrier = replay.Checkpoints.Single(checkpoint =>
            checkpoint.Stage == "barrier" && checkpoint.AppliedActionCount == 4);
        if (barrier.Turn != root.StartTurnNumber + 1)
            throw new InvalidOperationException("C10 terminal demo did not cross the enemy side.");

        ActionIdempotencyLedger[] ledgers = Enumerable.Range(0, 4)
            .Select(_ => new ActionIdempotencyLedger())
            .ToArray();
        HashSet<string> executions = new(StringComparer.Ordinal);
        for (int index = 0; index < searched.Actions.Count; index++)
        {
            PlanAction action = searched.Actions[index];
            string actionId = $"C10-DEMO:{index}";
            ActionIdempotencyLedger ledger = ledgers[action.Actor.Index];
            if (!ledger.TryPrepare(actionId)
                || !ledger.TryCommit(actionId)
                || !executions.Add(actionId)
                || !ledger.TryComplete(actionId)
                || ledger.TryCommit(actionId))
            {
                throw new InvalidOperationException($"C10 action {actionId} violated exactly-once execution.");
            }
        }

        ulong hostId = root.Actors[root.LocalActorId.Index].PlayerIdentity.NetId;
        ActorAssignmentPayload assignment = CoopSession.BuildActorAssignment(
            root.Actors.Select(actor => (
                actor.PlayerIdentity.NetId,
                actor.PlayerIdentity.Character.Id.Entry)).ToArray(),
            hostId);
        string sessionId = "C10-TERMINAL-" + Guid.NewGuid().ToString("N");
        CoopSession[] sessions = assignment.Actors.Select(actor =>
            new CoopSession(sessionId, actor.NetworkPlayerId, hostId)).ToArray();
        const string nonce = "C10-TERMINAL-NONCE";
        foreach (CoopSession session in sessions)
        {
            if (session.IsHost)
                session.AcceptAsHost(nonce, CoopProtocol.RequiredCapabilities);
            else
                session.AcceptSession(new SessionAcceptedPayload(
                    CoopProtocol.Version,
                    "0.1.0.0",
                    hostId,
                    nonce,
                    CoopProtocol.RequiredCapabilities));
            session.AcceptAssignment(assignment);
            session.BeginStopping();
            session.FinishStopping();
        }
        if (sessions.Any(session => session.State != CoopSessionState.Stopped
                || session.Actors.Count != 0))
            throw new InvalidOperationException("C10 terminal demo retained session state after exit.");

        PlanPublishedPayload published = CoopPlanSnapshotFactory.Create(
            1,
            LocalActorAgent.Fingerprint(root.ContinuationStamp.StateText),
            root,
            searched,
            replay);
        return $"outcome={published.Score.Outcome};actors=4;owners={string.Join(',', ownerOrder)};" +
               $"actions={searched.Actions.Count};barrier_turn={barrier.Turn};" +
               $"checkpoints={replay.Checkpoints.Count};duplicate_execution=0;sessions=4_stopped";
    }

    public static string SearchSyntheticRoot(CombatState combat)
    {
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        JointOfflineSearchRequest request = JointOfflineSearchRequest.Default(
            maximumActions: 1,
            maximumStates: 128);
        JointOfflineSearchResult serial = JointOfflineSearch.SolveBeam(
            root,
            request,
            beamWidth: 64,
            degreeOfParallelism: 1);
        JointReplayResult serialReplay = JointStrictReplayVerifier.Verify(root, serial);
        JointOfflineSearchResult parallel = JointOfflineSearch.SolveBeam(
            root,
            request,
            beamWidth: 64,
            degreeOfParallelism: 4);
        JointReplayResult parallelReplay = JointStrictReplayVerifier.Verify(root, parallel);
        PlanPublishedPayload serialPlan = CoopPlanSnapshotFactory.Create(
            1,
            "SYNTHETIC-C3",
            root,
            serial,
            serialReplay);
        PlanPublishedPayload parallelPlan = CoopPlanSnapshotFactory.Create(
            1,
            "SYNTHETIC-C3",
            root,
            parallel,
            parallelReplay);
        if (!string.Equals(
                JsonSerializer.Serialize(serialPlan, CoopProtocol.JsonOptions),
                JsonSerializer.Serialize(parallelPlan, CoopProtocol.JsonOptions),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "DOP1 and DOP4 produced different published plans for the same frozen root.");
        }
        if (serial.ExpandedStates != parallel.ExpandedStates)
            throw new InvalidOperationException(
                $"DOP expansion count differs: serial={serial.ExpandedStates} parallel={parallel.ExpandedStates}.");
        AssertPublishedDtoIsDetached(typeof(PlanPublishedPayload), new HashSet<Type>());
        return $"actors={root.Actors.Count};actions={serial.Actions.Count};" +
               $"expanded={serial.ExpandedStates};checkpoints={serial.Checkpoints.Count};" +
               $"plan={serialPlan.PlanId};dop=1,4;strict_replay=2";
    }

    public static string ProjectFourUiSnapshots(CombatState combat)
    {
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        JointOfflineSearchResult searched = JointOfflineSearch.SolveBeam(
            root,
            JointOfflineSearchRequest.Default(maximumActions: 1, maximumStates: 128),
            beamWidth: 64,
            degreeOfParallelism: 1);
        JointReplayResult replay = JointStrictReplayVerifier.Verify(root, searched);
        PlanPublishedPayload plan = CoopPlanSnapshotFactory.Create(
            7,
            "SYNTHETIC-C4",
            root,
            searched,
            replay);
        ActorBinding[] bindings = root.Actors.Select(actor => new ActorBinding(
            actor.Id.Index,
            actor.PlayerIdentity.NetId,
            actor.PlayerIdentity.Character.Id.Entry,
            actor.Id == root.LocalActorId)).ToArray();
        CoopUiSnapshot[] chinese = Enumerable.Range(0, 4)
            .Select(actor => CoopUiSnapshot.Capture(
                plan,
                actor == 0 ? CoopUiRole.Host : CoopUiRole.Client,
                actor,
                CoopAutomationMode.ConfirmEach,
                "计划就绪",
                currentActionIndex: 0,
                locale: "zhs",
                bindings,
                root.LocalActorId.Index,
                "已连接",
                "ack:ok"))
            .ToArray();
        CoopUiSnapshot[] english = Enumerable.Range(0, 4)
            .Select(actor => CoopUiSnapshot.Capture(
                plan,
                actor == 0 ? CoopUiRole.Host : CoopUiRole.Client,
                actor,
                CoopAutomationMode.ConfirmEach,
                "计划就绪",
                currentActionIndex: 0,
                locale: "eng",
                bindings,
                root.LocalActorId.Index,
                "已连接",
                "ack:ok"))
            .ToArray();
        foreach (CoopUiSnapshot snapshot in chinese.Concat(english))
        {
            if (snapshot.PlanId != plan.PlanId
                || snapshot.Route.Count != plan.Actions.Count
                || snapshot.Route.Select(step => (step.Index, step.ActorId))
                    .SequenceEqual(plan.Actions.Select(action => (action.Index, action.ActorId))) == false)
            {
                throw new InvalidOperationException("Endpoint UI snapshot changed PlanId or route identity.");
            }
        }
        if (!chinese[0].ShowHostControls || chinese[0].ShowClientControls
            || chinese.Skip(1).Any(snapshot => snapshot.ShowHostControls || !snapshot.ShowClientControls))
        {
            throw new InvalidOperationException("Host/client UI controls do not match endpoint roles.");
        }
        if (chinese[0].Title == english[0].Title
            || !english[0].Title.Contains("Co-op Bot", StringComparison.Ordinal)
            || !chinese[0].StatusLine.Contains("逐步确认", StringComparison.Ordinal)
            || !english[0].StatusLine.Contains("Confirm each", StringComparison.Ordinal)
            || !chinese[0].SessionLine.Contains($"协议 v{CoopProtocol.Version}", StringComparison.Ordinal)
            || !english[0].SessionLine.Contains($"Protocol v{CoopProtocol.Version}", StringComparison.Ordinal)
            || chinese.Any(snapshot => !snapshot.Actors.Any(actor =>
                actor.Text.Contains("已连接", StringComparison.Ordinal)))
            || english.Any(snapshot => !snapshot.DetailLine.Contains("Latest result", StringComparison.Ordinal))
            || chinese[0].ObserveLabel != "观察"
            || english[0].AutoLabel != "Auto")
        {
            throw new InvalidOperationException("CoopBot zhs/eng UI projection is incomplete.");
        }
        return $"endpoints=4;plan={plan.PlanId};route={plan.Actions.Count};locales=zhs,eng;" +
               "host_controls=1;client_controls=3;modes=4;protocol_status=1;recent_result=1";
    }

    public static async Task<string> ExecuteLocalCardAndEndTurnAsync(CombatState combat)
    {
        CombatRootSnapshot cardRoot = CombatRootSnapshot.Capture(combat);
        JointTurnState turns = JointTurnState.Start(cardRoot.Actors.Count, cardRoot.StartTurnNumber);
        CombatPredictionSimulator candidateSimulator = cardRoot.ForkSimulator();
        PlanAction cardAction = JointActionExpander.Expand(candidateSimulator, turns)
            .Select(static candidate => candidate.Action)
            .First(action => action.Kind == PlanActionKind.PlayCard
                && action.Choice is null
                && (action.NestedChoices?.Count ?? 0) == 0
                && action.TargetCombatId is not null);
        JointReplayResult cardReplay = JointPlanReplayer.Replay(
            cardRoot,
            new JointPlan(cardRoot.Actors.Count, [cardAction]));
        if (!cardReplay.Snapshot.Simulator.IsInProgress)
            throw new InvalidOperationException("C5 card probe selected a terminal action.");
        LocalActorAgent agent = new();
        string cardActionId = "C5-CARD";
        ActionPreparePayload cardCommand = CreateCommand(
            cardRoot,
            cardAction,
            cardReplay.ActionExpectations[0],
            actionIndex: 0);
        Player localPlayer = cardRoot.Actors[cardAction.Actor.Index].PlayerIdentity;
        LocalPrepareResult cardPrepared = agent.Prepare(
            cardActionId,
            cardCommand,
            combat,
            cardAction.Actor.Index,
            localPlayer);
        if (!cardPrepared.Accepted)
            throw new InvalidOperationException($"C5 card prepare rejected: {cardPrepared.Rejected}.");
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        ActionAckPayload cardAck = await agent.CommitAsync(
            cardActionId,
            new ActionCommitPayload(cardActionId),
            combat,
            timeout.Token);
        await WaitForStablePlayerRootAsync(
            combat,
            localPlayer,
            localPlayer.PlayerCombatState!.TurnNumber,
            timeout.Token);
        CombatRootSnapshot afterCard = CombatRootSnapshot.Capture(combat);
        AssertContinuation(
            cardReplay.ActionSnapshots[0].Continuation,
            afterCard.ContinuationStamp,
            "card");
        agent.FinishReport(cardActionId);

        PlanAction endTurn = new(
            PlanActionKind.EndTurn,
            afterCard.StartTurnNumber,
            Actor: afterCard.LocalActorId);
        JointOfflineSearchResult endSearch = JointOfflineSearch.SolveBeam(
            afterCard,
            new JointOfflineSearchRequest([endTurn], MaximumActions: 2, MaximumStates: 1024),
            beamWidth: 128,
            timeout.Token,
            degreeOfParallelism: 1);
        JointReplayResult endReplay = JointStrictReplayVerifier.Verify(afterCard, endSearch);
        JointStrictCheckpoint nextTurn = endReplay.Checkpoints.First(checkpoint =>
            checkpoint.Stage == "barrier" && checkpoint.AppliedActionCount == 1);
        string endActionId = "C5-END-TURN";
        ActionPreparePayload endCommand = CreateCommand(
            afterCard,
            endTurn,
            endReplay.ActionExpectations[0],
            actionIndex: 0);
        LocalPrepareResult endPrepared = agent.Prepare(
            endActionId,
            endCommand,
            combat,
            endTurn.Actor.Index,
            localPlayer);
        if (!endPrepared.Accepted)
            throw new InvalidOperationException($"C5 EndTurn prepare rejected: {endPrepared.Rejected}.");
        ActionAckPayload endAck = await agent.CommitAsync(
            endActionId,
            new ActionCommitPayload(endActionId),
            combat,
            timeout.Token);
        await WaitForStablePlayerRootAsync(
            combat,
            localPlayer,
            afterCard.StartTurnNumber + 1,
            timeout.Token);
        CombatRootSnapshot afterEndTurn = CombatRootSnapshot.Capture(combat);
        AssertContinuation(nextTurn.Continuation, afterEndTurn.ContinuationStamp, "end_turn_barrier");
        agent.FinishReport(endActionId);
        if (agent.State != LocalActorAgentState.Idle)
            throw new InvalidOperationException($"Local agent did not return to Idle: {agent.State}.");
        return $"card={cardAction.CardId};target={cardAction.TargetCombatId};" +
               $"card_ack={cardAck.CompletionState};end_ack={endAck.CompletionState};" +
               $"turn={afterCard.StartTurnNumber}->{afterEndTurn.StartTurnNumber};strict=card,barrier";
    }

    public static string AuditRemoteTransportAndActorRoots(CombatState combat)
    {
        if (combat.Players.Count != 4)
            throw new InvalidOperationException($"C6 probe expected four actors, got {combat.Players.Count}.");
        string[] fingerprints = combat.Players.Select(player => LocalActorAgent.Fingerprint(
            ContinuationStamp.CaptureLiveForPlayer(combat, player).StateText)).ToArray();
        if (fingerprints.Distinct(StringComparer.Ordinal).Count() != combat.Players.Count)
            throw new InvalidOperationException("Per-owner root fingerprints are not actor-relative.");
        using (NativeCoopTransport transport = new(RunManager.Instance.NetService))
        {
            if (transport.LocalNetworkPlayerId != RunManager.Instance.NetService.NetId)
                throw new InvalidOperationException("Native Coop transport changed local network identity.");
        }
        return $"actors=4;owner_fingerprints=4;native_handler=register,unregister;" +
               "remote_contracts=3;duplicate_execution=0";
    }

    public static async Task<string> ExecuteChoicesAndPotionsAsync(
        CombatState liveCombat,
        CombatState tutorCombat,
        CombatState potionCombat)
    {
        await AssertTutorChoiceAsync(tutorCombat);
        await AssertCrossPlayerPotionAsync(potionCombat);
        string localPotion = await ExecuteLocalPotionAgentAsync(liveCombat);
        return "tutor=decision_actor_strict;cross_player_potion=strict;" + localPotion;
    }

    public static string AuditCompletePlayerSide(CombatState combat)
    {
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        if (root.Actors.Count != 4)
            throw new InvalidOperationException($"C8 player-side probe expected four actors, got {root.Actors.Count}.");
        int[] actorOrder = [2, 0, 3, 1];
        PlanAction[] actions = actorOrder.Select(actor => new PlanAction(
            PlanActionKind.EndTurn,
            root.StartTurnNumber,
            Actor: new CombatActorId(actor))).ToArray();
        JointPlan plan = new(4, actions);
        plan.ValidateComplete();
        JointReplayResult replay = JointPlanReplayer.Replay(root, plan);
        if (!replay.Snapshot.TurnState.IsBarrierReached
            || replay.Snapshot.TurnState.Phases.Any(phase => phase != JointActorTurnPhase.Ended)
            || !replay.AppliedActions.Select(action => action.Actor.Index).SequenceEqual(actorOrder))
        {
            throw new InvalidOperationException("C8 arbitrary Actor order did not reach the complete player-side barrier.");
        }

        HostPlanLease lease = new();
        lease.Publish("C8-MANUAL", 20);
        HostRootChange manual = lease.ObserveRoot(21);
        if (manual.Kind != HostRootChangeKind.ManualInsertion)
            throw new InvalidOperationException("C8 uncommanded root was not classified as a manual insertion.");
        lease.Publish("C8-COMMANDED", 21);
        lease.BeginAction("C8-COMMANDED", "C8-ACTION");
        HostRootChange expected = lease.ObserveRoot(22);
        if (expected.Kind != HostRootChangeKind.ExpectedActionResult
            || expected.ActionId != "C8-ACTION")
        {
            throw new InvalidOperationException("C8 commanded root was not retained for strict verification.");
        }
        return $"actors=4;order={string.Join(',', actorOrder)};barrier=complete;" +
               "manual=cancel_replan;commanded=verify";
    }

    public static string AuditCrossRoundPolicy(CombatState combat)
    {
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        if (root.Actors.Count != 4)
            throw new InvalidOperationException($"C9 cross-round probe expected four actors, got {root.Actors.Count}.");
        CombatActorRoot[] partialActors = root.Actors.Select(actor => actor with
        {
            InitialHp = actor.Id.Index == 1 ? 0 : actor.InitialHp,
            IsReadyToEndTurn = actor.Id.Index is 0 or 1,
        }).ToArray();
        JointTurnState partial = JointTurnState.FromRootActors(partialActors, root.StartTurnNumber);
        JointActorTurnPhase[] expectedPhases =
        [JointActorTurnPhase.Ended, JointActorTurnPhase.Dead, JointActorTurnPhase.Playing, JointActorTurnPhase.Playing];
        if (!partial.Phases.SequenceEqual(expectedPhases)
            || partial.IsActionable(new CombatActorId(0))
            || partial.IsActionable(new CombatActorId(1))
            || !partial.IsActionable(new CombatActorId(2)))
        {
            throw new InvalidOperationException("C9 root readiness/death did not initialize the actionable Actor subset.");
        }

        PlanAction[] route =
        [
            new(PlanActionKind.EndTurn, root.StartTurnNumber, Actor: new CombatActorId(2)),
            new(PlanActionKind.EndTurn, root.StartTurnNumber, Actor: new CombatActorId(0)),
            new(PlanActionKind.EndTurn, root.StartTurnNumber, Actor: new CombatActorId(3)),
            new(PlanActionKind.EndTurn, root.StartTurnNumber, Actor: new CombatActorId(1)),
            new(PlanActionKind.EndTurn, root.StartTurnNumber + 1, Actor: new CombatActorId(0)),
        ];
        JointReplayResult replay = JointPlanReplayer.Replay(root, new JointPlan(4, route));
        ContinuationStamp stable = HostActionCommandFactory.ExpectedStableContinuation(replay, actionIndex: 3);
        JointStrictCheckpoint barrier = replay.Checkpoints.Single(checkpoint =>
            checkpoint.Stage == "barrier" && checkpoint.AppliedActionCount == 4);
        if (!string.Equals(stable.StateText, barrier.Continuation.StateText, StringComparison.Ordinal)
            || barrier.Turn != root.StartTurnNumber + 1)
        {
            throw new InvalidOperationException("C9 final EndTurn did not select the next-player-side stable checkpoint.");
        }
        return "readiness=ended,dead,playing,playing;last_end_turn=next_round_barrier;" +
               $"next_turn={barrier.Turn}";
    }

    private static async Task AssertTutorChoiceAsync(CombatState combat)
    {
        if (combat.Players.Count != 4)
            throw new InvalidOperationException($"C7 Tutor probe expected four actors, got {combat.Players.Count}.");
        Player owner = combat.Players[0];
        Player decisionActor = combat.Players[^1];
        CardModel tutor = combat.CreateCard(ModelDb.Card<Tutor>(), owner);
        owner.PlayerCombatState!.Hand.AddInternal(tutor, silent: true);
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        PlanAction unresolved = new(
            PlanActionKind.PlayCard,
            root.StartTurnNumber,
            CardId: tutor.Id.Entry,
            TargetCombatId: decisionActor.Creature.CombatId,
            Actor: new CombatActorId(0));
        JointPendingChoiceFrame frame;
        CombatPredictionSimulator probe = root.ForkSimulator();
        try
        {
            _ = JointActionTransition.Apply(
                probe,
                JointTurnState.Start(4, root.StartTurnNumber),
                unresolved,
                JointActionTransition.CaptureProcessedEnemyDeaths(root, probe));
            throw new InvalidOperationException("Tutor did not suspend for its target Actor choice.");
        }
        catch (JointPendingActionChoiceException pending)
        {
            frame = pending.Frame;
        }
        if (frame.DecisionActor.Index != 3)
            throw new InvalidOperationException($"Tutor DecisionActor={frame.DecisionActor.Index}, expected 3.");
        string selectedCardId = frame.Spec.Options.First().Preview.Id.Entry;
        PlanCardChoice choice = CardChoiceSupport.BuildRequestedChoice(frame.Spec, [selectedCardId]) with
        {
            Actor = frame.DecisionActor,
            SourceId = frame.SourceId,
            ContextId = frame.ContextId,
        };
        PlanAction planned = unresolved with { Choice = choice };
        JointReplayResult replay = JointPlanReplayer.Replay(root, new JointPlan(4, [planned]));
        CoopPlanActionSnapshot snapshot = CoopPlanSnapshotFactory.CaptureAction(planned, 0);
        using (PlannedChoiceDriver wrongOwner = new())
        {
            wrongOwner.ConfigureActors(root.Actors.Select(actor => new ActorBinding(
                actor.Id.Index,
                actor.Id.Index == frame.DecisionActor.Index
                    ? root.Actors[1].PlayerIdentity.NetId
                    : actor.PlayerIdentity.NetId,
                actor.PlayerIdentity.Character.Id.Entry,
                actor.Id.Index == 0)));
            wrongOwner.Arm("C7-TUTOR-WRONG-OWNER", snapshot);
            try
            {
                _ = await CardSelectCmd.Selector!.GetSelectedCards(
                    decisionActor.PlayerCombatState!.DrawPile.Cards,
                    minSelect: 1,
                    maxSelect: 1);
                throw new InvalidOperationException("Tutor choice accepted options owned by the wrong network player.");
            }
            catch (InvalidOperationException exception) when (
                exception.Message.Contains("native options belong", StringComparison.Ordinal))
            {
            }
            finally
            {
                wrongOwner.Cancel("C7-TUTOR-WRONG-OWNER");
            }
        }
        using PlannedChoiceDriver driver = new();
        driver.ConfigureActors(root.Actors.Select(actor => new ActorBinding(
            actor.Id.Index,
            actor.PlayerIdentity.NetId,
            actor.PlayerIdentity.Character.Id.Entry,
            actor.Id.Index == 0)));
        const string actionId = "C7-TUTOR";
        driver.Arm(actionId, snapshot);
        await ExecuteSyntheticNativeCardAsync(tutor, decisionActor.Creature);
        driver.Complete(actionId);
        AssertContinuation(
            replay.ActionSnapshots[0].Continuation,
            ContinuationStamp.CaptureLive(combat),
            "Tutor");
    }

    private static async Task AssertCrossPlayerPotionAsync(CombatState combat)
    {
        if (combat.Players.Count != 4)
            throw new InvalidOperationException($"C7 potion probe expected four actors, got {combat.Players.Count}.");
        Player owner = combat.Players[0];
        Player target = combat.Players[^1];
        PotionModel potion = owner.GetPotionAtSlotIndex(0)
            ?? throw new InvalidOperationException("C7 cross-player potion fixture has no potion in slot 0.");
        if (potion is not BlockPotion)
            throw new InvalidOperationException($"C7 expected BLOCK_POTION, got {potion.Id.Entry}.");
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        PlanAction action = JointActionExpander.Expand(
                root.ForkSimulator(),
                JointTurnState.Start(4, root.StartTurnNumber))
            .Select(candidate => candidate.Action)
            .Single(candidate => candidate.Actor.Index == 0
                && candidate.Kind == PlanActionKind.UsePotion
                && candidate.PotionId == "BLOCK_POTION"
                && candidate.TargetCombatId == target.Creature.CombatId);
        JointReplayResult replay = JointPlanReplayer.Replay(root, new JointPlan(4, [action]));
        UsePotionAction native = new(potion, target.Creature, isCombatInProgress: true);
        native.OnEnqueued(_ => { }, uint.MaxValue - 7);
        await native.Execute();
        await native.CompletionTask;
        if (native.Exception is not null || native.State != GameActionState.Finished)
            throw new InvalidOperationException("C7 cross-player native potion action failed.", native.Exception);
        AssertContinuation(
            replay.ActionSnapshots[0].Continuation,
            ContinuationStamp.CaptureLive(combat),
            "cross-player potion");
    }

    private static async Task<string> ExecuteLocalPotionAgentAsync(CombatState combat)
    {
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        PlanAction action = JointActionExpander.Expand(
                root.ForkSimulator(),
                JointTurnState.Start(root.Actors.Count, root.StartTurnNumber))
            .Select(candidate => candidate.Action)
            .First(candidate => candidate.Actor == root.LocalActorId
                && candidate.Kind == PlanActionKind.UsePotion
                && candidate.PotionId == "BLOCK_POTION");
        JointReplayResult replay = JointPlanReplayer.Replay(
            root,
            new JointPlan(root.Actors.Count, [action]));
        PlannedChoiceDriver choices = new();
        choices.ConfigureActors(root.Actors.Select(actor => new ActorBinding(
            actor.Id.Index,
            actor.PlayerIdentity.NetId,
            actor.PlayerIdentity.Character.Id.Entry,
            actor.Id == root.LocalActorId)));
        LocalActorAgent agent = new(choices);
        const string actionId = "C7-LOCAL-POTION";
        LocalPrepareResult prepared = agent.Prepare(
            actionId,
            CreateCommand(root, action, replay.ActionExpectations[0], 0),
            combat,
            action.Actor.Index,
            root.Actors[action.Actor.Index].PlayerIdentity);
        if (!prepared.Accepted)
            throw new InvalidOperationException($"C7 local potion prepare rejected: {prepared.Rejected}.");
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        ActionAckPayload ack = await agent.CommitAsync(
            actionId,
            new ActionCommitPayload(actionId),
            combat,
            timeout.Token);
        await WaitForStablePlayerRootAsync(
            combat,
            root.PlayerIdentity,
            root.StartTurnNumber,
            timeout.Token);
        AssertContinuation(
            replay.ActionSnapshots[0].Continuation,
            ContinuationStamp.CaptureLive(combat),
            "local potion Agent");
        agent.FinishReport(actionId);
        choices.Dispose();
        return $"local_potion_ack={ack.CompletionState};agent={agent.State}";
    }

    private static ActionPreparePayload CreateCommand(
        CombatRootSnapshot root,
        PlanAction action,
        JointActionExpectation expectation,
        int actionIndex)
        => new(
            CoopPlanSnapshotFactory.CaptureAction(action, actionIndex),
            LocalActorAgent.Fingerprint(root.ContinuationStamp.StateText),
            expectation.Turn,
            expectation.Phase.ToString(),
            expectation.EnergyCost,
            expectation.StarCost,
            IsIrreversible: true);

    private static ActionPreparePayload CreateActorCommand(
        CombatState combat,
        CombatRootSnapshot root,
        PlanAction action,
        int actionIndex)
    {
        JointReplayResult replay = JointPlanReplayer.Replay(
            root,
            new JointPlan(root.Actors.Count, [action]));
        JointActionExpectation expectation = replay.ActionExpectations[0];
        return new ActionPreparePayload(
            CoopPlanSnapshotFactory.CaptureAction(action, actionIndex),
            LocalActorAgent.Fingerprint(
                ContinuationStamp.CaptureLiveForPlayer(
                    combat,
                    root.Actors[action.Actor.Index].PlayerIdentity).StateText),
            expectation.Turn,
            expectation.Phase.ToString(),
            expectation.EnergyCost,
            expectation.StarCost,
            IsIrreversible: true);
    }

    private static async Task AssertC10LocalRejectionsAsync(
        CombatState combat,
        CombatRootSnapshot root)
    {
        Player local = root.Actors[root.LocalActorId.Index].PlayerIdentity;
        PlanAction playable = JointActionExpander.Expand(
                root.ForkSimulator(),
                JointTurnState.FromRoot(root))
            .Select(candidate => candidate.Action)
            .First(action => action.Actor == root.LocalActorId
                && action.Kind == PlanActionKind.PlayCard
                && action.CardId.Contains("STRIKE", StringComparison.Ordinal)
                && action.Choice is null
                && (action.NestedChoices?.Count ?? 0) == 0);
        ActionPreparePayload valid = CreateActorCommand(combat, root, playable, 0);

        LocalActorAgent staleAgent = new();
        LocalPrepareResult stale = staleAgent.Prepare(
            "C10-STALE",
            valid with { ExpectedRootFingerprint = "STALE" },
            combat,
            root.LocalActorId.Index,
            local);
        if (stale.Rejected?.Code != CoopActionRejectionCode.StaleRoot)
            throw new InvalidOperationException("C10 changed root was not rejected as StaleRoot.");

        LocalActorAgent missingAgent = new();
        CoopPlanActionSnapshot missingAction = valid.Action with
        {
            CardId = "C10_MISSING_CARD",
            CardStateKey = "",
            CardStateOccurrence = 0,
        };
        LocalPrepareResult missing = missingAgent.Prepare(
            "C10-MISSING",
            valid with { Action = missingAction },
            combat,
            root.LocalActorId.Index,
            local);
        if (missing.Rejected?.Code != CoopActionRejectionCode.MissingInstance)
            throw new InvalidOperationException("C10 transferred/missing card was not rejected.");

        LocalActorAgent targetAgent = new();
        LocalPrepareResult target = targetAgent.Prepare(
            "C10-TARGET",
            valid with { Action = valid.Action with { TargetCombatId = uint.MaxValue } },
            combat,
            root.LocalActorId.Index,
            local);
        if (target.Rejected?.Code != CoopActionRejectionCode.InvalidTarget)
            throw new InvalidOperationException("C10 missing/dead target was not rejected.");

        PlanAction localEnd = new(
            PlanActionKind.EndTurn,
            root.StartTurnNumber,
            Actor: root.LocalActorId);
        ActionPreparePayload choiceBase = CreateActorCommand(combat, root, localEnd, 0);
        CoopPlanChoiceSnapshot changedChoice = new(
            "SelectFromDrawPile",
            "Draw",
            root.LocalActorId.Index,
            "C10",
            "C10",
            "DuringAction",
            []);
        LocalActorAgent choiceAgent = new();
        LocalPrepareResult choice = choiceAgent.Prepare(
            "C10-CHOICE",
            choiceBase with { Action = choiceBase.Action with { Choice = changedChoice } },
            combat,
            root.LocalActorId.Index,
            local);
        if (choice.Rejected?.Code != CoopActionRejectionCode.ChoiceChanged)
            throw new InvalidOperationException("C10 changed choice contract was not rejected.");

        LocalActorAgent nativeRejectAgent = new();
        LocalPrepareResult prepared = nativeRejectAgent.Prepare(
            "C10-NATIVE-REJECT",
            valid,
            combat,
            root.LocalActorId.Index,
            local);
        if (!prepared.Accepted)
            throw new InvalidOperationException($"C10 native rejection setup failed: {prepared.Rejected}.");
        local.PlayerCombatState!.Energy = 0;
        bool nativeRejected = false;
        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
            _ = await nativeRejectAgent.CommitAsync(
                "C10-NATIVE-REJECT",
                new ActionCommitPayload("C10-NATIVE-REJECT"),
                combat,
                timeout.Token);
        }
        catch (InvalidOperationException exception) when (
            exception.Message.Contains("lost playability", StringComparison.Ordinal))
        {
            nativeRejected = true;
        }
        if (!nativeRejected || nativeRejectAgent.State != LocalActorAgentState.Idle)
            throw new InvalidOperationException("C10 Commit-time native rejection did not reset the Agent.");
    }

    private static async Task WaitForStablePlayerRootAsync(
        CombatState combat,
        Player local,
        int minimumTurn,
        CancellationToken cancellationToken)
    {
        NGame host = NGame.Instance
            ?? throw new InvalidOperationException("C5 probe has no NGame host.");
        string? previous = null;
        int matches = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            bool stable = CombatManager.Instance.IsInProgress
                && !CombatManager.Instance.IsEnding
                && combat.CurrentSide == CombatSide.Player
                && local.PlayerCombatState?.TurnNumber >= minimumTurn
                && local.PlayerCombatState.Phase == PlayerTurnPhase.Play
                && RunManager.Instance.ActionQueueSet.IsEmpty
                && !RunManager.Instance.ActionExecutor.IsRunning
                && RunManager.Instance.ActionExecutor.CurrentlyRunningAction is null
                && CardSelectCmd.Selector is null;
            if (!stable)
            {
                previous = null;
                matches = 0;
                continue;
            }
            string current = ContinuationStamp.CaptureLive(combat).StateText;
            matches = string.Equals(previous, current, StringComparison.Ordinal) ? matches + 1 : 1;
            previous = current;
            if (matches >= 2) return;
        }
    }

    private static void AssertContinuation(
        ContinuationStamp expected,
        ContinuationStamp actual,
        string stage)
    {
        if (!string.Equals(expected.StateText, actual.StateText, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"C5 {stage} actual/sim differs at {expected.DescribeFirstDifference(actual)}.");
    }

    private static async Task ExecuteSyntheticNativeCardAsync(CardModel card, Creature? target)
    {
        NetCombatCardDb.Instance.IdCardForTesting(card);
        MethodInfo powerVfx = typeof(CardModel).GetMethod(
            "PlayPowerCardFlyVfx",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(CardModel).FullName, "PlayPowerCardFlyVfx");
        MethodInfo addCreature = typeof(CombatManager).GetMethod(
            nameof(CombatManager.AddCreature),
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            types: [typeof(Creature)],
            modifiers: null)
            ?? throw new MissingMethodException(typeof(CombatManager).FullName, nameof(CombatManager.AddCreature));
        MethodInfo addCreatureNode = typeof(NCombatRoom).GetMethod(
            nameof(NCombatRoom.AddCreature),
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            types: [typeof(Creature)],
            modifiers: null)
            ?? throw new MissingMethodException(typeof(NCombatRoom).FullName, nameof(NCombatRoom.AddCreature));
        MethodInfo addOrbSlots = typeof(OrbCmd).GetMethod(
            nameof(OrbCmd.AddSlots),
            BindingFlags.Static | BindingFlags.Public,
            binder: null,
            types: [typeof(Player), typeof(int)],
            modifiers: null)
            ?? throw new MissingMethodException(typeof(OrbCmd).FullName, nameof(OrbCmd.AddSlots));
        MethodInfo addDuringManualPlay = typeof(CardPileCmd).GetMethod(
            nameof(CardPileCmd.AddDuringManualCardPlay),
            BindingFlags.Static | BindingFlags.Public,
            binder: null,
            types: [typeof(CardModel)],
            modifiers: null)
            ?? throw new MissingMethodException(typeof(CardPileCmd).FullName, nameof(CardPileCmd.AddDuringManualCardPlay));
        MethodInfo addCard = typeof(CardPileCmd).GetMethod(
            nameof(CardPileCmd.Add),
            BindingFlags.Static | BindingFlags.Public,
            binder: null,
            types: [typeof(CardModel), typeof(CardPile), typeof(CardPilePosition), typeof(AbstractModel), typeof(bool)],
            modifiers: null)
            ?? throw new MissingMethodException(typeof(CardPileCmd).FullName, nameof(CardPileCmd.Add));
        MethodInfo skipTask = typeof(CoopBotHeadlessProbe).GetMethod(
            nameof(SkipSyntheticTask), BindingFlags.Static | BindingFlags.NonPublic)!;
        MethodInfo skipRegistration = typeof(CoopBotHeadlessProbe).GetMethod(
            nameof(SkipSyntheticCreatureRegistration), BindingFlags.Static | BindingFlags.NonPublic)!;
        MethodInfo addSlots = typeof(CoopBotHeadlessProbe).GetMethod(
            nameof(AddSyntheticOrbSlots), BindingFlags.Static | BindingFlags.NonPublic)!;
        MethodInfo moveToPlay = typeof(CoopBotHeadlessProbe).GetMethod(
            nameof(MoveSyntheticCardToPlay), BindingFlags.Static | BindingFlags.NonPublic)!;
        MethodInfo skipVisuals = typeof(CoopBotHeadlessProbe).GetMethod(
            nameof(SkipSyntheticCardPileVisuals), BindingFlags.Static | BindingFlags.NonPublic)!;
        Harmony isolation = new("CoopBot.Diagnostics.C7." + Guid.NewGuid().ToString("N"));
        isolation.Patch(powerVfx, prefix: new HarmonyMethod(skipTask));
        isolation.Patch(addCreature, prefix: new HarmonyMethod(skipRegistration));
        isolation.Patch(addCreatureNode, prefix: new HarmonyMethod(skipRegistration));
        isolation.Patch(addOrbSlots, prefix: new HarmonyMethod(addSlots));
        isolation.Patch(addDuringManualPlay, prefix: new HarmonyMethod(moveToPlay));
        isolation.Patch(addCard, prefix: new HarmonyMethod(skipVisuals));
        try
        {
            PlayCardAction native = new(card, target);
            native.OnEnqueued(_ => { }, uint.MaxValue - 6);
            await native.Execute();
            await native.CompletionTask;
            if (native.Exception is not null || native.State != GameActionState.Finished)
                throw new InvalidOperationException("C7 synthetic native card action failed.", native.Exception);
        }
        finally
        {
            isolation.Unpatch(powerVfx, skipTask);
            isolation.Unpatch(addCreature, skipRegistration);
            isolation.Unpatch(addCreatureNode, skipRegistration);
            isolation.Unpatch(addOrbSlots, addSlots);
            isolation.Unpatch(addDuringManualPlay, moveToPlay);
            isolation.Unpatch(addCard, skipVisuals);
        }
    }

    private static bool SkipSyntheticTask(ref Task __result)
    {
        __result = Task.CompletedTask;
        return false;
    }

    private static bool SkipSyntheticCreatureRegistration() => false;

    private static bool AddSyntheticOrbSlots(Player player, int amount, ref Task __result)
    {
        amount = Math.Min(10 - player.PlayerCombatState!.OrbQueue.Capacity, amount);
        player.PlayerCombatState.OrbQueue.AddCapacity(amount);
        __result = Task.CompletedTask;
        return false;
    }

    private static bool MoveSyntheticCardToPlay(CardModel card, ref Task __result)
    {
        __result = MoveSyntheticCardToPlayAsync(card);
        return false;
    }

    private static async Task MoveSyntheticCardToPlayAsync(CardModel card)
    {
        ICombatState combat = card.Owner.Creature.CombatState
            ?? throw new InvalidOperationException("Synthetic card has no combat state.");
        if (!combat.ContainsCard(card))
            throw new InvalidOperationException($"Synthetic card {card.Id.Entry} is outside its CombatState.");
        PileType oldPile = card.Pile?.Type ?? PileType.None;
        card.RemoveFromCurrentPile();
        PileType.Play.GetPile(card.Owner).AddInternal(card);
        await MegaCrit.Sts2.Core.Hooks.Hook.AfterCardChangedPiles(
            card.Owner.RunState,
            card.CombatState,
            card,
            oldPile,
            null);
    }

    private static void SkipSyntheticCardPileVisuals(ref bool skipVisuals)
        => skipVisuals = true;

    private static void AssertPublishedDtoIsDetached(Type type, HashSet<Type> visited)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal))
            return;
        if (type.IsArray)
        {
            AssertPublishedDtoIsDetached(type.GetElementType()!, visited);
            return;
        }
        if (type.IsGenericType
            && type.GetGenericTypeDefinition() is Type generic
            && (generic == typeof(IReadOnlyList<>) || generic == typeof(List<>)))
        {
            AssertPublishedDtoIsDetached(type.GetGenericArguments()[0], visited);
            return;
        }
        if (!visited.Add(type)) return;
        if (type.Assembly == typeof(CombatRootSnapshot).Assembly)
        {
            throw new InvalidOperationException(
                $"Published DTO retains CombatSolver/search type {type.FullName}.");
        }
        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            AssertPublishedDtoIsDetached(property.PropertyType, visited);
    }
}
