using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;

namespace CombatSolver.Engine.InCombat.Mirrors.Cards.OnPlay;

internal static class BespokeCardMirrors
{
    private static readonly AccessTools.FieldRef<TheBall, decimal> TheBallExtraDamageFromPlays =
        AccessTools.FieldRefAccess<TheBall, decimal>("_extraDamageFromPlays");

    public static void BelieveInYouOnPlay(BelieveInYou card, CardOnPlayMirrorContext context)
        => context.Simulator.GainEnergy(context.TargetPlayer, card.DynamicVars.Energy.IntValue);

    public static void BlazeOnPlay(Blaze card, CardOnPlayMirrorContext context)
        => RequireCombat(context).ApplyPowerFromSource(
            typeof(StrengthPower),
            context.Target,
            card.DynamicVars.Strength.IntValue,
            card.Owner.Creature,
            card);

    public static void DemonicShieldOnPlay(DemonicShield card, CardOnPlayMirrorContext context)
    {
        decimal block = context.Calculate(card.DynamicVars.CalculatedBlock);
        context.Simulator.Damage(
            [card.Owner.Creature],
            card.DynamicVars.HpLoss.BaseValue,
            ValueProp.Unblockable | ValueProp.Unpowered | ValueProp.Move,
            card.Owner.Creature,
            context.Card,
            context.CardPlay);
        if (!context.Simulator.HasPendingChoice)
            context.GainBlock(context.Target, block, card.DynamicVars.CalculatedBlock.Props);
    }

    public static void EnergySurgeOnPlay(EnergySurge card, CardOnPlayMirrorContext context)
    {
        foreach (Creature ally in context.CombatState.GetTeammatesOf(card.Owner.Creature)
                     .Where(creature => creature.IsPlayer && context.State.GetCreature(creature).IsAlive))
        {
            context.Simulator.GainEnergy(ally.Player!, card.DynamicVars.Energy.IntValue);
        }
    }

    public static void MimicOnPlay(Mimic card, CardOnPlayMirrorContext context)
        => context.GainBlock(
            card.Owner.Creature,
            context.Calculate(card.DynamicVars.CalculatedBlock),
            card.DynamicVars.CalculatedBlock.Props);

    public static void CoordinateOnPlay(Coordinate card, CardOnPlayMirrorContext context)
        => RequireCombat(context).ApplyTemporaryStrengthGainFromSource<CoordinatePower>(
            context.Target,
            card.DynamicVars.Strength.IntValue,
            card.Owner.Creature,
            card);

    public static void FadeOnPlay(Fade card, CardOnPlayMirrorContext context)
        => RequireCombat(context).ApplyTemporaryDexterityFromSource<FadePower>(
            context.Target,
            card.DynamicVars.Dexterity.IntValue,
            card.Owner.Creature,
            card);

    public static void InterceptOnPlay(Intercept card, CardOnPlayMirrorContext context)
    {
        context.GainBlock(card.Owner.Creature);
        SimulatedCombatState combat = RequireCombat(context);
        combat.Apply<CoveredPower>(context.Target, 1, card.Owner.Creature);
        CoveredPower covered = combat.EffectivePowers().OfType<CoveredPower>()
            .Last(power => ReferenceEquals(power.Owner, context.Target)
                && ReferenceEquals(power.Applier, card.Owner.Creature));
        InterceptPower? intercept = combat.GetPower<InterceptPower>(card.Owner.Creature);
        if (intercept == null)
        {
            combat.Apply<InterceptPower>(card.Owner.Creature, 1, context.Target);
            intercept = combat.GetPower<InterceptPower>(card.Owner.Creature)
                ?? throw new InvalidOperationException("Intercept application did not create its owner power.");
        }
        InterceptPredictionState state = context.StateStore.Get(
            intercept,
            () => new InterceptPredictionState(intercept));
        if (!state.CoveredCreatures.Contains(context.Target))
            state.CoveredCreatures.Add(context.Target);
        _ = covered;
    }

    public static void TagTeamOnPlay(TagTeam card, CardOnPlayMirrorContext context)
    {
        context.AttackSingle();
        if (!context.Simulator.HasPendingChoice)
            ApplyPower<TagTeamPower>(card, context, context.Target, 1);
    }

    public static void TankOnPlay(Tank card, CardOnPlayMirrorContext context)
    {
        SimulatedCombatState combat = RequireCombat(context);
        combat.ApplyPowerFromSource(
            typeof(TankPower), card.Owner.Creature, 1, card.Owner.Creature, card);
        foreach (Creature teammate in context.CombatState.GetTeammatesOf(card.Owner.Creature)
                     .Where(creature => creature.IsPlayer
                         && !ReferenceEquals(creature, card.Owner.Creature)
                         && context.State.GetCreature(creature).IsAlive))
        {
            combat.Apply<GuardedPower>(teammate, 1, card.Owner.Creature);
        }
    }

    public static void KnockdownOnPlay(Knockdown card, CardOnPlayMirrorContext context)
    {
        context.AttackSingle();
        if (!context.Simulator.HasPendingChoice)
            ApplyPower<KnockdownPower>(card, context, context.Target,
                card.DynamicVars["KnockdownPower"].IntValue);
    }

    public static void SimpleSelfPowerOnPlay<TPower>(CardModel card, CardOnPlayMirrorContext context, int amount = 1)
        where TPower : PowerModel
        => ApplyPower<TPower>(card, context, card.Owner.Creature, amount);

    public static void SimpleTargetPowerOnPlay<TPower>(CardModel card, CardOnPlayMirrorContext context, int amount)
        where TPower : PowerModel
        => ApplyPower<TPower>(card, context, context.Target, amount);

    private static void ApplyPower<TPower>(
        CardModel card,
        CardOnPlayMirrorContext context,
        Creature target,
        int amount)
        where TPower : PowerModel
        => RequireCombat(context).ApplyPowerFromSource(
            typeof(TPower), target, amount, card.Owner.Creature, card);

    public static void OneForAllOnPlay(OneForAll card, CardOnPlayMirrorContext context)
    {
        if (context.CombatState is not ICombatPredictionEffectSink effects)
            throw new InvalidOperationException("万众一心效果缺少可写的预测状态。");
        int amount = card.DynamicVars["OneForAllPower"].IntValue;
        foreach (Player player in context.State.Players)
        {
            effects.ApplyPower(
                typeof(OneForAllPower),
                player.Creature,
                amount,
                card.Owner.Creature);
        }
    }

    public static void TheBallOnPlay(TheBall card, CardOnPlayMirrorContext context)
    {
        context.AttackSingle();
        if (context.Simulator.HasPendingChoice)
            return;
        decimal increase = card.DynamicVars["Increase"].BaseValue;
        card.DynamicVars.Damage.BaseValue += increase;
        TheBallExtraDamageFromPlays(card) += increase;
    }

    public static void OutrageOnPlay(Outrage card, CardOnPlayMirrorContext context)
    {
        context.AttackSingle();
        if (context.Simulator.HasPendingChoice)
            return;
        foreach (Creature teammate in context.CombatState.GetTeammatesOf(card.Owner.Creature)
                     .Where(creature => creature.IsPlayer && context.State.GetCreature(creature).IsAlive))
        {
            context.Simulator.AddGeneratedCardToCombat(
                context.Card.CreateCloneForPlayer(teammate.Player!),
                PileType.Discard,
                card.Owner,
                resultKind: CardGenerationResultKind.Fixed);
        }
    }

    public static void BladeSymphonyOnPlay(BladeSymphony card, CardOnPlayMirrorContext context)
    {
        foreach (Creature teammate in context.CombatState.GetTeammatesOf(card.Owner.Creature)
                     .Where(creature => creature.IsPlayer && context.State.GetCreature(creature).IsAlive))
        {
            context.Simulator.CreateAndAddGeneratedCardsToCombat<Shiv>(
                teammate.Player!, PileType.Hand, card.DynamicVars.Cards.IntValue, card.Owner);
        }
    }

    public static void PlotOnPlay(Plot card, CardOnPlayMirrorContext context)
    {
        SimulatedCombatState combat = RequireCombat(context);
        foreach (Creature teammate in context.CombatState.GetTeammatesOf(card.Owner.Creature)
                     .Where(creature => creature.IsPlayer && context.State.GetCreature(creature).IsAlive))
        {
            combat.ApplyPowerFromSource(
                typeof(DrawCardsNextTurnPower), teammate, card.DynamicVars.Cards.IntValue,
                card.Owner.Creature, card);
        }
    }

    public static void GlimpseBeyondOnPlay(GlimpseBeyond card, CardOnPlayMirrorContext context)
    {
        foreach (Creature teammate in context.CombatState.GetTeammatesOf(card.Owner.Creature)
                     .Where(creature => creature.IsPlayer && context.State.GetCreature(creature).IsAlive))
        {
            context.Simulator.CreateAndAddGeneratedCardsToCombat<Soul>(
                teammate.Player!, PileType.Draw, card.DynamicVars.Cards.IntValue, card.Owner,
                CardPilePosition.Random);
        }
    }

    public static void ImitationLearningOnPlay(ImitationLearning card, CardOnPlayMirrorContext context)
    {
        SimulatedCombatState combat = RequireCombat(context);
        ImitationLearningPower? existing = combat.EffectivePowers()
            .OfType<ImitationLearningPower>()
            .FirstOrDefault(power =>
                ReferenceEquals(power.Owner, card.Owner.Creature)
                && ReferenceEquals(power.PlayerTarget, context.TargetPlayer));
        if (existing is not null)
        {
            combat.SetPowerAmount(existing, existing.Amount + card.DynamicVars["ImitationLearningPower"].IntValue);
            return;
        }

        ImitationLearningPower created = combat.AddPowerInstance<ImitationLearningPower>(
            card.Owner.Creature,
            card.DynamicVars["ImitationLearningPower"].IntValue,
            card.Owner.Creature);
        created._target = null;
        created.PlayerTarget = context.TargetPlayer;
    }

    public static void TutorOnPlay(Tutor card, CardOnPlayMirrorContext context)
    {
        if (context.CombatState is not ICombatPredictionChoiceSink choices)
            throw new InvalidOperationException("Tutor requires multiplayer choice state.");
        _ = choices.ResolvePileChoice(
            context.Simulator,
            card.Id.Entry,
            context.TargetPlayer,
            PileType.Draw,
            1);
    }

    private static SimulatedCombatState RequireCombat(CardOnPlayMirrorContext context)
        => context.CombatState as SimulatedCombatState
           ?? throw new InvalidOperationException("Multiplayer card effect requires SimulatedCombatState.");

    public static void AstralPulseOnPlay(AstralPulse _, CardOnPlayMirrorContext context)
        => context.AttackAllOpponents(hitCount: 2);

    public static void DaggerSprayOnPlay(DaggerSpray _, CardOnPlayMirrorContext context)
        => context.AttackAllOpponents(hitCount: 2);

    // Vanilla wraps the whole body in Osty.CheckMissingWithAnim, so the attack and the block are both
    // skipped once the Osty is gone. The sacrifice stays in CardEffectSpecRegistry, which runs after
    // this mirror and is gated on the same condition.
    public static void BoneShardsOnPlay(BoneShards card, CardOnPlayMirrorContext context)
    {
        if (context.State.GetOsty(card.Owner) is not { } osty || context.State.GetCreature(osty).IsDead)
        {
            return;
        }

        DamageCmd.Attack(card.DynamicVars.OstyDamage.BaseValue)
            .FromOsty(osty, card, context.CardPlay)
            .TargetingAllOpponents(context.CombatState)
            .Simulate(context.Simulator);

        if (context.Simulator.HasPendingChoice)
            return;

        context.GainBlock(card.Owner.Creature);
    }

    public static void PactsEndOnPlay(PactsEnd card, CardOnPlayMirrorContext context)
    {
        if (context.OwnerState.ExhaustPile.Cards.Count >= card.DynamicVars.Cards.IntValue)
            context.AttackAllOpponents();
    }

    public static void TwinStrikeOnPlay(TwinStrike _, CardOnPlayMirrorContext context)
        => context.AttackSingle(hitCount: 2);

    public static void HeavenlyDrillOnPlay(HeavenlyDrill card, CardOnPlayMirrorContext context)
    {
        int hits = context.Card.ResolveEnergyXValue(context.State);
        if (hits >= card.DynamicVars.Energy.IntValue)
            hits *= 2;
        context.AttackSingle(hitCount: hits);
    }

    public static void FiendFireOnPlay(FiendFire card, CardOnPlayMirrorContext context)
    {
        PredictedCard[] hand = context.OwnerState.Hand.Cards.ToArray();
        foreach (PredictedCard candidate in hand)
        {
            context.Simulator.Exhaust(candidate);
            if (context.Simulator.HasPendingChoice)
                return;
        }
        DamageCmd.Attack(card.DynamicVars.Damage.BaseValue)
            .WithHitCount(hand.Length)
            .FromCard(card, context.CardPlay)
            .Targeting(context.Target)
            .Simulate(context.Simulator);
    }

    public static void DismantleOnPlay(Dismantle card, CardOnPlayMirrorContext context)
    {
        int hitCount = GetPowerAmount<VulnerablePower>(context, context.Target) > 0 ? 2 : 1;
        DamageCmd.Attack(card.DynamicVars.Damage.BaseValue)
            .WithHitCount(hitCount)
            .FromCard(card, context.CardPlay)
            .Targeting(context.Target)
            .Simulate(context.Simulator);
    }

    public static void EntrenchOnPlay(Entrench card, CardOnPlayMirrorContext context)
    {
        int currentBlock = context.State.GetCreature(card.Owner.Creature).Block;
        context.GainBlock(
            card.Owner.Creature,
            currentBlock,
            MegaCrit.Sts2.Core.ValueProps.ValueProp.Unpowered
            | MegaCrit.Sts2.Core.ValueProps.ValueProp.Move);
    }

    public static void LeadingStrikeOnPlay(LeadingStrike card, CardOnPlayMirrorContext context)
    {
        DamageCmd.Attack(card.DynamicVars.Damage.BaseValue)
            .FromCard(card, context.CardPlay)
            .Targeting(context.Target)
            .Simulate(context.Simulator);
        if (context.Simulator.HasPendingChoice)
            return;
        context.Simulator.CreateAndAddGeneratedCardsToCombat<Shiv>(
            card.Owner,
            PileType.Hand,
            card.DynamicVars["Shivs"].IntValue,
            card.Owner);
    }

    public static void MaulOnPlay(Maul card, CardOnPlayMirrorContext context)
    {
        DamageCmd.Attack(card.DynamicVars.Damage.BaseValue)
            .WithHitCount(2)
            .FromCard(card, context.CardPlay)
            .Targeting(context.Target)
            .Simulate(context.Simulator);
        if (context.Simulator.HasPendingChoice)
            return;
        decimal increase = card.DynamicVars["Increase"].BaseValue;
        foreach (PredictedCard candidate in context.OwnerState.AllCards
                     .Where(candidate => candidate.Preview is Maul)
                     .ToArray())
        {
            Maul mutable = (Maul)candidate.MutablePreview;
            mutable.DynamicVars.Damage.BaseValue += increase;
            mutable._extraDamageFromMaulPlays += increase;
        }
    }

    public static void SpiteOnPlay(Spite card, CardOnPlayMirrorContext context)
    {
        SimulatedCombatState combat = context.CombatState as SimulatedCombatState
            ?? throw new InvalidOperationException("恶意缺少分支回合受伤状态。");
        int hitCount = 1;
        if (combat.HasLostHpThisTurn(card.Owner.Creature))
        {
            // Spite's RepeatVar is canonical (2, plus one per upgrade). A few live cards can
            // carry a stale DynamicVarSet after an in-run card-state rewrite; recover this
            // card-specific canonical value instead of failing the whole search.
            hitCount = card.DynamicVars.TryGetValue("Repeat", out DynamicVar? repeat)
                ? repeat.IntValue
                : 2 + card.CurrentUpgradeLevel;
        }
        DamageCmd.Attack(card.DynamicVars.Damage.BaseValue)
            .WithHitCount(hitCount)
            .FromCard(card, context.CardPlay)
            .Targeting(context.Target)
            .Simulate(context.Simulator);
    }

    public static void TheScytheOnPlay(TheScythe card, CardOnPlayMirrorContext context)
    {
        DamageCmd.Attack(card.DynamicVars.Damage.BaseValue)
            .FromCard(card, context.CardPlay)
            .Targeting(context.Target)
            .Simulate(context.Simulator);
        if (context.Simulator.HasPendingChoice)
            return;
        TheScythe mutable = (TheScythe)context.Card.MutablePreview;
        int increase = card.DynamicVars["Increase"].IntValue;
        mutable.IncreasedDamage += increase;
        mutable.CurrentDamage = 13 + mutable.IncreasedDamage;
        if (mutable.DeckVersion != null
            && context.CombatState is SimulatedCombatState combat)
        {
            combat.RecordLongTermResource(increase);
            combat.RecordGrowthReward(GrowthSource.TheScythe);
        }
    }

    public static void SacrificeOnPlay(Sacrifice card, CardOnPlayMirrorContext context)
    {
        if (context.State.GetOsty(card.Owner) is not { } osty || !context.State.GetCreature(osty).IsAlive)
            return;
        int block = context.State.GetCreature(osty).MaxHp * 3;
        context.Simulator.Kill(osty, force: true);
        if (context.Simulator.HasPendingChoice)
            return;
        context.Simulator.GainBlock(
            card.Owner.Creature,
            block,
            card.DynamicVars.CalculatedBlock.Props,
            context.Card,
            context.CardPlay);
    }

    public static void SecondWindOnPlay(SecondWind card, CardOnPlayMirrorContext context)
    {
        PredictedCard[] cards = context.OwnerState.Hand.Cards
            .Where(candidate => candidate.Preview.Type != CardType.Attack)
            .ToArray();
        foreach (PredictedCard candidate in cards)
        {
            context.Simulator.Exhaust(candidate);
            if (context.Simulator.HasPendingChoice)
                return;
            context.Simulator.GainBlock(
                card.Owner.Creature,
                card.DynamicVars.Block,
                context.Card,
                context.CardPlay);
            if (context.Simulator.HasPendingChoice)
                return;
        }
    }

    public static void SovereignBladeOnPlay(SovereignBlade card, CardOnPlayMirrorContext context)
    {
        bool allEnemies = GetPowerAmount<SeekingEdgePower>(context, card.Owner.Creature) > 0;
        var attack = DamageCmd.Attack(card.DynamicVars.Damage.BaseValue)
            .WithHitCount(card.DynamicVars.Repeat.IntValue)
            .FromCard(card, context.CardPlay);
        if (allEnemies)
            attack.TargetingAllOpponents(context.CombatState);
        else
            attack.Targeting(context.Target);
        attack.Simulate(context.Simulator);
        if (context.Simulator.HasPendingChoice)
            return;

        int parry = GetPowerAmount<ParryPower>(context, card.Owner.Creature);
        if (parry > 0)
        {
            context.Simulator.GainBlock(
                card.Owner.Creature,
                parry,
                card.DynamicVars.CalculatedBlock.Props,
                context.Card,
                context.CardPlay);
        }
    }

    private static int GetPowerAmount<TPower>(CardOnPlayMirrorContext context, Creature owner)
        where TPower : PowerModel
    {
        if (context.CombatState is ICombatPredictionHookListenerSource source)
        {
            return source.HookListeners.OfType<TPower>()
                .Where(power => power.Owner == owner)
                .Sum(power => power.Amount);
        }
        return owner.GetPowerAmount<TPower>();
    }
}
