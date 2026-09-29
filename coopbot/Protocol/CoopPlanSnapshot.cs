using System.Security.Cryptography;
using System.Text;
using CombatSolver;

namespace CoopBot.Protocol;

public sealed record CoopPlanCardTokenSnapshot(
    string CardId,
    int UpgradeLevel,
    string StateKey,
    int SourceOccurrence,
    int OptionOccurrence,
    string Title);

public sealed record CoopPlanChoiceSnapshot(
    string Effect,
    string SourcePile,
    int ActorId,
    string SourceId,
    string ContextId,
    string Timing,
    IReadOnlyList<CoopPlanCardTokenSnapshot> Cards);

public sealed record CoopPlanActionSnapshot(
    int Index,
    int ActorId,
    string Kind,
    int Turn,
    string CardId,
    int CardOccurrence,
    int TargetIndex,
    uint? TargetCombatId,
    string CardTitle,
    string TargetName,
    int PotionSlot,
    string PotionId,
    string PotionTitle,
    int ReplayCount,
    string CardStateKey,
    int CardStateOccurrence,
    bool EndsPlayerTurn,
    int CardUpgradeLevel,
    string CardEnchantmentId,
    CoopPlanChoiceSnapshot? Choice,
    IReadOnlyList<CoopPlanChoiceSnapshot> NestedChoices,
    int NestedChoicesBeforePrimary,
    IReadOnlyList<CoopPlanChoiceSnapshot> TurnStartChoices);

public sealed record CoopPlanActorResultSnapshot(
    int ActorId,
    int Turn,
    string Phase,
    int Hp,
    int MaxHp,
    int HpLost,
    int Block,
    int Energy,
    int Stars,
    int Gold,
    int HandCount,
    int DrawCount,
    int DiscardCount,
    int ExhaustCount,
    int PlayCount);

public sealed record CoopPlanCheckpointSnapshot(
    int AppliedActionCount,
    string Stage,
    int Turn,
    IReadOnlyList<string> ActorPhases,
    string StateKey,
    string ContinuationFingerprint);

public sealed record CoopPlanScoreSnapshot(
    string? Outcome,
    int DeadActorCount,
    int TotalHpLost,
    IReadOnlyList<int> HpLostByActor,
    int PotionStrategicCost,
    int PotionUses,
    int DeathSaveUses,
    int GrowthRewards,
    int LongTermResourceValue,
    int OutstandingStolenResource,
    int Turns,
    int Actions);

public sealed record PlanPublishedPayload(
    string PlanId,
    long RootRevision,
    string RootFingerprint,
    string TerminalStateKey,
    string Termination,
    int ExpandedStates,
    CoopPlanScoreSnapshot Score,
    IReadOnlyList<CoopPlanActionSnapshot> Actions,
    IReadOnlyList<CoopPlanActorResultSnapshot> Actors,
    IReadOnlyList<CoopPlanCheckpointSnapshot> Checkpoints);

internal static class CoopPlanSnapshotFactory
{
    internal static PlanPublishedPayload Create(
        long rootRevision,
        string rootFingerprint,
        CombatRootSnapshot root,
        JointOfflineSearchResult searched,
        JointReplayResult replay)
    {
        CoopPlanActionSnapshot[] actions = searched.Actions
            .Select((action, index) => CaptureAction(action, index))
            .ToArray();
        CoopPlanActorResultSnapshot[] actors = searched.Snapshot.Actors
            .Select(actor => new CoopPlanActorResultSnapshot(
                actor.Id.Index,
                actor.Turn,
                actor.Phase.ToString(),
                actor.Hp,
                actor.MaxHp,
                Math.Max(0, root.Actors[actor.Id.Index].InitialHp - actor.Hp),
                actor.Block,
                actor.Energy,
                actor.Stars,
                actor.Gold,
                actor.HandCount,
                actor.DrawCount,
                actor.DiscardCount,
                actor.ExhaustCount,
                actor.PlayCount))
            .ToArray();
        CoopPlanCheckpointSnapshot[] checkpoints = replay.Checkpoints
            .Select(checkpoint => new CoopPlanCheckpointSnapshot(
                checkpoint.AppliedActionCount,
                checkpoint.Stage,
                checkpoint.Turn,
                checkpoint.Phases.Select(static phase => phase.ToString()).ToArray(),
                FormatKey(checkpoint.StateKey),
                Fingerprint(checkpoint.Continuation.StateText)))
            .ToArray();
        JointObjectiveScore score = searched.Score;
        CoopPlanScoreSnapshot publishedScore = new(
            score.Outcome?.ToString(),
            score.DeadActorCount,
            score.TotalHpLost,
            score.HpLostByActor.ToArray(),
            score.PotionStrategicCost,
            score.PotionUses,
            score.DeathSaveUses,
            score.GrowthRewards,
            score.LongTermResourceValue,
            score.OutstandingStolenResource,
            score.Turns,
            score.Actions);
        string terminalStateKey = FormatKey(searched.Snapshot.StateKey);
        string canonical = string.Join('|',
            rootRevision,
            rootFingerprint,
            terminalStateKey,
            searched.Termination,
            System.Text.Json.JsonSerializer.Serialize(actions, CoopProtocol.JsonOptions));
        string planId = Fingerprint(canonical);
        return new PlanPublishedPayload(
            planId,
            rootRevision,
            rootFingerprint,
            terminalStateKey,
            searched.Termination.ToString(),
            searched.ExpandedStates,
            publishedScore,
            Array.AsReadOnly(actions),
            Array.AsReadOnly(actors),
            Array.AsReadOnly(checkpoints));
    }

    internal static CoopPlanActionSnapshot CaptureAction(PlanAction action, int index)
        => new(
            index,
            action.Actor.Index,
            action.Kind.ToString(),
            action.Turn,
            action.CardId,
            action.CardOccurrence,
            action.TargetIndex,
            action.TargetCombatId,
            action.CardTitle,
            action.TargetName,
            action.PotionSlot,
            action.PotionId,
            action.PotionTitle,
            action.ReplayCount,
            action.CardStateKey,
            action.CardStateOccurrence,
            action.EndsPlayerTurn,
            action.CardUpgradeLevel,
            action.CardEnchantmentId,
            CaptureChoice(action.Choice),
            action.NestedChoices?.Select(CaptureChoiceRequired).ToArray() ?? [],
            action.NestedChoicesBeforePrimary,
            action.TurnStartChoices?.Select(CaptureChoiceRequired).ToArray() ?? []);

    private static CoopPlanChoiceSnapshot? CaptureChoice(PlanCardChoice? choice)
        => choice is null ? null : CaptureChoiceRequired(choice);

    private static CoopPlanChoiceSnapshot CaptureChoiceRequired(PlanCardChoice choice)
        => new(
            choice.Effect.ToString(),
            choice.SourcePile.ToString(),
            choice.Actor.Index,
            choice.SourceId,
            choice.ContextId,
            choice.Timing.ToString(),
            choice.Cards.Select(card => new CoopPlanCardTokenSnapshot(
                card.CardId,
                card.UpgradeLevel,
                card.StateKey,
                card.SourceOccurrence,
                card.OptionOccurrence,
                card.Title)).ToArray());

    private static string FormatKey(StateFingerprint key)
        => $"{key.First:X16}{key.Second:X16}";

    private static string Fingerprint(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
