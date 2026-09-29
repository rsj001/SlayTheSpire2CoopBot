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
        Exact<Coordinate>("M5"),
        Verified<GangUp>("M4"),
        Exact<HuddleUp>("M6"),
        Exact<Intercept>("M5"),
        Verified<Lift>("M4"),
        Exact<TagTeam>("M5"),
        Partial<TheBall>("M6"),
        Exact<BeaconOfHope>("M5"),
        Exact<Knockdown>("M5"),
        Verified<Mimic>("M4"),
        Verified<Rally>("M4"),

        Verified<Blaze>("M4"),
        Verified<DemonicShield>("M4"),
        Partial<Outrage>("M6"),
        Exact<Midnight>("M5"),
        Exact<Tank>("M5"),

        Missing<BladeSymphony>("M6"),
        Exact<Concoct>("M5"),
        Exact<Fade>("M5"),
        Exact<Flanking>("M5"),
        Exact<Sneaky>("M5"),

        Verified<Constellation>("M4"),
        Exact<Largesse>("M6"),
        Missing<Plot>("M6"),
        Exact<HammerTime>("M5"),
        Missing<Tutor>("M6"),

        Missing<LegionOfBone>("M7"),
        Exact<Soulbound>("M5"),
        Exact<Underworld>("M5"),
        Exact<Cacophony>("M5"),
        Missing<GlimpseBeyond>("M6"),

        Verified<EnergySurge>("M4"),
        Missing<Hibernate>("M7"),
        Exact<Ignition>("M7"),
        Missing<ImitationLearning>("M6"),
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
