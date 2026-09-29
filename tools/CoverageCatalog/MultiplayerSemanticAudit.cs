using System.Reflection;
using CombatSolver;
using CombatSolver.Engine.Common.Mirrors;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

internal static class MultiplayerSemanticAudit
{
    public static MultiplayerSemanticCoverageCatalog Build(
        string combatSolverVersion,
        string gameVersion)
    {
        Type[] discoveredCards = ModelDb.All
            .OfType<CardModel>()
            .Where(static card => card.GetType().Namespace == "MegaCrit.Sts2.Core.Models.Cards")
            .Where(static card => card.MultiplayerConstraint == CardMultiplayerConstraint.MultiplayerOnly)
            .Select(static card => card.GetType())
            .Distinct()
            .OrderBy(static type => type.FullName, StringComparer.Ordinal)
            .ToArray();

        MultiplayerCardSemanticDescriptor[] declared = MultiplayerSemanticCatalog.Cards
            .OrderBy(static entry => entry.CardType.FullName, StringComparer.Ordinal)
            .ToArray();
        HashSet<Type> discoveredSet = discoveredCards.ToHashSet();
        HashSet<Type> declaredSet = declared.Select(static entry => entry.CardType).ToHashSet();
        Type[] exactMirrors = ReadExplicitMultiplayerOnPlayMirrors();
        HashSet<Type> exactMirrorSet = exactMirrors.ToHashSet();

        MultiplayerCardCoverageEntry[] cards = declared.Select(entry => new MultiplayerCardCoverageEntry(
            entry.CardType.FullName ?? entry.CardType.Name,
            entry.Status.ToString(),
            entry.OnPlaySupport.ToString(),
            entry.PlannedStage,
            discoveredSet.Contains(entry.CardType),
            exactMirrorSet.Contains(entry.CardType)))
            .ToArray();

        MultiplayerPowerScalingEntry[] powerScaling = ModelDb.All
            .OfType<PowerModel>()
            .Where(static power => power.GetType().Namespace == "MegaCrit.Sts2.Core.Models.Powers")
            .DistinctBy(static power => power.GetType())
            .Select(static power => BuildPowerScalingEntry(power))
            .Where(static entry => entry.ShouldScale || entry.OverridesScaleFlag || entry.OverridesAmount)
            .OrderBy(static entry => entry.Type, StringComparer.Ordinal)
            .ToArray();

        MultiplayerPotionTargetEntry[] crossPlayerPotions = ModelDb.All
            .OfType<PotionModel>()
            .Where(static potion => potion.GetType().Namespace == "MegaCrit.Sts2.Core.Models.Potions")
            .Where(static potion => potion.TargetType is TargetType.AnyPlayer or TargetType.AnyAlly or TargetType.AllAllies)
            .DistinctBy(static potion => potion.GetType())
            .Select(static potion => new MultiplayerPotionTargetEntry(
                potion.GetType().FullName ?? potion.GetType().Name,
                potion.TargetType.ToString()))
            .OrderBy(static entry => entry.Type, StringComparer.Ordinal)
            .ToArray();

        string[] missingCards = discoveredCards
            .Where(type => !declaredSet.Contains(type))
            .Select(static type => type.FullName ?? type.Name)
            .ToArray();
        string[] staleCards = declared
            .Where(entry => !discoveredSet.Contains(entry.CardType))
            .Select(static entry => entry.CardType.FullName ?? entry.CardType.Name)
            .ToArray();
        string[] exactMirrorMismatches = declared
            .Where(entry => (entry.OnPlaySupport == MultiplayerCardOnPlaySupportKind.ExactMirror)
                            != exactMirrorSet.Contains(entry.CardType))
            .Select(static entry => entry.CardType.FullName ?? entry.CardType.Name)
            .ToArray();

        return new MultiplayerSemanticCoverageCatalog(
            MultiplayerSemanticCatalog.SchemaVersion,
            combatSolverVersion,
            gameVersion,
            cards,
            powerScaling,
            crossPlayerPotions,
            missingCards,
            staleCards,
            exactMirrorMismatches);
    }

    private static Type[] ReadExplicitMultiplayerOnPlayMirrors()
    {
        Assembly solver = typeof(MultiplayerSemanticCatalog).Assembly;
        Type facade = solver.GetType(
            "CombatSolver.Engine.InCombat.Mirrors.Cards.OnPlay.CardOnPlayMirrors",
            throwOnError: true)!;
        object registry = facade.GetField("Registry", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null)
            ?? throw new InvalidOperationException("CardOnPlayMirrors.Registry was not found.");
        if (registry is not IMethodMirrorRegistryDescriptorProvider provider)
            throw new InvalidOperationException("CardOnPlay registry does not publish a descriptor.");
        return provider.DescribeMirrorSupport().Registrations
            .Where(static registration => registration.Kind == MethodMirrorRegistrationKind.Handled)
            .Select(static registration => registration.ReceiverType)
            .Where(type => MultiplayerSemanticCatalog.TryGetCard(type, out _))
            .OrderBy(static type => type.FullName, StringComparer.Ordinal)
            .ToArray();
    }

    private static MultiplayerPowerScalingEntry BuildPowerScalingEntry(PowerModel instance)
    {
        Type type = instance.GetType();
        PropertyInfo flagProperty = type.GetProperty(nameof(PowerModel.ShouldScaleInMultiplayer))
            ?? throw new InvalidOperationException($"{type.FullName} has no multiplayer scaling flag.");
        MethodInfo flagGetter = flagProperty.GetMethod
            ?? throw new InvalidOperationException($"{type.FullName} has no multiplayer scaling getter.");
        MethodInfo amountMethod = type.GetMethod(nameof(PowerModel.GetScaledAmountForMultiplayer))
            ?? throw new InvalidOperationException($"{type.FullName} has no multiplayer amount method.");
        return new MultiplayerPowerScalingEntry(
            type.FullName ?? type.Name,
            instance.ShouldScaleInMultiplayer,
            flagGetter.DeclaringType == type && flagGetter.DeclaringType != typeof(PowerModel),
            amountMethod.DeclaringType == type && amountMethod.DeclaringType != typeof(PowerModel));
    }
}

internal sealed record MultiplayerSemanticCoverageCatalog(
    int SchemaVersion,
    string CombatSolverVersion,
    string GameVersion,
    IReadOnlyList<MultiplayerCardCoverageEntry> Cards,
    IReadOnlyList<MultiplayerPowerScalingEntry> PowerScaling,
    IReadOnlyList<MultiplayerPotionTargetEntry> CrossPlayerPotions,
    IReadOnlyList<string> MissingCards,
    IReadOnlyList<string> StaleCards,
    IReadOnlyList<string> ExactMirrorMismatches)
{
    public bool IsCurrent =>
        Cards.Count == 37
        && MissingCards.Count == 0
        && StaleCards.Count == 0
        && ExactMirrorMismatches.Count == 0;
}

internal sealed record MultiplayerCardCoverageEntry(
    string Type,
    string Status,
    string OnPlaySupport,
    string PlannedStage,
    bool DiscoveredInGame,
    bool HasExactMirror);

internal sealed record MultiplayerPowerScalingEntry(
    string Type,
    bool ShouldScale,
    bool OverridesScaleFlag,
    bool OverridesAmount);

internal sealed record MultiplayerPotionTargetEntry(string Type, string TargetType);
