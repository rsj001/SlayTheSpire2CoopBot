using System.Reflection;
using CombatSolver;
using CombatSolver.Engine.Common.Mirrors;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;

internal static class MultiplayerSemanticAudit
{
    private static readonly string[] VerifiedPowerScalingTypes =
    [
        "MegaCrit.Sts2.Core.Models.Powers.ArtifactPower",
        "MegaCrit.Sts2.Core.Models.Powers.BufferPower",
        "MegaCrit.Sts2.Core.Models.Powers.CurlUpPower",
        "MegaCrit.Sts2.Core.Models.Powers.FlutterPower",
        "MegaCrit.Sts2.Core.Models.Powers.HardenedShellPower",
        "MegaCrit.Sts2.Core.Models.Powers.PlatingPower",
        "MegaCrit.Sts2.Core.Models.Powers.PlowPower",
        "MegaCrit.Sts2.Core.Models.Powers.RampartPower",
        "MegaCrit.Sts2.Core.Models.Powers.ReattachPower",
        "MegaCrit.Sts2.Core.Models.Powers.RegenPower",
        "MegaCrit.Sts2.Core.Models.Powers.ShriekPower",
        "MegaCrit.Sts2.Core.Models.Powers.SkittishPower",
        "MegaCrit.Sts2.Core.Models.Powers.SlipperyPower",
    ];

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
        string[] discoveredScalingTypes = powerScaling.Select(static entry => entry.Type).ToArray();
        string[] missingPowerScalingTypes = VerifiedPowerScalingTypes
            .Except(discoveredScalingTypes, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] unverifiedPowerScalingTypes = discoveredScalingTypes
            .Except(VerifiedPowerScalingTypes, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
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
        MultiplayerMonsterMoveScopeEntry[] monsterMoveScopes = BuildMonsterMoveScopes();

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
            monsterMoveScopes,
            missingCards,
            staleCards,
            exactMirrorMismatches,
            missingPowerScalingTypes,
            unverifiedPowerScalingTypes);
    }

    private static MultiplayerMonsterMoveScopeEntry[] BuildMonsterMoveScopes()
    {
        Assembly solver = typeof(MultiplayerSemanticCatalog).Assembly;
        Type effects = solver.GetType("CombatSolver.MonsterMoveEffects", throwOnError: true)!;
        Type semantics = solver.GetType("CombatSolver.MonsterMoveSemantics", throwOnError: true)!;
        MethodInfo supports = effects.GetMethod(
            "Supports",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("MonsterMoveEffects.Supports was not found.");
        MethodInfo describe = semantics.GetMethod(
            "DescribeJointEffectScope",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("MonsterMoveSemantics scope descriptor was not found.");
        List<MultiplayerMonsterMoveScopeEntry> entries = [];
        foreach (MonsterModel canonical in ModelDb.All
                     .OfType<MonsterModel>()
                     .Where(static monster => monster.GetType().Namespace == "MegaCrit.Sts2.Core.Models.Monsters")
                     .DistinctBy(static monster => monster.GetType())
                     .OrderBy(static monster => monster.GetType().FullName, StringComparer.Ordinal))
        {
            MonsterModel monster = canonical.ToMutable();
            MethodInfo generate = monster.GetType().GetMethod(
                "GenerateMoveStateMachine",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException($"{monster.GetType().FullName} has no move generator.");
            MonsterMoveStateMachine machine;
            try
            {
                machine = (MonsterMoveStateMachine)(generate.Invoke(monster, null)
                    ?? throw new InvalidOperationException("Move generator returned null."));
            }
            catch (TargetInvocationException exception)
            {
                throw new InvalidOperationException(
                    $"Could not enumerate multiplayer move scopes for {monster.GetType().FullName}.",
                    exception.InnerException ?? exception);
            }
            foreach (MoveState move in machine.States.Values.OfType<MoveState>()
                         .Distinct()
                         .OrderBy(static move => move.Id, StringComparer.Ordinal))
            {
                if (!(bool)(supports.Invoke(null, [monster, move.Id]) ?? false))
                    continue;
                string scope;
                try
                {
                    scope = (string)(describe.Invoke(null, [monster, move.Id])
                        ?? throw new InvalidOperationException("Scope descriptor returned null."));
                }
                catch (TargetInvocationException exception)
                {
                    throw new InvalidOperationException(
                        $"Could not classify {monster.GetType().Name}/{move.Id}.",
                        exception.InnerException ?? exception);
                }
                entries.Add(new MultiplayerMonsterMoveScopeEntry(
                    monster.GetType().FullName ?? monster.GetType().Name,
                    move.Id,
                    scope));
            }
        }
        return entries.ToArray();
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
    IReadOnlyList<MultiplayerMonsterMoveScopeEntry> MonsterMoveScopes,
    IReadOnlyList<string> MissingCards,
    IReadOnlyList<string> StaleCards,
    IReadOnlyList<string> ExactMirrorMismatches,
    IReadOnlyList<string> MissingPowerScalingTypes,
    IReadOnlyList<string> UnverifiedPowerScalingTypes)
{
    public bool IsCurrent =>
        Cards.Count == 37
        && MissingCards.Count == 0
        && StaleCards.Count == 0
        && ExactMirrorMismatches.Count == 0
        && MonsterMoveScopes.Count > 0
        && MonsterMoveScopes.All(static entry => entry.Scope is
            "OwnerOnly" or "TargetOnly" or "PostAttackMixed" or "PreAttackMixed")
        && MissingPowerScalingTypes.Count == 0
        && UnverifiedPowerScalingTypes.Count == 0;
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

internal sealed record MultiplayerMonsterMoveScopeEntry(string Type, string MoveId, string Scope);
