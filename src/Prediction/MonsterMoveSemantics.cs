using MegaCrit.Sts2.Core.Commands.Builders;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal static class MonsterMoveSemantics
{
    private enum JointMoveEffectScope
    {
        None,
        OwnerOnly,
        TargetOnly,
        PostAttackMixed,
        PreAttackMixed,
    }

    internal static void ApplyBasicForecastMoveToPlayers(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        IReadOnlyList<Creature> players,
        ISet<uint> processedEnemyDeaths)
    {
        MonsterModel monster = move.Owner.Monster
            ?? throw new InvalidOperationException("预测行动所有者不是怪物。");
        JointMoveEffectScope scope = JointEffectScope(monster, move.Move.Id);
        if (scope == JointMoveEffectScope.OwnerOnly && move.AttackHits.Count == 0)
        {
            Creature? representative = players.FirstOrDefault(player =>
                simulator.State.GetCreature(player).IsAlive);
            if (representative != null)
                _ = ApplyForecastMove(
                    simulator,
                    combat,
                    move,
                    representative,
                    processedEnemyDeaths);
            return;
        }
        if (scope == JointMoveEffectScope.PostAttackMixed
            || scope == JointMoveEffectScope.OwnerOnly && move.AttackHits.Count > 0)
        {
            Creature? representative = null;
            foreach (Creature player in players)
            {
                if (!simulator.State.GetCreature(player).IsAlive)
                    continue;
                representative ??= player;
                _ = ApplyForecastMove(
                    simulator,
                    combat,
                    move,
                    player,
                    processedEnemyDeaths,
                    plannedChoices: null,
                    applyMoveEffect: false);
                if (scope == JointMoveEffectScope.PostAttackMixed)
                {
                    ApplyForecastMoveEffect(
                        simulator,
                        combat,
                        move,
                        player,
                        processedEnemyDeaths,
                        plannedChoices: null,
                        applyTargetPortion: true,
                        applyOwnerPortion: false);
                }
                if (simulator.HasPendingChoice || simulator.State.GetCreature(move.Owner).IsDead)
                    return;
            }
            if (representative != null)
                ApplyForecastMoveEffect(
                    simulator,
                    combat,
                    move,
                    representative,
                    processedEnemyDeaths,
                    plannedChoices: null,
                    applyTargetPortion: false,
                    applyOwnerPortion: true);
            return;
        }
        if (scope == JointMoveEffectScope.PreAttackMixed)
        {
            Creature? representative = players.FirstOrDefault(player =>
                simulator.State.GetCreature(player).IsAlive);
            if (representative == null)
                return;
            MonsterMoveEffects.ApplyBeforeAttack(simulator, combat, move, representative);
            foreach (Creature player in players)
            {
                if (!simulator.State.GetCreature(player).IsAlive)
                    continue;
                _ = ApplyForecastMove(
                    simulator,
                    combat,
                    move,
                    player,
                    processedEnemyDeaths,
                    plannedChoices: null,
                    applyBeforeAttack: false,
                    applyMoveEffect: false);
                if (simulator.HasPendingChoice || simulator.State.GetCreature(move.Owner).IsDead)
                    return;
            }
            ApplyForecastMoveEffect(
                simulator,
                combat,
                move,
                representative,
                processedEnemyDeaths,
                plannedChoices: null,
                applyTargetPortion: false,
                applyOwnerPortion: true);
            return;
        }
        bool targetOnly = scope == JointMoveEffectScope.TargetOnly;
        if (move.AttackHits.Count == 0 && !targetOnly
            || MonsterMoveEffects.Supports(monster, move.Move.Id) && !targetOnly)
        {
            throw new PredictionUnsupportedException(
                $"联合敌方轮尚未登记 move={move.Owner.Monster?.Id.Entry}/{move.Move.Id} 的多人后效。");
        }
        if (targetOnly && move.AttackHits.Count > 0)
        {
            Creature[] originalTargets = players
                .Where(player => simulator.State.GetCreature(player).IsAlive)
                .ToArray();
            foreach (Creature player in originalTargets)
            {
                _ = ApplyForecastMove(
                    simulator,
                    combat,
                    move,
                    player,
                    processedEnemyDeaths,
                    plannedChoices: null,
                    applyMoveEffect: false);
                if (simulator.HasPendingChoice || simulator.State.GetCreature(move.Owner).IsDead)
                    return;
            }
            if (originalTargets.All(player => simulator.State.GetCreature(player).IsDead))
                return;
            foreach (Creature player in originalTargets)
            {
                ApplyForecastMoveEffect(
                    simulator,
                    combat,
                    move,
                    player,
                    processedEnemyDeaths,
                    plannedChoices: null,
                    applyTargetPortion: true,
                    applyOwnerPortion: false);
                if (simulator.HasPendingChoice || simulator.State.GetCreature(move.Owner).IsDead)
                    return;
            }
            return;
        }
        foreach (Creature player in players)
        {
            if (simulator.State.GetCreature(player).IsAlive)
            {
                _ = ApplyForecastMove(simulator, combat, move, player, processedEnemyDeaths);
            }
            if (simulator.HasPendingChoice || simulator.State.GetCreature(move.Owner).IsDead)
                return;
        }
    }

    internal static string DescribeJointEffectScope(MonsterModel monster, string moveId)
    {
        if (!MonsterMoveEffects.Supports(monster, moveId))
            throw new PredictionUnsupportedException(
                $"怪物行动 {monster.Id.Entry}/{moveId} 尚未进入多人作用域目录。");
        return JointEffectScope(monster, moveId).ToString();
    }

    private static JointMoveEffectScope JointEffectScope(MonsterModel monster, string moveId)
    {
        (string Type, string Move) key = (monster.GetType().Name, moveId);
        if (!MonsterMoveEffects.Supports(monster, moveId))
            return JointMoveEffectScope.None;
        return key switch
        {
            ("LivingFog", "BLOAT_MOVE") => JointMoveEffectScope.PreAttackMixed,
            ("TestSubject", "BURNING_GROWL_MOVE") or
            ("LagavulinMatriarch", "SOUL_SIPHON_MOVE") or
            ("Wriggler", "WRIGGLE_MOVE") or
            ("TheLost", "DEBILITATING_SMOG") or
            ("SlimedBerserker", "LEECHING_HUG_MOVE") or
            ("TheForgotten", "MIASMA") or
            ("OwlMagistrate", "VERDICT") or
            ("Aeonglass", "INCREASING_INTENSITY_MOVE") or
            ("WaterfallGiant", "STOMP_MOVE") or
            ("GremlinMerc", "DOUBLE_SMASH_MOVE")
                => JointMoveEffectScope.PostAttackMixed,
            ("MagiKnight", "DAMPEN_MOVE") or
            ("KnowledgeDemon", "CURSE_OF_KNOWLEDGE_MOVE") or
            ("TestSubject", "SKULL_BASH_MOVE") or
            ("SludgeSpinner", "OIL_SPRAY_MOVE") or
            ("Flyconid", "VULNERABLE_SPORES_MOVE") or
            ("Flyconid", "FRAIL_SPORES_MOVE") or
            ("FrogKnight", "TONGUE_LASH") or
            ("GlobeHead", "SHOCKING_SLAP") or
            ("BowlbugSilk", "TOXIC_SPIT_MOVE") or
            ("HauntedShip", "HAUNT_MOVE") or
            ("HunterKiller", "TENDERIZING_GOOP_MOVE") or
            ("KinPriest", "ORB_OF_FRAILTY_MOVE") or
            ("KinPriest", "ORB_OF_WEAKNESS_MOVE") or
            ("LeafSlimeM", "STICKY_SHOT") or
            ("LeafSlimeS", "GOOP_MOVE") or
            ("Mawler", "ROAR_MOVE") or
            ("Myte", "TOXIC_MOVE") or
            ("Chomper", "SCREECH_MOVE") or
            ("MechaKnight", "FLAMETHROWER_MOVE") or
            ("PunchConstruct", "FAST_PUNCH_MOVE") or
            ("CorpseSlug", "GOOP_MOVE") or
            ("SoulFysh", "SCREAM_MOVE") or
            ("EyeWithTeeth", "DISTRACT_MOVE") or
            ("Ovicopter", "TENDERIZER_MOVE") or
            ("Stabbot", "STAB_MOVE") or
            ("ShrinkerBeetle", "SHRINKER_MOVE") or
            ("VineShambler", "GRASPING_VINES_MOVE") or
            ("SlitheringStrangler", "CONSTRICT") or
            ("SpectralKnight", "HEX") or
            ("SoulNexus", "DRAIN_LIFE_MOVE") or
            ("SlimedBerserker", "VOMIT_ICHOR_MOVE") or
            ("TerrorEel", "TERROR_MOVE") or
            ("TwigSlimeM", "STICKY_SHOT_MOVE") or
            ("PhrogParasite", "INFECT_MOVE") or
            ("Vantom", "DISMEMBER_MOVE") or
            ("CeremonialBeast", "BEAST_CRY_MOVE") or
            ("Queen", "PUPPET_STRINGS_MOVE") or
            ("Queen", "YOU_ARE_MINE_MOVE") or
            ("LouseProgenitor", "WEB_CANNON_MOVE") or
            ("Crusher", "BUG_STING_MOVE") or
            ("TrackerRubyRaider", "TRACK_MOVE") or
            ("Noisebot", "NOISE_MOVE") or
            ("SoulFysh", "BECKON_MOVE") or
            ("SoulFysh", "GAZE_MOVE") or
            ("Axebot", "HAMMER_UPPERCUT_MOVE") or
            ("FakeMerchantMonster", "THROW_RELIC_MOVE") or
            ("FossilStalker", "TACKLE_MOVE") or
            ("DecimillipedeSegmentBack", "CONSTRICT_MOVE") or
            ("DecimillipedeSegmentFront", "CONSTRICT_MOVE") or
            ("DecimillipedeSegmentMiddle", "CONSTRICT_MOVE") or
            ("LivingFog", "ADVANCED_GAS_MOVE") or
            ("TheInsatiable", "LIQUIFY_GROUND_MOVE") or
            ("ThievingHopper", "THIEVERY_MOVE") or
            ("TwoTailedRat", "SCREECH_MOVE")
                => JointMoveEffectScope.TargetOnly,
            _ => JointMoveEffectScope.OwnerOnly,
        };
    }

    public static bool ApplyForecastMove(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        ISet<uint> processedEnemyDeaths,
        IReadOnlyList<PlanCardChoice>? plannedChoices = null,
        bool applyBeforeAttack = true,
        bool applyMoveEffect = true)
    {
        SimCreatureState simulatedPlayer = simulator.State.GetCreature(player);
        if (applyBeforeAttack)
            MonsterMoveEffects.ApplyBeforeAttack(simulator, combat, move, player);
        if (simulator.HasPendingChoice)
            return simulatedPlayer.IsDead;
        bool fullyBlockedAttack = false;
        bool playerDied = false;
        AttackCommand? attackContext = move.AttackHits.Count > 0
            ? simulator.BeginAttackContext(
                new AttackCommand(0m)
                    .FromMonster(move.Owner.Monster
                        ?? throw new InvalidOperationException("预测攻击的所有者不是怪物。"))
                    .WithHitCount(0))
            : null;
        bool attackCompleted = attackContext == null;
        try
        {
            if (simulator.HasPendingChoice)
                return simulatedPlayer.IsDead;

            foreach (ForecastAttackHit hit in move.AttackHits)
            {
                int baseDamage = combat.AdjustMonsterMoveDamage(move.Owner, move.Move.Id, hit.BaseDamage);
                IReadOnlyList<DamageResult> results = DamagePlayer(
                    simulator,
                    combat,
                    move.Owner,
                    player,
                    baseDamage);
                if (simulator.HasPendingChoice)
                    return simulatedPlayer.IsDead;
                simulator.AddAttackContextHit(attackContext!, results);
                foreach (DamageResult result in results)
                {
                    if (ReferenceEquals(result.Receiver, player) && result.WasFullyBlocked)
                        fullyBlockedAttack = true;
                }
                CorePowerSupport.ApplyEnemyDeathPowers(
                    simulator,
                    combat,
                    combat.KnownEnemies,
                    processedEnemyDeaths);
                if (simulator.HasPendingChoice)
                    return simulatedPlayer.IsDead;
                if (simulatedPlayer.IsDead)
                {
                    playerDied = true;
                    break;
                }
                if (simulator.State.GetCreature(move.Owner).IsDead)
                    break;
            }

            attackCompleted = true;
        }
        finally
        {
            if (attackContext != null)
                simulator.EndAttackContext(attackContext, attackCompleted);
        }

        if (simulator.HasPendingChoice)
            return simulatedPlayer.IsDead;
        if (playerDied)
            return true;
        if (fullyBlockedAttack && combat.GetAmount<ImbalancedPower>(move.Owner) > 0)
        {
            if (move.Owner.Monster is BowlbugRock)
                combat.ForceStunnedMove(move.Owner, "HEADBUTT_MOVE");
            combat.StunNextMove(move.Owner);
        }
        if (applyMoveEffect)
        {
            ApplyForecastMoveEffect(
                simulator,
                combat,
                move,
                player,
                processedEnemyDeaths,
                plannedChoices);
        }
        else
        {
            simulator.SynchronizePowerAmountPredictionStates();
            PowerLifecycleSupport.ResolvePowerAmountChanges(simulator, combat);
            combat.NormalizeAeonglassWithers(simulator);
            combat.NormalizeCardAfflictions(simulator);
        }
        return simulatedPlayer.IsDead;
    }

    private static void ApplyForecastMoveEffect(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        ISet<uint> processedEnemyDeaths,
        IReadOnlyList<PlanCardChoice>? plannedChoices)
        => ApplyForecastMoveEffect(
            simulator,
            combat,
            move,
            player,
            processedEnemyDeaths,
            plannedChoices,
            applyTargetPortion: true,
            applyOwnerPortion: true);

    private static void ApplyForecastMoveEffect(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        ISet<uint> processedEnemyDeaths,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        bool applyTargetPortion,
        bool applyOwnerPortion)
    {
        MonsterMoveEffects.Apply(
            simulator,
            combat,
            move,
            player,
            out bool killedOwner,
            plannedChoices,
            applyTargetPortion,
            applyOwnerPortion);
        if (simulator.HasPendingChoice)
            return;
        if (killedOwner
            && move.Owner.CombatId is uint moveOwnerCombatId
            && !processedEnemyDeaths.Contains(moveOwnerCombatId))
        {
            CorePowerSupport.ApplyEnemyDeathPowers(
                simulator,
                combat,
                combat.KnownEnemies,
                processedEnemyDeaths);
            if (simulator.HasPendingChoice)
                return;
        }
        simulator.SynchronizePowerAmountPredictionStates();
        PowerLifecycleSupport.ResolvePowerAmountChanges(simulator, combat);
        combat.NormalizeAeonglassWithers(simulator);
        combat.NormalizeCardAfflictions(simulator);
    }

    public static IReadOnlyList<DamageResult> DamagePlayer(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        Creature attacker,
        Creature player,
        int baseDamage)
    {
        Creature? osty = player.Player is { } owner ? simulator.State.GetOsty(owner) : null;
        int? suppressedDieForYou = null;
        if (osty != null
            && simulator.State.GetCreature(osty).IsDead
            && combat.GetAmount<DieForYouPower>(osty) is > 0 and var amount)
        {
            suppressedDieForYou = amount;
            combat.SetAmount<DieForYouPower>(osty, 0);
        }

        try
        {
            using (simulator.PushDamageSource(
                CombatDamageSource.For(CombatDamageSourceKind.MonsterMove, attacker.Monster?.Id.Entry)))
            {
                return simulator.Damage(player, baseDamage, ValueProp.Move, attacker);
            }
        }
        finally
        {
            if (suppressedDieForYou is { } restoredAmount)
                combat.SetAmount<DieForYouPower>(osty!, restoredAmount);
        }
    }
}
