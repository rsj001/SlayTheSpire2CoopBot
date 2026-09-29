using CombatSolver;
using MegaCrit.Sts2.Core.Combat;

namespace CoopBot.Capture;

internal sealed record HostVisibilityAuditResult(
    bool ActorVitalsAndResources,
    bool OrderedCardInstances,
    bool PowersAndOwnership,
    bool PotionsRelicsOrbsAndPets,
    bool MonsterStateAndIntent,
    bool NineCombatRngStreams,
    bool TurnPhaseAndTerminal,
    IReadOnlyList<string> Failures)
{
    public bool IsComplete
        => ActorVitalsAndResources
           && OrderedCardInstances
           && PowersAndOwnership
           && PotionsRelicsOrbsAndPets
           && MonsterStateAndIntent
           && NineCombatRngStreams
           && TurnPhaseAndTerminal
           && Failures.Count == 0;
}

internal static class HostVisibilityAudit
{
    internal static HostVisibilityAuditResult Inspect(
        CombatState live,
        CombatRootSnapshot root)
    {
        List<string> failures = [];
        string stamp = root.ContinuationStamp.StateText;
        int actorCount = live.Players.Count;

        bool actorVitals = root.Actors.Count == actorCount
            && live.Players.All(player => player.PlayerCombatState != null)
            && (actorCount == 1 || stamp.Contains($";actor_count={actorCount}", StringComparison.Ordinal));
        if (!actorVitals) failures.Add("actor_vitals_or_resources");

        bool cards = live.Players.All(player => player.PlayerCombatState is { } pcs
            && pcs.PlayPile.Cards.Count == 0
            && pcs.AllCards.All(card => card.Owner == player))
            && Enumerable.Range(0, actorCount).All(actor =>
                HasActorField(stamp, actor, "H=", actorCount)
                && HasActorField(stamp, actor, "D=", actorCount)
                && HasActorField(stamp, actor, "C=", actorCount)
                && HasActorField(stamp, actor, "X=", actorCount)
                && HasActorField(stamp, actor, "P=", actorCount));
        if (!cards) failures.Add("ordered_card_instances_or_play_pile_not_empty");

        bool powers = stamp.Contains(";P=", StringComparison.Ordinal)
            && (actorCount == 1
                || stamp.Contains(";multiplayer_power_identities=", StringComparison.Ordinal));
        if (!powers) failures.Add("powers_or_applier_target_identity");

        bool actorModels = Enumerable.Range(0, actorCount).All(actor =>
            HasActorField(stamp, actor, "potions=", actorCount)
            && HasActorField(stamp, actor, "O=", actorCount)
            && HasActorField(stamp, actor, "osty=", actorCount));
        if (!actorModels) failures.Add("potions_relics_orbs_pets");

        bool monsters = root.Enemies.Count == live.Enemies.Count
            && (live.Enemies.Count == 0 || stamp.Contains(";E0=", StringComparison.Ordinal));
        if (!monsters) failures.Add("monster_roster_intent_or_private_state");

        bool rng = TryCountRngStreams(stamp, out int rngStreams) && rngStreams == 9;
        if (!rng) failures.Add($"combat_rng_streams={rngStreams}");

        bool turns = root.CurrentSide == live.CurrentSide
            && root.Actors.Select(actor => actor.Phase)
                .SequenceEqual(live.Players.Select(player => player.PlayerCombatState!.Phase))
            && root.Actors.Select(actor => actor.IsReadyToEndTurn)
                .SequenceEqual(live.Players.Select(CombatManager.Instance.IsPlayerReadyToEndTurn));
        if (!turns) failures.Add("turn_phase_or_terminal");

        return new HostVisibilityAuditResult(
            actorVitals,
            cards,
            powers,
            actorModels,
            monsters,
            rng,
            turns,
            failures);
    }

    private static bool HasActorField(string stamp, int actor, string field, int actorCount)
        => actorCount == 1
            ? stamp.Contains($";{field}", StringComparison.Ordinal)
            : stamp.Contains($";A{actor}.{field}", StringComparison.Ordinal);

    private static bool TryCountRngStreams(string stamp, out int count)
    {
        const string marker = ";R=";
        int start = stamp.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            count = 0;
            return false;
        }
        start += marker.Length;
        int end = stamp.IndexOf(';', start);
        string value = end < 0 ? stamp[start..] : stamp[start..end];
        count = value.Split('/').Length;
        return true;
    }
}
