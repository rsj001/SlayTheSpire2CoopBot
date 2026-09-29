using CoopBot.Capture;
using CoopBot.Protocol;
using CoopBot.UI;
using CoopBot.NativeAdapter;
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

namespace CoopBot.Diagnostics;

public static class CoopBotHeadlessProbe
{
    public static string AuditSyntheticRoot(CombatState combat)
    {
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        HostVisibilityAuditResult audit = HostVisibilityAudit.Inspect(combat, root);
        if (!audit.IsComplete)
            throw new InvalidOperationException(
                "Synthetic Host visibility audit failed: " + string.Join(',', audit.Failures));
        return $"actors={root.Actors.Count};rng=9;fingerprint_fields={root.ContinuationStamp.StateText.Split(';').Length}";
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
        CoopUiSnapshot[] chinese = Enumerable.Range(0, 4)
            .Select(actor => CoopUiSnapshot.Capture(
                plan,
                actor == 0 ? CoopUiRole.Host : CoopUiRole.Client,
                actor,
                CoopAutomationMode.ConfirmEach,
                "计划就绪",
                currentActionIndex: 0,
                locale: "zhs"))
            .ToArray();
        CoopUiSnapshot[] english = Enumerable.Range(0, 4)
            .Select(actor => CoopUiSnapshot.Capture(
                plan,
                actor == 0 ? CoopUiRole.Host : CoopUiRole.Client,
                actor,
                CoopAutomationMode.ConfirmEach,
                "计划就绪",
                currentActionIndex: 0,
                locale: "eng"))
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
            || !english[0].StatusLine.Contains("Confirm each", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("CoopBot zhs/eng UI projection is incomplete.");
        }
        return $"endpoints=4;plan={plan.PlanId};route={plan.Actions.Count};locales=zhs,eng;" +
               "host_controls=1;client_controls=3";
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
