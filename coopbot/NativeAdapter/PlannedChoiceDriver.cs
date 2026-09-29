using CombatSolver;
using CoopBot.Protocol;
using CoopBot.Session;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.TestSupport;

namespace CoopBot.NativeAdapter;

internal sealed class PlannedChoiceDriver : IDisposable
{
    private sealed class Selector(
        IReadOnlyList<CoopPlanChoiceSnapshot> choices,
        IReadOnlyDictionary<int, ulong> actorOwners) : ICardSelector
    {
        private int _next;

        public Task<IEnumerable<CardModel>> GetSelectedCards(
            IEnumerable<CardModel> options,
            int minSelect,
            int maxSelect)
        {
            if ((uint)_next >= (uint)choices.Count)
                throw new InvalidOperationException("Native action requested more card choices than the plan contains.");
            CoopPlanChoiceSnapshot choice = choices[_next++];
            List<CardModel> remaining = options.ToList();
            if (!actorOwners.TryGetValue(choice.ActorId, out ulong expectedOwner))
                throw new InvalidOperationException($"Planned choice refers to unknown Actor {choice.ActorId}.");
            if (remaining.Any(card => card.Owner.NetId != expectedOwner))
            {
                string actualOwners = string.Join(",", remaining.Select(card => card.Owner.NetId).Distinct().Order());
                throw new InvalidOperationException(
                    $"Choice Actor {choice.ActorId} belongs to network player {expectedOwner}, " +
                    $"but native options belong to [{actualOwners}].");
            }
            List<CardModel> selected = [];
            foreach (CoopPlanCardTokenSnapshot token in choice.Cards)
            {
                CardModel card = remaining
                    .Where(candidate => candidate.Id.Entry == token.CardId
                        && candidate.CurrentUpgradeLevel == token.UpgradeLevel
                        && (string.IsNullOrEmpty(token.StateKey)
                            || string.Equals(
                                CardChoiceSupport.ChoiceCardKey(candidate),
                                token.StateKey,
                                StringComparison.Ordinal)))
                    .Skip(token.OptionOccurrence)
                    .FirstOrDefault()
                    ?? throw new InvalidOperationException(
                        $"Planned choice cannot find {token.CardId}+{token.UpgradeLevel}#{token.OptionOccurrence}.");
                selected.Add(card);
                remaining.Remove(card);
            }
            if (selected.Count < minSelect || selected.Count > maxSelect)
                throw new InvalidOperationException(
                    $"Planned choice count {selected.Count} is outside [{minSelect},{maxSelect}].");
            return Task.FromResult<IEnumerable<CardModel>>(selected);
        }

        public CardRewardSelection GetSelectedCardReward(
            IReadOnlyList<CardCreationResult> options,
            IReadOnlyList<CardRewardAlternative> alternatives)
            => throw new InvalidOperationException("Combat plan selector cannot answer reward choices.");

        internal bool AllConsumed => _next == choices.Count;
    }

    private IDisposable? _scope;
    private Selector? _selector;
    private string? _actionId;
    private CoopPlanActionSnapshot? _action;
    private IReadOnlyDictionary<int, ulong>? _actorOwners;
    private string? _completedActionId;
    private Exception? _completionFailure;

    internal bool IsArmed => _scope is not null;

    internal void ConfigureActors(IEnumerable<ActorBinding> actors)
    {
        if (_scope is not null)
            throw new InvalidOperationException("Cannot replace ActorAssignment while a planned choice is armed.");
        Dictionary<int, ulong> configured = actors.ToDictionary(actor => actor.ActorId, actor => actor.NetworkPlayerId);
        if (configured.Count == 0)
            throw new InvalidOperationException("Planned choice driver requires a non-empty ActorAssignment.");
        _actorOwners = configured;
    }

    internal void Arm(string actionId, CoopPlanActionSnapshot action)
    {
        CoopPlanChoiceSnapshot[] choices = EnumerateChoices(action).ToArray();
        if (choices.Length == 0)
            return;
        if (_scope is not null)
        {
            if (string.Equals(_actionId, actionId, StringComparison.Ordinal))
                return;
            throw new InvalidOperationException($"Choice driver is already armed for {_actionId}.");
        }
        IReadOnlyDictionary<int, ulong> actorOwners = _actorOwners
            ?? throw new InvalidOperationException("Choice driver has no ActorAssignment.");
        _selector = new Selector(choices, actorOwners);
        _scope = CardSelectCmd.UseSelector(_selector);
        _actionId = actionId;
        _action = action;
        _completedActionId = null;
        _completionFailure = null;
        RunManager.Instance.ActionExecutor.AfterActionExecuted += OnAfterActionExecuted;
    }

    internal void Complete(string actionId)
    {
        if (_scope is not null && string.Equals(_actionId, actionId, StringComparison.Ordinal))
            Release(requireAllConsumed: true);
        if (!string.Equals(_completedActionId, actionId, StringComparison.Ordinal))
            return;
        Exception? failure = _completionFailure;
        _completedActionId = null;
        _completionFailure = null;
        if (failure is not null)
            throw new InvalidOperationException($"Native choices for {actionId} did not match the plan.", failure);
    }

    internal void Cancel(string actionId)
    {
        if (_scope is null || !string.Equals(_actionId, actionId, StringComparison.Ordinal))
            return;
        Release(requireAllConsumed: false);
    }

    public void Dispose()
        => Release(requireAllConsumed: false);

    private void OnAfterActionExecuted(GameAction action)
    {
        if (!MatchesPlannedAction(action))
            return;
        Release(requireAllConsumed: true);
    }

    private void Release(bool requireAllConsumed)
    {
        if (_scope is null) return;
        RunManager.Instance.ActionExecutor.AfterActionExecuted -= OnAfterActionExecuted;
        Selector selector = _selector!;
        _scope.Dispose();
        _scope = null;
        _selector = null;
        string actionId = _actionId!;
        _actionId = null;
        _action = null;
        _completedActionId = requireAllConsumed ? actionId : null;
        _completionFailure = requireAllConsumed && !selector.AllConsumed
            ? new InvalidOperationException($"Native action {actionId} did not consume every planned choice.")
            : null;
    }

    private bool MatchesPlannedAction(GameAction native)
    {
        CoopPlanActionSnapshot planned = _action
            ?? throw new InvalidOperationException("Armed choice driver has no planned action.");
        ulong expectedOwner = _actorOwners![planned.ActorId];
        return planned.Kind switch
        {
            "PlayCard" => native is PlayCardAction play
                && native.OwnerId == expectedOwner
                && play.NetCombatCard.ToCardModelOrNull() is CardModel card
                && card.Id.Entry == planned.CardId
                && card.CurrentUpgradeLevel == planned.CardUpgradeLevel
                && (string.IsNullOrEmpty(planned.CardStateKey)
                    || string.Equals(CardChoiceSupport.ChoiceCardKey(card), planned.CardStateKey, StringComparison.Ordinal)),
            "UsePotion" => native is UsePotionAction potion
                && potion.Player.NetId == expectedOwner
                && potion.PotionIndex == (uint)planned.PotionSlot,
            _ => native.OwnerId == expectedOwner,
        };
    }

    private static IEnumerable<CoopPlanChoiceSnapshot> EnumerateChoices(CoopPlanActionSnapshot action)
    {
        for (int index = 0; index < action.NestedChoicesBeforePrimary; index++)
            yield return action.NestedChoices[index];
        if (action.Choice is not null)
            yield return action.Choice;
        for (int index = action.NestedChoicesBeforePrimary; index < action.NestedChoices.Count; index++)
            yield return action.NestedChoices[index];
        foreach (CoopPlanChoiceSnapshot choice in action.TurnStartChoices)
            yield return choice;
    }
}
