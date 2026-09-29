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
        Verified<BelieveInYou>("M4"),
        Verified<Coordinate>("M5"),
        Verified<GangUp>("M4"),
        Verified<HuddleUp>("M6"),
        Verified<Intercept>("M5"),
        Verified<Lift>("M4"),
        Verified<TagTeam>("M5"),
        Verified<TheBall>("M6"),
        Verified<BeaconOfHope>("M5"),
        Verified<Knockdown>("M5"),
        Verified<Mimic>("M4"),
        Verified<Rally>("M4"),

        Verified<Blaze>("M4"),
        Verified<DemonicShield>("M4"),
        Verified<Outrage>("M6"),
        Verified<Midnight>("M5"),
        Verified<Tank>("M5"),

        Verified<BladeSymphony>("M6"),
        Verified<Concoct>("M5"),
        Verified<Fade>("M5"),
        Verified<Flanking>("M5"),
        Verified<Sneaky>("M5"),

        Verified<Constellation>("M4"),
        Verified<Largesse>("M6"),
        Verified<Plot>("M6"),
        Verified<HammerTime>("M5"),
        Verified<Tutor>("M6"),

        Missing<LegionOfBone>("M7"),
        Verified<Soulbound>("M5"),
        Verified<Underworld>("M5"),
        Verified<Cacophony>("M5"),
        Verified<GlimpseBeyond>("M6"),

        Verified<EnergySurge>("M4"),
        Missing<Hibernate>("M7"),
        Exact<Ignition>("M7"),
        Verified<ImitationLearning>("M6"),
        Verified<OneForAll>("M4"),
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

    private static MultiplayerCardSemanticDescriptor Verified<TCard>(string stage)
        where TCard : CardModel => new(
            typeof(TCard),
            MultiplayerSemanticSupportStatus.Verified,
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
