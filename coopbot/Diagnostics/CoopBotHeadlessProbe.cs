using CoopBot.Capture;
using CoopBot.Protocol;
using CoopBot.UI;
using CombatSolver;
using MegaCrit.Sts2.Core.Combat;
using System.Reflection;
using System.Text.Json;

namespace CoopBot.Diagnostics;

public static class CoopBotHeadlessProbe
{
    public static string AuditSyntheticRoot(CombatState combat)
    {
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        HostVisibilityAuditResult audit = HostVisibilityAudit.Inspect(combat, root);
        if (!audit.IsComplete)
            throw new InvalidOperationException(
                "Synthetic Host visibility audit failed: " + string.Join(',', audit.Failures));
        return $"actors={root.Actors.Count};rng=9;fingerprint_fields={root.ContinuationStamp.StateText.Split(';').Length}";
    }

    public static string SearchSyntheticRoot(CombatState combat)
    {
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        JointOfflineSearchRequest request = JointOfflineSearchRequest.Default(
            maximumActions: 1,
            maximumStates: 128);
        JointOfflineSearchResult serial = JointOfflineSearch.SolveBeam(
            root,
            request,
            beamWidth: 64,
            degreeOfParallelism: 1);
        JointReplayResult serialReplay = JointStrictReplayVerifier.Verify(root, serial);
        JointOfflineSearchResult parallel = JointOfflineSearch.SolveBeam(
            root,
            request,
            beamWidth: 64,
            degreeOfParallelism: 4);
        JointReplayResult parallelReplay = JointStrictReplayVerifier.Verify(root, parallel);
        PlanPublishedPayload serialPlan = CoopPlanSnapshotFactory.Create(
            1,
            "SYNTHETIC-C3",
            root,
            serial,
            serialReplay);
        PlanPublishedPayload parallelPlan = CoopPlanSnapshotFactory.Create(
            1,
            "SYNTHETIC-C3",
            root,
            parallel,
            parallelReplay);
        if (!string.Equals(
                JsonSerializer.Serialize(serialPlan, CoopProtocol.JsonOptions),
                JsonSerializer.Serialize(parallelPlan, CoopProtocol.JsonOptions),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "DOP1 and DOP4 produced different published plans for the same frozen root.");
        }
        if (serial.ExpandedStates != parallel.ExpandedStates)
            throw new InvalidOperationException(
                $"DOP expansion count differs: serial={serial.ExpandedStates} parallel={parallel.ExpandedStates}.");
        AssertPublishedDtoIsDetached(typeof(PlanPublishedPayload), new HashSet<Type>());
        return $"actors={root.Actors.Count};actions={serial.Actions.Count};" +
               $"expanded={serial.ExpandedStates};checkpoints={serial.Checkpoints.Count};" +
               $"plan={serialPlan.PlanId};dop=1,4;strict_replay=2";
    }

    public static string ProjectFourUiSnapshots(CombatState combat)
    {
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        JointOfflineSearchResult searched = JointOfflineSearch.SolveBeam(
            root,
            JointOfflineSearchRequest.Default(maximumActions: 1, maximumStates: 128),
            beamWidth: 64,
            degreeOfParallelism: 1);
        JointReplayResult replay = JointStrictReplayVerifier.Verify(root, searched);
        PlanPublishedPayload plan = CoopPlanSnapshotFactory.Create(
            7,
            "SYNTHETIC-C4",
            root,
            searched,
            replay);
        CoopUiSnapshot[] chinese = Enumerable.Range(0, 4)
            .Select(actor => CoopUiSnapshot.Capture(
                plan,
                actor == 0 ? CoopUiRole.Host : CoopUiRole.Client,
                actor,
                CoopAutomationMode.ConfirmEach,
                "计划就绪",
                currentActionIndex: 0,
                locale: "zhs"))
            .ToArray();
        CoopUiSnapshot[] english = Enumerable.Range(0, 4)
            .Select(actor => CoopUiSnapshot.Capture(
                plan,
                actor == 0 ? CoopUiRole.Host : CoopUiRole.Client,
                actor,
                CoopAutomationMode.ConfirmEach,
                "计划就绪",
                currentActionIndex: 0,
                locale: "eng"))
            .ToArray();
        foreach (CoopUiSnapshot snapshot in chinese.Concat(english))
        {
            if (snapshot.PlanId != plan.PlanId
                || snapshot.Route.Count != plan.Actions.Count
                || snapshot.Route.Select(step => (step.Index, step.ActorId))
                    .SequenceEqual(plan.Actions.Select(action => (action.Index, action.ActorId))) == false)
            {
                throw new InvalidOperationException("Endpoint UI snapshot changed PlanId or route identity.");
            }
        }
        if (!chinese[0].ShowHostControls || chinese[0].ShowClientControls
            || chinese.Skip(1).Any(snapshot => snapshot.ShowHostControls || !snapshot.ShowClientControls))
        {
            throw new InvalidOperationException("Host/client UI controls do not match endpoint roles.");
        }
        if (chinese[0].Title == english[0].Title
            || !english[0].Title.Contains("Co-op Bot", StringComparison.Ordinal)
            || !chinese[0].StatusLine.Contains("逐步确认", StringComparison.Ordinal)
            || !english[0].StatusLine.Contains("Confirm each", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("CoopBot zhs/eng UI projection is incomplete.");
        }
        return $"endpoints=4;plan={plan.PlanId};route={plan.Actions.Count};locales=zhs,eng;" +
               "host_controls=1;client_controls=3";
    }

    private static void AssertPublishedDtoIsDetached(Type type, HashSet<Type> visited)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal))
            return;
        if (type.IsArray)
        {
            AssertPublishedDtoIsDetached(type.GetElementType()!, visited);
            return;
        }
        if (type.IsGenericType
            && type.GetGenericTypeDefinition() is Type generic
            && (generic == typeof(IReadOnlyList<>) || generic == typeof(List<>)))
        {
            AssertPublishedDtoIsDetached(type.GetGenericArguments()[0], visited);
            return;
        }
        if (!visited.Add(type)) return;
        if (type.Assembly == typeof(CombatRootSnapshot).Assembly)
        {
            throw new InvalidOperationException(
                $"Published DTO retains CombatSolver/search type {type.FullName}.");
        }
        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            AssertPublishedDtoIsDetached(property.PropertyType, visited);
    }
}
