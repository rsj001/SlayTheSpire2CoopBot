using CoopBot.Capture;
using CoopBot.Protocol;
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
