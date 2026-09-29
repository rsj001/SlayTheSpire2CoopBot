using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using CombatSolver.Engine.Common;

namespace CombatSolver;

public enum MultiplayerSemanticSupportStatus
{
    Unsupported,
    UnderTest,
    Verified,
}

public enum MultiplayerCardOnPlaySupportKind
{
    ExactMirror,
    GenericCandidate,
    Partial,
    Missing,
}

public sealed record MultiplayerCardSemanticDescriptor(
    Type CardType,
    MultiplayerSemanticSupportStatus Status,
    MultiplayerCardOnPlaySupportKind OnPlaySupport,
    string PlannedStage);

/// <summary>
/// Versioned support inventory for base-game cards that are legal only in multiplayer.
/// </summary>
/// <remarks>
/// This catalog is a fail-closed capability boundary, not proof that a card is semantically correct. A card moves to
/// <see cref="MultiplayerSemanticSupportStatus.Verified"/> only after native multiplayer actual/simulated evidence.
/// </remarks>
public static class MultiplayerSemanticCatalog
{
    public const int SchemaVersion = 1;
    public const string GameVersion = "0.111.0";

    private static readonly MultiplayerCardSemanticDescriptor[] CardEntries =
    [
        Missing<BelieveInYou>("M4"),
        Missing<Coordinate>("M5"),
        Generic<GangUp>("M4"),
        Exact<HuddleUp>("M6"),
        Partial<Intercept>("M5"),
        Generic<Lift>("M4"),
        Generic<TagTeam>("M5"),
        Partial<TheBall>("M6"),
        Missing<BeaconOfHope>("M5"),
        Partial<Knockdown>("M5"),
        Generic<Mimic>("M4"),
        Generic<Rally>("M4"),

        Missing<Blaze>("M4"),
        Partial<DemonicShield>("M4"),
        Partial<Outrage>("M6"),
        Generic<Midnight>("M5"),
        Missing<Tank>("M5"),

        Missing<BladeSymphony>("M6"),
        Missing<Concoct>("M5"),
        Missing<Fade>("M5"),
        Missing<Flanking>("M5"),
        Missing<Sneaky>("M5"),

        Exact<Constellation>("M4"),
        Exact<Largesse>("M6"),
        Missing<Plot>("M6"),
        Missing<HammerTime>("M5"),
        Missing<Tutor>("M6"),

        Missing<LegionOfBone>("M7"),
        Missing<Soulbound>("M5"),
        Missing<Underworld>("M5"),
        Missing<Cacophony>("M5"),
        Missing<GlimpseBeyond>("M6"),

        Missing<EnergySurge>("M4"),
        Missing<Hibernate>("M7"),
        Exact<Ignition>("M7"),
        Missing<ImitationLearning>("M6"),
        Exact<OneForAll>("M4"),
    ];

    private static readonly IReadOnlyDictionary<Type, MultiplayerCardSemanticDescriptor> CardsByType =
        CardEntries.ToDictionary(static entry => entry.CardType);

    static MultiplayerSemanticCatalog()
    {
        if (CardsByType.Count != CardEntries.Length)
            throw new InvalidOperationException("Multiplayer semantic catalog contains duplicate card types.");
    }

    public static IReadOnlyList<MultiplayerCardSemanticDescriptor> Cards => CardEntries;

    public static bool TryGetCard(Type cardType, out MultiplayerCardSemanticDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(cardType);
        return CardsByType.TryGetValue(cardType, out descriptor!);
    }

    internal static void RequireExecutableOnPlay(Type cardType, bool hasExactMirror)
    {
        ArgumentNullException.ThrowIfNull(cardType);
        if (!CardsByType.TryGetValue(cardType, out MultiplayerCardSemanticDescriptor? descriptor))
        {
            throw new PredictionUnsupportedException(
                $"Unregistered MultiplayerOnly card OnPlay: {cardType.FullName} " +
                $"(catalog schema {SchemaVersion}, game {GameVersion}).");
        }

        if (descriptor.Status == MultiplayerSemanticSupportStatus.Unsupported)
        {
            throw new PredictionUnsupportedException(
                $"Unsupported MultiplayerOnly card OnPlay: {cardType.FullName}; " +
                $"support={descriptor.OnPlaySupport}, plannedStage={descriptor.PlannedStage}.");
        }

        if (!hasExactMirror && descriptor.Status != MultiplayerSemanticSupportStatus.Verified)
        {
            throw new PredictionUnsupportedException(
                $"Unverified inferred MultiplayerOnly card OnPlay: {cardType.FullName}; " +
                $"support={descriptor.OnPlaySupport}, status={descriptor.Status}, " +
                $"plannedStage={descriptor.PlannedStage}.");
        }
    }

    private static MultiplayerCardSemanticDescriptor Exact<TCard>(string stage)
        where TCard : CardModel => new(
            typeof(TCard),
            MultiplayerSemanticSupportStatus.UnderTest,
            MultiplayerCardOnPlaySupportKind.ExactMirror,
            stage);

    private static MultiplayerCardSemanticDescriptor Generic<TCard>(string stage)
        where TCard : CardModel => new(
            typeof(TCard),
            MultiplayerSemanticSupportStatus.UnderTest,
            MultiplayerCardOnPlaySupportKind.GenericCandidate,
            stage);

    private static MultiplayerCardSemanticDescriptor Partial<TCard>(string stage)
        where TCard : CardModel => new(
            typeof(TCard),
            MultiplayerSemanticSupportStatus.UnderTest,
            MultiplayerCardOnPlaySupportKind.Partial,
            stage);

    private static MultiplayerCardSemanticDescriptor Missing<TCard>(string stage)
        where TCard : CardModel => new(
            typeof(TCard),
            MultiplayerSemanticSupportStatus.Unsupported,
            MultiplayerCardOnPlaySupportKind.Missing,
            stage);
}
