using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CombatSolver;
using CoopBot.Capture;
using CoopBot.Host;
using CoopBot.Protocol;
using CoopBot.Runtime;
using CoopBot.Session;
using MegaCrit.Sts2.Core.Combat;
using STS2RitsuLib;

namespace CoopBot.Diagnostics;

internal sealed record CoopEvidenceAssembly(
    string Name,
    string Version,
    string Sha256);

internal sealed record CoopEvidenceActor(
    int ActorId,
    string NetworkIdentity,
    string CharacterId,
    bool IsHost,
    bool IsLocal);

internal sealed record CoopEvidenceSession(
    string CombatSessionId,
    int ProtocolVersion,
    string Role,
    string LocalNetworkIdentity,
    string HostNetworkIdentity,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<CoopEvidenceActor> Actors);

internal sealed record CoopEvidenceRoot(
    long RootRevision,
    string Fingerprint,
    int ActorCount,
    IReadOnlyList<string> ActorFingerprints,
    int RngStreamCount,
    IReadOnlyList<string> VisibilityFailures);

internal sealed record CoopEvidenceProtocol(
    long EvidenceSequence,
    string Direction,
    string Kind,
    long MessageSequence,
    string PeerNetworkIdentity,
    long RootRevision,
    string? PlanId,
    string? ActionId,
    string Disposition);

internal sealed record CoopEvidenceDeployment(
    long EvidenceSequence,
    string Kind,
    string PlanId,
    string ActionId,
    string Detail);

internal sealed record CoopEvidenceCleanup(
    string Reason,
    string SessionState,
    string LocalAgentState,
    bool NoResidualSessionState,
    int DuplicateLocalExecutionCount);

internal sealed record CoopEvidenceDocument(
    int SchemaVersion,
    DateTimeOffset CreatedUtc,
    IReadOnlyList<CoopEvidenceAssembly> Assemblies,
    CoopEvidenceSession? Session,
    HostSearchPolicy? SearchPolicy,
    IReadOnlyList<CoopEvidenceRoot> Roots,
    IReadOnlyList<PlanPublishedPayload> Plans,
    IReadOnlyList<CoopEvidenceProtocol> Protocol,
    IReadOnlyList<CoopEvidenceDeployment> Deployment,
    IReadOnlyList<string> LocalExecutionActionIds,
    CoopEvidenceCleanup Cleanup);

internal sealed class CoopEvidenceRecorder
{
    private const int SchemaVersion = 1;
    private readonly object _gate = new();
    private string _combatSessionId;
    private readonly string _outputDirectory;
    private readonly List<CoopEvidenceRoot> _roots = [];
    private readonly List<PlanPublishedPayload> _plans = [];
    private readonly List<CoopEvidenceProtocol> _protocol = [];
    private readonly List<CoopEvidenceDeployment> _deployment = [];
    private readonly List<string> _localExecutions = [];
    private long _sequence;
    private CoopEvidenceSession? _session;
    private HostSearchPolicy? _policy;

    internal CoopEvidenceRecorder(string combatSessionId, string outputDirectory)
    {
        if (string.IsNullOrWhiteSpace(combatSessionId))
            throw new ArgumentException("Combat session ID is required.", nameof(combatSessionId));
        _combatSessionId = combatSessionId;
        _outputDirectory = outputDirectory;
    }

    internal static string DefaultOutputDirectory
        => Path.Combine(Godot.ProjectSettings.GlobalizePath("user://"), "CoopBot", "evidence");

    internal void RecordSession(CoopSession session)
    {
        _combatSessionId = session.CombatSessionId;
        CoopEvidenceActor[] actors = session.Actors
            .OrderBy(actor => actor.ActorId)
            .Select(actor => new CoopEvidenceActor(
                actor.ActorId,
                Pseudonym(actor.NetworkPlayerId),
                actor.CharacterId,
                actor.IsHost,
                actor.NetworkPlayerId == session.LocalNetworkPlayerId))
            .ToArray();
        lock (_gate)
        {
            _session = new CoopEvidenceSession(
                session.CombatSessionId,
                CoopProtocol.Version,
                session.IsHost ? "Host" : "Client",
                Pseudonym(session.LocalNetworkPlayerId),
                Pseudonym(session.HostNetworkPlayerId),
                session.Capabilities.ToArray(),
                Array.AsReadOnly(actors));
        }
    }

    internal void RecordPolicy(HostSearchPolicy policy)
    {
        lock (_gate)
            _policy = policy;
    }

    internal void RecordRoot(RecordedCombatRoot root)
    {
        lock (_gate)
        {
            _roots.Add(new CoopEvidenceRoot(
                root.RootRevision,
                root.Fingerprint,
                root.Root.Actors.Count,
                root.ActorFingerprints.ToArray(),
                RngStreamCount: 9,
                root.Visibility.Failures.ToArray()));
        }
    }

    internal void RecordPlan(PlanPublishedPayload plan)
    {
        lock (_gate)
            _plans.Add(plan);
    }

    internal void RecordProtocol(CoopProtocolEvent observed)
    {
        lock (_gate)
        {
            _protocol.Add(new CoopEvidenceProtocol(
                ++_sequence,
                observed.Direction,
                observed.Kind.ToString(),
                observed.Sequence,
                Pseudonym(observed.PeerNetworkPlayerId),
                observed.RootRevision,
                observed.PlanId,
                observed.ActionId,
                observed.Disposition));
        }
    }

    internal void RecordDeployment(HostDeploymentEvent observed)
    {
        lock (_gate)
        {
            _deployment.Add(new CoopEvidenceDeployment(
                ++_sequence,
                observed.Kind,
                observed.PlanId,
                observed.ActionId,
                observed.Detail));
        }
    }

    internal void RecordLocalExecution(string actionId)
    {
        lock (_gate)
            _localExecutions.Add(actionId);
    }

    internal string Complete(
        string reason,
        CoopSessionState sessionState,
        string localAgentState,
        bool noResidualSessionState)
    {
        CoopEvidenceDocument document;
        lock (_gate)
        {
            int duplicateExecutions = _localExecutions.Count
                - _localExecutions.Distinct(StringComparer.Ordinal).Count();
            document = new CoopEvidenceDocument(
                SchemaVersion,
                DateTimeOffset.UtcNow,
                CaptureAssemblies(),
                _session,
                _policy,
                _roots.ToArray(),
                _plans.ToArray(),
                _protocol.ToArray(),
                _deployment.ToArray(),
                _localExecutions.ToArray(),
                new CoopEvidenceCleanup(
                    reason,
                    sessionState.ToString(),
                    localAgentState,
                    noResidualSessionState,
                    duplicateExecutions));
        }
        Directory.CreateDirectory(_outputDirectory);
        string timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ");
        string destination = Path.Combine(
            _outputDirectory,
            $"coopbot-{timestamp}-{ShortHash(_combatSessionId)}.json");
        string temporary = destination + ".tmp";
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(document, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        });
        File.WriteAllBytes(temporary, json);
        File.Move(temporary, destination, overwrite: true);
        return destination;
    }

    private string Pseudonym(ulong networkPlayerId)
        => ShortHash(_combatSessionId + ":" + networkPlayerId);

    private static string ShortHash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];

    private static IReadOnlyList<CoopEvidenceAssembly> CaptureAssemblies()
        => new[]
        {
            CaptureAssembly(typeof(CombatState).Assembly),
            CaptureAssembly(typeof(CombatRootSnapshot).Assembly),
            CaptureAssembly(typeof(CoopEvidenceRecorder).Assembly),
            CaptureAssembly(typeof(RitsuLibFramework).Assembly),
        };

    private static CoopEvidenceAssembly CaptureAssembly(System.Reflection.Assembly assembly)
    {
        string path = assembly.Location;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new InvalidOperationException($"Cannot hash loaded assembly {assembly.FullName}.");
        return new CoopEvidenceAssembly(
            assembly.GetName().Name ?? throw new InvalidOperationException("Assembly name is missing."),
            assembly.GetName().Version?.ToString() ?? "0.0.0.0",
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
    }
}
