using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private static void AssertMultiplayerSemanticSearchOracles(CombatState source)
    {
        AssertCardOracle(source, typeof(OneForAll), "DirectAllAllies");
        AssertCardOracle(source, typeof(Coordinate), "StatePower", crossTurn: true);
        AssertCardOracle(source, typeof(TheBall), "CardTransfer");
        AssertCardOracle(source, typeof(Tutor), "Choice");
        AssertCardOracle(
            source,
            typeof(Hibernate),
            "Orb",
            [ModelDb.Character<Defect>(), ModelDb.Character<Ironclad>()]);
        AssertCardOracle(
            source,
            typeof(LegionOfBone),
            "Pet",
            [ModelDb.Character<Necrobinder>(), ModelDb.Character<Ironclad>()]);
        AssertPotionOracle(source);
        AssertFourActorMultiplayerCardOracle(source);
    }

    private static void AssertCardOracle(
        CombatState source,
        Type cardType,
        string family,
        IReadOnlyList<CharacterModel>? roster = null,
        bool crossTurn = false)
    {
        OfflineJointCombat offline = CreateOfflineJointCombat(
            source,
            actorCount: 2,
            characterRoster: roster);
        foreach (Player player in offline.Players)
        {
            PlayerCombatState state = player.PlayerCombatState!;
            foreach (CardModel existing in state.Hand.Cards.ToArray())
                state.Hand.RemoveInternal(existing, silent: true);
            state.Energy = 99;
        }
        Player owner = offline.Players[0];
        CardModel canonical = ModelDb.All.OfType<CardModel>().Single(card => card.GetType() == cardType);
        CardModel card = offline.State.CreateCard(canonical, owner);
        owner.PlayerCombatState!.Hand.AddInternal(card, silent: true);
        CombatRootSnapshot root = CombatRootSnapshot.Capture(offline.State);
        JointTurnState turns = JointTurnState.Start(2, root.StartTurnNumber);
        PlanAction cardAction = JointActionExpander.Expand(root.ForkSimulator(), turns)
            .Select(static candidate => candidate.Action)
            .First(action => action.Actor == new CombatActorId(0)
                && action.Kind == PlanActionKind.PlayCard
                && action.CardId == card.Id.Entry);
        List<PlanAction> prefix = [cardAction];
        int maximumActions = 2;
        if (crossTurn)
        {
            prefix.Add(new PlanAction(
                PlanActionKind.EndTurn,
                root.StartTurnNumber,
                Actor: new CombatActorId(0)));
            prefix.Add(new PlanAction(
                PlanActionKind.EndTurn,
                root.StartTurnNumber,
                Actor: new CombatActorId(1)));
            maximumActions = 4;
        }
        AssertSearchMatchesOracle(
            root,
            new JointOfflineSearchRequest(prefix, maximumActions, MaximumStates: 10_000),
            family);
    }

    private static void AssertPotionOracle(CombatState source)
    {
        CombatRootSnapshot root = CreateOfflineJointRoot(source, actorCount: 2);
        JointTurnState turns = JointTurnState.Start(2, root.StartTurnNumber);
        PlanAction potion = JointActionExpander.Expand(root.ForkSimulator(), turns)
            .Select(static candidate => candidate.Action)
            .First(action => action.Actor == new CombatActorId(0)
                && action.Kind == PlanActionKind.UsePotion
                && action.PotionId == "BLOCK_POTION");
        AssertSearchMatchesOracle(
            root,
            new JointOfflineSearchRequest([potion], MaximumActions: 2, MaximumStates: 4_000),
            "Potion");
    }

    private static void AssertFourActorMultiplayerCardOracle(CombatState source)
    {
        OfflineJointCombat offline = CreateOfflineJointCombat(source, actorCount: 4);
        Player owner = offline.Players[0];
        foreach (Player player in offline.Players)
        {
            foreach (CardModel existing in player.PlayerCombatState!.Hand.Cards.ToArray())
                player.PlayerCombatState.Hand.RemoveInternal(existing, silent: true);
        }
        CardModel card = offline.State.CreateCard(ModelDb.Card<OneForAll>(), owner);
        owner.PlayerCombatState!.Hand.AddInternal(card, silent: true);
        CombatRootSnapshot root = CombatRootSnapshot.Capture(offline.State);
        PlanAction action = JointActionExpander.Expand(
                root.ForkSimulator(),
                JointTurnState.Start(4, root.StartTurnNumber))
            .Select(static candidate => candidate.Action)
            .Single(candidate => candidate.Kind == PlanActionKind.PlayCard);
        AssertSearchMatchesOracle(
            root,
            new JointOfflineSearchRequest([action], MaximumActions: 2, MaximumStates: 4_000),
            "FourActorAllAllies");
    }

    private static void AssertSearchMatchesOracle(
        CombatRootSnapshot root,
        JointOfflineSearchRequest request,
        string family)
    {
        JointOfflineSearchResult oracle = JointOfflineSearch.SolveDepthFirstOracle(root, request);
        JointOfflineSearchResult bfs = JointOfflineSearch.SolveBreadthFirst(root, request);
        JointOfflineSearchResult beam1 = JointOfflineSearch.SolveBeam(
            root, request, beamWidth: request.MaximumStates, degreeOfParallelism: 1);
        JointOfflineSearchResult beam4 = JointOfflineSearch.SolveBeam(
            root, request, beamWidth: request.MaximumStates, degreeOfParallelism: 4);
        JointOfflineSearchResult bfws = JointOfflineSearch.SolveBfws(
            root, request, maximumOpen: request.MaximumStates);
        foreach (JointOfflineSearchResult actual in new[] { bfs, beam1, beam4, bfws })
        {
            if (actual.Termination != JointSearchTermination.Completed
                || JointObjectiveScore.Compare(actual.Score, oracle.Score) != 0
                || !actual.Score.HpLostByActor.SequenceEqual(oracle.Score.HpLostByActor)
                || actual.Snapshot.StateKey != oracle.Snapshot.StateKey
                || ComparePlanActions(actual.Actions, oracle.Actions) != 0)
            {
                throw new InvalidOperationException(
                    $"联合 {family} 搜索未与不去重 DFS oracle 对齐：" +
                    $"termination={actual.Termination}, expanded={actual.ExpandedStates}。 ");
            }
        }
        if (beam1.ExpandedStates != beam4.ExpandedStates
            || ComparePlanActions(beam1.Actions, beam4.Actions) != 0)
        {
            throw new InvalidOperationException($"联合 {family} DOP1/DOP4 结果或展开数不确定。 ");
        }
        JointReplayResult replay = JointStrictReplayVerifier.Verify(root, beam1);
        if (replay.Snapshot.StateKey != beam1.Snapshot.StateKey
            || ComparePlanActions(replay.AppliedActions, beam1.Actions) != 0)
        {
            throw new InvalidOperationException($"联合 {family} 最佳路线不能从原根严格回放。 ");
        }
    }
}
