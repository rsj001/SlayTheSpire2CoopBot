using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Actions;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private void AssertMultiplayerNativeDifferentialSubstrate(CombatState source)
    {
        foreach (int actorCount in new[] { 2, 3, 4 })
        {
            OfflineJointCombat native = CreateOfflineJointCombat(source, actorCount);
            CombatRootSnapshot root = CombatRootSnapshot.Capture(native.State);
            string liveBefore = ContinuationStamp.CaptureLive(native.State).StateText;
            CombatPredictionSimulator first = root.ForkSimulator();
            CombatPredictionSimulator second = root.ForkSimulator();
            SimulatedCombatState firstCombat = (SimulatedCombatState)first.State.CombatState;
            SimulatedCombatState secondCombat = (SimulatedCombatState)second.State.CombatState;

            for (int actorIndex = 0; actorIndex < actorCount; actorIndex++)
            {
                Player nativePlayer = native.Players[actorIndex];
                Player predictedPlayer = root.Actors[actorIndex].PlayerIdentity;
                MoveStateSnapshot actual = CaptureActual(native.State, nativePlayer, native.Enemy);
                MoveStateSnapshot predicted = CaptureSimulated(
                    first,
                    firstCombat,
                    predictedPlayer,
                    native.Enemy,
                    root.PlayerIdentity);
                AssertSnapshotEqual(
                    predicted,
                    actual,
                    "MultiplayerNativeRoot",
                    $"ActorCount{actorCount}.Actor{actorIndex}");
            }

            JointTurnState turns = JointTurnState.Start(actorCount, root.StartTurnNumber);
            JointCombatSnapshot firstSnapshot = JointCombatSnapshot.Capture(root, first, turns);
            JointCombatSnapshot secondSnapshot = JointCombatSnapshot.Capture(root, second, turns);
            if (firstSnapshot.StateKey != secondSnapshot.StateKey
                || firstSnapshot.Continuation != secondSnapshot.Continuation)
            {
                throw new InvalidOperationException(
                    $"{actorCount} Actor 原生根建立的两个 prediction Fork 初始状态不同。 ");
            }

            SimPlayerCombatState remote = second.State.GetPlayerCombatState(root.Actors[^1].PlayerIdentity);
            remote.GainEnergy(1);
            JointCombatSnapshot mutated = JointCombatSnapshot.Capture(root, second, turns);
            if (mutated.StateKey == firstSnapshot.StateKey)
                throw new InvalidOperationException($"{actorCount} Actor 远端能量变化没有进入联合状态键。 ");
            if (ContinuationStamp.CaptureLive(native.State).StateText != liveBefore)
                throw new InvalidOperationException($"{actorCount} Actor prediction Fork 修改了原生根。 ");
        }
    }

    private async Task AssertMultiplayerNativeSimpleActionDifferentialAsync(CombatState source)
    {
        OfflineJointCombat native = CreateOfflineJointCombat(source, actorCount: 2);
        Player nativeActor = native.Players[0];
        nativeActor.PlayerCombatState!.Hand.AddInternal(
            native.State.CreateCard(ModelDb.Card<OneForAll>(), nativeActor),
            silent: true);
        CombatRootSnapshot root = CombatRootSnapshot.Capture(native.State);
        CombatPredictionSimulator predicted = root.ForkSimulator();
        SimulatedCombatState predictedCombat = (SimulatedCombatState)predicted.State.CombatState;
        JointTurnState turns = JointTurnState.Start(2, root.StartTurnNumber);
        JointActionCandidate candidate = JointActionExpander.Expand(predicted, turns).Single(item =>
            item.Action.Actor.Index == 0
            && item.Action.Kind == PlanActionKind.PlayCard
            && item.Action.CardId == "ONE_FOR_ALL");

        CardModel nativeCard = nativeActor.PlayerCombatState!.Hand.Cards.Single(card =>
            card.Id.Entry == "ONE_FOR_ALL");
        await ExecuteSyntheticNativeCardAsync(nativeCard, target: null);

        ForkableSet<uint> deaths = JointActionTransition.CaptureProcessedEnemyDeaths(root, predicted);
        _ = JointActionTransition.Apply(predicted, turns, candidate.Action, deaths);
        for (int actorIndex = 0; actorIndex < native.Players.Count; actorIndex++)
        {
            MoveStateSnapshot actual = CaptureActual(
                native.State,
                native.Players[actorIndex],
                native.Enemy);
            MoveStateSnapshot simulated = CaptureSimulated(
                predicted,
                predictedCombat,
                root.Actors[actorIndex].PlayerIdentity,
                native.Enemy,
                root.PlayerIdentity);
            AssertSnapshotEqual(
                simulated,
                actual,
                "MultiplayerNativeAction",
                $"OneForAll.Actor{actorIndex}");
        }

        await AssertMultiplayerDirectCardsAsync(source);
        await AssertKnockdownExpectedGapAsync(source);
        await AssertTheBallExpectedGapAsync(source);
    }

    private async Task AssertMultiplayerDirectCardsAsync(CombatState source)
    {
        Type[] cardTypes =
        [
            typeof(BelieveInYou), typeof(GangUp), typeof(Lift), typeof(Mimic), typeof(Rally),
            typeof(Blaze), typeof(DemonicShield), typeof(Constellation), typeof(EnergySurge),
            typeof(OneForAll),
        ];
        foreach (Type cardType in cardTypes)
        {
            foreach (bool upgraded in new[] { false, true })
                await AssertMultiplayerDirectCardAsync(source, cardType, upgraded, actorCount: 2, deadActor: false);
        }

        foreach (Type cardType in new[] { typeof(Rally), typeof(EnergySurge) })
            await AssertMultiplayerDirectCardAsync(source, cardType, upgraded: true, actorCount: 4, deadActor: true);

        AssertGangUpAllyHistory(source);
        AssertDemonicShieldLethalOwner(source);
    }

    private static void AssertGangUpAllyHistory(CombatState source)
    {
        OfflineJointCombat native = CreateOfflineJointCombat(source, actorCount: 2);
        Player owner = native.Players[0];
        CardModel card = native.State.CreateCard(ModelDb.Card<GangUp>(), owner);
        card.UpgradeInternal();
        card.FinalizeUpgradeInternal();
        owner.PlayerCombatState!.Hand.AddInternal(card, silent: true);
        CombatRootSnapshot root = CombatRootSnapshot.Capture(native.State);
        CombatPredictionSimulator simulator = root.ForkSimulator();
        simulator.Damage(
            [native.Enemy],
            1,
            ValueProp.Move,
            native.Players[1].Creature);
        int before = simulator.State.GetCreature(native.Enemy).CurrentHp;
        _ = JointActionTransition.Apply(
            simulator,
            JointTurnState.Start(2, root.StartTurnNumber),
            new PlanAction(
                PlanActionKind.PlayCard,
                root.StartTurnNumber,
                CardId: card.Id.Entry,
                TargetCombatId: native.Enemy.CombatId,
                Actor: new CombatActorId(0)),
            JointActionTransition.CaptureProcessedEnemyDeaths(root, simulator));
        int dealt = before - simulator.State.GetCreature(native.Enemy).CurrentHp;
        int expected = card.DynamicVars.CalculationBase.IntValue + card.DynamicVars.ExtraDamage.IntValue;
        if (dealt != expected)
            throw new InvalidOperationException($"GangUp 队友本回合命中倍率错误：expected={expected}, actual={dealt}。 ");
    }

    private async Task AssertMultiplayerDirectCardAsync(
        CombatState source,
        Type cardType,
        bool upgraded,
        int actorCount,
        bool deadActor)
    {
        OfflineJointCombat native = CreateOfflineJointCombat(source, actorCount);
        Player sourceActor = native.Players[0];
        Player recipient = native.Players[1];
        sourceActor.PlayerCombatState!.Stars = 3;
        recipient.Creature.GainBlockInternal(7);
        sourceActor.Creature.GainBlockInternal(6);
        if (deadActor)
            native.Players[2].Creature.SetCurrentHpInternal(0);

        CardModel canonical = ModelDb.All.OfType<CardModel>().Single(card => card.GetType() == cardType);
        CardModel card = native.State.CreateCard(canonical, sourceActor);
        if (upgraded)
        {
            card.UpgradeInternal();
            card.FinalizeUpgradeInternal();
        }
        sourceActor.PlayerCombatState.Hand.AddInternal(card, silent: true);
        CombatRootSnapshot root = CombatRootSnapshot.Capture(native.State);
        CombatPredictionSimulator predicted = root.ForkSimulator();
        SimulatedCombatState predictedCombat = (SimulatedCombatState)predicted.State.CombatState;
        Creature? target = card.TargetType switch
        {
            TargetType.AnyEnemy => native.Enemy,
            TargetType.AnyAlly => recipient.Creature,
            _ => null,
        };
        await ExecuteSyntheticNativeCardAsync(card, target);
        PlanAction action = new(
            PlanActionKind.PlayCard,
            root.StartTurnNumber,
            CardId: card.Id.Entry,
            TargetCombatId: target?.CombatId,
            Actor: new CombatActorId(0));
        _ = JointActionTransition.Apply(
            predicted,
            JointTurnState.Start(actorCount, root.StartTurnNumber),
            action,
            JointActionTransition.CaptureProcessedEnemyDeaths(root, predicted));

        for (int actorIndex = 0; actorIndex < actorCount; actorIndex++)
        {
            AssertSnapshotEqual(
                CaptureSimulated(
                    predicted,
                    predictedCombat,
                    root.Actors[actorIndex].PlayerIdentity,
                    native.Enemy,
                    root.PlayerIdentity),
                CaptureActual(native.State, native.Players[actorIndex], native.Enemy),
                "MultiplayerDirectCard",
                $"{cardType.Name}.{(upgraded ? "Upgraded" : "Base")}.Actor{actorIndex}.Count{actorCount}");
        }
    }

    private static void AssertDemonicShieldLethalOwner(CombatState source)
    {
        OfflineJointCombat native = CreateOfflineJointCombat(source, actorCount: 2);
        Player sourceActor = native.Players[0];
        Player recipient = native.Players[1];
        sourceActor.Creature.SetCurrentHpInternal(1);
        sourceActor.Creature.GainBlockInternal(9);
        CardModel card = native.State.CreateCard(ModelDb.Card<DemonicShield>(), sourceActor);
        sourceActor.PlayerCombatState!.Hand.AddInternal(card, silent: true);
        CombatRootSnapshot root = CombatRootSnapshot.Capture(native.State);
        CombatPredictionSimulator predicted = root.ForkSimulator();
        _ = JointActionTransition.Apply(
            predicted,
            JointTurnState.Start(2, root.StartTurnNumber),
            new PlanAction(
                PlanActionKind.PlayCard,
                root.StartTurnNumber,
                CardId: card.Id.Entry,
                TargetCombatId: recipient.Creature.CombatId,
                Actor: new CombatActorId(0)),
            JointActionTransition.CaptureProcessedEnemyDeaths(root, predicted));
        SimCreatureState ownerState = predicted.State.GetCreature(sourceActor.Creature);
        SimCreatureState recipientState = predicted.State.GetCreature(recipient.Creature);
        if (!ownerState.IsDead || recipientState.Block != 9)
            throw new InvalidOperationException(
                $"DemonicShield 致死顺序错误：ownerDead={ownerState.IsDead}, recipientBlock={recipientState.Block}。 ");
    }

    private async Task AssertBelieveInYouExpectedGapAsync(CombatState source)
    {
        OfflineJointCombat native = CreateOfflineJointCombat(source, actorCount: 2);
        Player sourceActor = native.Players[0];
        Player recipient = native.Players[1];
        CardModel card = native.State.CreateCard(ModelDb.Card<BelieveInYou>(), sourceActor);
        sourceActor.PlayerCombatState!.Hand.AddInternal(card, silent: true);
        CombatRootSnapshot root = CombatRootSnapshot.Capture(native.State);
        CombatPredictionSimulator predicted = root.ForkSimulator();
        int energyBefore = recipient.PlayerCombatState!.Energy;
        await ExecuteSyntheticNativeCardAsync(card, recipient.Creature);
        int expectedEnergy = energyBefore + card.DynamicVars.Energy.IntValue;
        if (recipient.PlayerCombatState.Energy != expectedEnergy)
        {
            throw new InvalidOperationException(
                $"BelieveInYou 原版 RecipientActor.Energy={recipient.PlayerCombatState.Energy}，预期 {expectedEnergy}。 ");
        }

        PlanAction action = new(
            PlanActionKind.PlayCard,
            root.StartTurnNumber,
            CardId: card.Id.Entry,
            TargetCombatId: recipient.Creature.CombatId,
            Actor: new CombatActorId(0));
        try
        {
            _ = JointActionTransition.Apply(
                predicted,
                JointTurnState.Start(2, root.StartTurnNumber),
                action,
                JointActionTransition.CaptureProcessedEnemyDeaths(root, predicted));
        }
        catch (PredictionUnsupportedException error) when (
            error.Message.Contains(typeof(BelieveInYou).FullName!, StringComparison.Ordinal)
            && error.Message.Contains("plannedStage=M4", StringComparison.Ordinal))
        {
            return;
        }
        throw new InvalidOperationException(
            "BelieveInYou 预测侧没有以 M4 未支持语义稳定失败；原版首个已知差异为 RecipientActor.Energy。 ");
    }

    private async Task ExecuteSyntheticNativeCardAsync(CardModel nativeCard, MegaCrit.Sts2.Core.Entities.Creatures.Creature? target)
    {
        NetCombatCardDb.Instance.IdCardForTesting(nativeCard);
        MethodInfo powerVfx = typeof(CardModel).GetMethod(
            "PlayPowerCardFlyVfx",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(CardModel).FullName, "PlayPowerCardFlyVfx");
        MethodInfo powerVfxPrefix = typeof(UnattendedTestRunner).GetMethod(
            nameof(SkipSyntheticPowerCardVfx),
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(SkipSyntheticPowerCardVfx));
        Harmony vfxIsolation = new("CombatSolver.Testing.MultiplayerNativePowerVfx." + _request.RunId);
        vfxIsolation.Patch(powerVfx, prefix: new HarmonyMethod(powerVfxPrefix));
        try
        {
            PlayCardAction actualAction = new(nativeCard, target);
            actualAction.OnEnqueued(_ => { }, uint.MaxValue - 1);
            await actualAction.Execute();
            await actualAction.CompletionTask;
            if (actualAction.Exception != null || actualAction.State != GameActionState.Finished)
                throw new InvalidOperationException(
                    "原版 OneForAll GameAction 未完成。",
                    actualAction.Exception);
        }
        finally
        {
            vfxIsolation.Unpatch(powerVfx, powerVfxPrefix);
        }

    }

    private async Task AssertKnockdownExpectedGapAsync(CombatState source)
    {
        OfflineJointCombat native = CreateOfflineJointCombat(source, actorCount: 2);
        Player sourceActor = native.Players[0];
        CardModel card = native.State.CreateCard(ModelDb.Card<Knockdown>(), sourceActor);
        sourceActor.PlayerCombatState!.Hand.AddInternal(card, silent: true);
        CombatRootSnapshot root = CombatRootSnapshot.Capture(native.State);
        CombatPredictionSimulator predicted = root.ForkSimulator();
        int hpBefore = native.Enemy.CurrentHp;
        await ExecuteSyntheticNativeCardAsync(card, native.Enemy);
        PowerModel? power = native.Enemy.Powers.SingleOrDefault(item => item is KnockdownPower);
        if (native.Enemy.CurrentHp >= hpBefore || power?.Amount != card.DynamicVars["KnockdownPower"].IntValue)
        {
            throw new InvalidOperationException(
                "Knockdown 原版没有同时改变 Enemy.Hp 与 Enemy.Power[KNOCKDOWN_POWER]。 ");
        }

        AssertExpectedUnsupportedCardTransition(
            root,
            predicted,
            card,
            native.Enemy.CombatId,
            "M5",
            "Enemy.Hp/Power[KNOCKDOWN_POWER]");
    }

    private async Task AssertTheBallExpectedGapAsync(CombatState source)
    {
        OfflineJointCombat native = CreateOfflineJointCombat(source, actorCount: 2);
        Player sourceActor = native.Players[0];
        Player recipient = native.Players[1];
        CardModel card = native.State.CreateCard(ModelDb.Card<TheBall>(), sourceActor);
        sourceActor.PlayerCombatState!.Hand.AddInternal(card, silent: true);
        CombatRootSnapshot root = CombatRootSnapshot.Capture(native.State);
        CombatPredictionSimulator predicted = root.ForkSimulator();
        int hpBefore = native.Enemy.CurrentHp;
        await ExecuteSyntheticNativeCardAsync(card, native.Enemy);
        if (native.Enemy.CurrentHp >= hpBefore
            || !ReferenceEquals(card.Owner, recipient)
            || card.Pile?.Type != MegaCrit.Sts2.Core.Entities.Cards.PileType.Draw
            || card.DynamicVars.Damage.IntValue != 20)
        {
            throw new InvalidOperationException(
                "TheBall 原版没有同时改变 Enemy.Hp、CardOwner/Pile 与 Card.DynamicVars.Damage。 ");
        }

        AssertExpectedUnsupportedCardTransition(
            root,
            predicted,
            card,
            native.Enemy.CombatId,
            "M6",
            "Enemy.Hp/CardOwner/Pile/DynamicVars.Damage");
    }

    private static void AssertExpectedUnsupportedCardTransition(
        CombatRootSnapshot root,
        CombatPredictionSimulator predicted,
        CardModel card,
        uint? targetCombatId,
        string plannedStage,
        string nativeFields)
    {
        PlanAction action = new(
            PlanActionKind.PlayCard,
            root.StartTurnNumber,
            CardId: card.Id.Entry,
            TargetCombatId: targetCombatId,
            Actor: new CombatActorId(0));
        try
        {
            _ = JointActionTransition.Apply(
                predicted,
                JointTurnState.Start(root.Actors.Count, root.StartTurnNumber),
                action,
                JointActionTransition.CaptureProcessedEnemyDeaths(root, predicted));
        }
        catch (PredictionUnsupportedException error) when (
            error.Message.Contains(card.GetType().FullName!, StringComparison.Ordinal)
            && error.Message.Contains($"plannedStage={plannedStage}", StringComparison.Ordinal))
        {
            return;
        }
        throw new InvalidOperationException(
            $"{card.Id.Entry} 预测侧没有以 {plannedStage} 未支持语义稳定失败；" +
            $"原版首个已知差异字段为 {nativeFields}。 ");
    }

    private static bool SkipSyntheticPowerCardVfx(ref Task __result)
    {
        __result = Task.CompletedTask;
        return false;
    }
}
