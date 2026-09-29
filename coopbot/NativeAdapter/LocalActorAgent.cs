using System.Security.Cryptography;
using System.Text;
using CombatSolver;
using CoopBot.Protocol;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Actions;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Models;

namespace CoopBot.NativeAdapter;

internal enum LocalActorAgentState
{
    Idle,
    Validating,
    Prepared,
    Executing,
    WaitingNativeCompletion,
    Reporting,
}

internal sealed record LocalPrepareResult(
    ActionPreparedPayload? Prepared,
    ActionRejectedPayload? Rejected)
{
    internal bool Accepted => Prepared is not null;
}

internal sealed class LocalActorAgent
{
    private sealed record PreparedNativeAction(
        string ActionId,
        ActionPreparePayload Command,
        Player Player,
        CardModel? Card,
        Creature? Target);

    private PreparedNativeAction? _prepared;

    internal LocalActorAgentState State { get; private set; }
    internal bool BlocksRootCapture => State is
        LocalActorAgentState.Validating or
        LocalActorAgentState.Prepared or
        LocalActorAgentState.Executing or
        LocalActorAgentState.WaitingNativeCompletion;

    internal LocalPrepareResult Prepare(
        string actionId,
        ActionPreparePayload command,
        CombatState combat,
        int localActorId,
        Player localPlayer)
    {
        if (State != LocalActorAgentState.Idle)
            return Reject(CoopActionRejectionCode.ActionInFlight, $"state={State}");
        State = LocalActorAgentState.Validating;
        try
        {
            if (command.Action.ActorId != localActorId)
                return RejectAndReset(CoopActionRejectionCode.NotLocalActor,
                    $"command={command.Action.ActorId} local={localActorId}");
            string actualFingerprint = Fingerprint(
                ContinuationStamp.CaptureLiveForPlayer(combat, localPlayer).StateText);
            if (!string.Equals(command.ExpectedRootFingerprint, actualFingerprint, StringComparison.Ordinal))
                return RejectAndReset(CoopActionRejectionCode.StaleRoot,
                    $"expected={command.ExpectedRootFingerprint} actual={actualFingerprint}");
            PlayerCombatState playerState = localPlayer.PlayerCombatState
                ?? throw new InvalidOperationException("Local actor has no combat state.");
            if (playerState.TurnNumber != command.ExpectedTurn
                || playerState.Phase != PlayerTurnPhase.Play
                || !string.Equals(command.ExpectedPhase, "Playing", StringComparison.Ordinal))
            {
                return RejectAndReset(CoopActionRejectionCode.NotPlayPhase,
                    $"turn={playerState.TurnNumber}/{command.ExpectedTurn} phase={playerState.Phase}/{command.ExpectedPhase}");
            }
            if (command.Action.Choice is not null
                || command.Action.NestedChoices.Count > 0
                || command.Action.TurnStartChoices.Count > 0)
            {
                return RejectAndReset(CoopActionRejectionCode.ChoiceChanged,
                    "C5 local agent does not accept choice-bearing actions.");
            }

            CardModel? card = null;
            Creature? target = ResolveTarget(combat, command.Action.TargetCombatId);
            switch (command.Action.Kind)
            {
                case "PlayCard":
                    PlanAction planAction = ToPlanAction(command.Action);
                    try
                    {
                        card = SolverController.FindCardForDeployment(
                            playerState.Hand.Cards.ToList(),
                            planAction);
                    }
                    catch (InvalidOperationException exception)
                    {
                        return RejectAndReset(CoopActionRejectionCode.MissingInstance, exception.Message);
                    }
                    if (!card.CanPlayTargeting(target))
                        return RejectAndReset(CoopActionRejectionCode.InvalidTarget,
                            $"card={card.Id.Entry} target={command.Action.TargetCombatId?.ToString() ?? "-"}");
                    int energyCost = card.EnergyCost.GetAmountToSpend();
                    int starCost = Math.Max(0, card.GetStarCostWithModifiers());
                    if (energyCost != command.ExpectedEnergyCost || starCost != command.ExpectedStarCost)
                        return RejectAndReset(CoopActionRejectionCode.CostChanged,
                            $"energy={energyCost}/{command.ExpectedEnergyCost} stars={starCost}/{command.ExpectedStarCost}");
                    break;
                case "EndTurn":
                    if (command.ExpectedEnergyCost != 0 || command.ExpectedStarCost != 0)
                        return RejectAndReset(CoopActionRejectionCode.CostChanged, "EndTurn has a non-zero expected cost.");
                    break;
                default:
                    return RejectAndReset(CoopActionRejectionCode.ProtocolMismatch,
                        $"C5 unsupported action kind {command.Action.Kind}.");
            }

            _prepared = new PreparedNativeAction(actionId, command, localPlayer, card, target);
            State = LocalActorAgentState.Prepared;
            return new LocalPrepareResult(
                new ActionPreparedPayload(
                    localActorId,
                    command.Action.Kind,
                    card is null
                        ? $"turn:{command.ExpectedTurn}"
                        : $"{command.Action.CardStateKey}@{command.Action.CardStateOccurrence}"),
                null);
        }
        catch
        {
            _prepared = null;
            State = LocalActorAgentState.Idle;
            throw;
        }
    }

    internal async Task<ActionAckPayload> CommitAsync(
        string actionId,
        ActionCommitPayload commit,
        CombatState combat,
        CancellationToken cancellationToken)
    {
        PreparedNativeAction prepared = State == LocalActorAgentState.Prepared
            && _prepared is not null
            && string.Equals(_prepared.ActionId, actionId, StringComparison.Ordinal)
            && string.Equals(commit.PreparedActionId, actionId, StringComparison.Ordinal)
                ? _prepared
                : throw new InvalidOperationException(
                    $"Commit does not match prepared action: state={State} action={actionId}.");
        State = LocalActorAgentState.Executing;
        try
        {
            GameAction native = await EnqueueAndCaptureAsync(prepared, cancellationToken);
            State = LocalActorAgentState.WaitingNativeCompletion;
            await native.CompletionTask.WaitAsync(cancellationToken);
            if (native.Exception is not null || native.State != GameActionState.Finished)
                throw new InvalidOperationException(
                    $"Native action {native.GetType().Name} did not finish in state {native.State}.",
                    native.Exception);
            State = LocalActorAgentState.Reporting;
            return new ActionAckPayload(
                native.GetType().Name,
                native.State.ToString(),
                Fingerprint(ContinuationStamp.CaptureLive(combat).StateText));
        }
        catch
        {
            _prepared = null;
            State = LocalActorAgentState.Idle;
            throw;
        }
    }

    internal void FinishReport(string actionId)
    {
        if (State != LocalActorAgentState.Reporting
            || _prepared is null
            || !string.Equals(_prepared.ActionId, actionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Cannot finish action report {actionId} from state {State}.");
        }
        _prepared = null;
        State = LocalActorAgentState.Idle;
    }

    internal void Cancel()
    {
        if (State is LocalActorAgentState.Executing or LocalActorAgentState.WaitingNativeCompletion)
            throw new InvalidOperationException("Cannot cancel an already committed native action.");
        _prepared = null;
        State = LocalActorAgentState.Idle;
    }

    internal static string Fingerprint(string stateText)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stateText)));

    private static PlanAction ToPlanAction(CoopPlanActionSnapshot action)
        => new(
            PlanActionKind.PlayCard,
            action.Turn,
            CardId: action.CardId,
            CardOccurrence: action.CardOccurrence,
            TargetIndex: action.TargetIndex,
            TargetCombatId: action.TargetCombatId,
            CardStateKey: action.CardStateKey,
            CardStateOccurrence: action.CardStateOccurrence,
            CardUpgradeLevel: action.CardUpgradeLevel,
            CardEnchantmentId: action.CardEnchantmentId,
            Actor: new CombatActorId(action.ActorId));

    private static Creature? ResolveTarget(CombatState combat, uint? combatId)
        => combatId is null
            ? null
            : combat.Creatures.SingleOrDefault(creature => creature.CombatId == combatId)
              ?? throw new InvalidOperationException($"Target CombatId {combatId} is absent.");

    private static async Task<GameAction> EnqueueAndCaptureAsync(
        PreparedNativeAction prepared,
        CancellationToken cancellationToken)
    {
        TaskCompletionSource<GameAction> captured = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnBeforeActionExecuted(GameAction action)
        {
            bool matches = prepared.Command.Action.Kind switch
            {
                "PlayCard" => action is PlayCardAction play
                    && ReferenceEquals(play.NetCombatCard.ToCardModelOrNull(), prepared.Card),
                "EndTurn" => action is EndPlayerTurnAction
                    && action.OwnerId == prepared.Player.NetId,
                _ => false,
            };
            if (matches)
                captured.TrySetResult(action);
        }

        RunManager.Instance.ActionExecutor.BeforeActionExecuted += OnBeforeActionExecuted;
        try
        {
            if (prepared.Card is not null)
            {
                if (!prepared.Card.TryManualPlay(prepared.Target))
                    throw new InvalidOperationException(
                        $"Prepared card {prepared.Card.Id.Entry} lost playability before enqueue.");
            }
            else
            {
                CombatManager.Instance.OnEndedTurnLocally();
                RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(
                    new EndPlayerTurnAction(prepared.Player, prepared.Command.ExpectedTurn));
            }
            return await captured.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            RunManager.Instance.ActionExecutor.BeforeActionExecuted -= OnBeforeActionExecuted;
        }
    }

    private static LocalPrepareResult Reject(CoopActionRejectionCode code, string detail)
        => new(null, new ActionRejectedPayload(code, detail));

    private LocalPrepareResult RejectAndReset(CoopActionRejectionCode code, string detail)
    {
        _prepared = null;
        State = LocalActorAgentState.Idle;
        return Reject(code, detail);
    }
}
