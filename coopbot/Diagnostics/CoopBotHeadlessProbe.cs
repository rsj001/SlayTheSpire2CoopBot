using CoopBot.Capture;
using CombatSolver;
using MegaCrit.Sts2.Core.Combat;

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
}
