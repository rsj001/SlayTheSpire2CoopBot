using System.Text.Json;

if (args.Length != 4)
    throw new ArgumentException("Usage: CoopBot.EvidenceChecks <host.json> <client1.json> <client2.json> <client3.json>");

List<JsonDocument> documents = [];
try
{
    foreach (string argument in args)
    {
        string path = Path.GetFullPath(argument);
        if (!File.Exists(path))
            throw new FileNotFoundException("CoopBot evidence file is missing.", path);
        documents.Add(JsonDocument.Parse(File.ReadAllBytes(path)));
    }
    JsonElement[] roots = documents.Select(document => document.RootElement).ToArray();
    Require(roots.All(root => root.GetProperty("schemaVersion").GetInt32() == 1), "schema version");
    Require(roots.All(root => root.GetProperty("session").GetProperty("protocolVersion").GetInt32() == 2),
        "protocol version");
    string[] sessionIds = roots.Select(root =>
        root.GetProperty("session").GetProperty("combatSessionId").GetString()!).ToArray();
    Require(sessionIds.Distinct(StringComparer.Ordinal).Count() == 1, "shared combat session ID");
    string[] roles = roots.Select(root =>
        root.GetProperty("session").GetProperty("role").GetString()!).ToArray();
    Require(roles.Count(role => role == "Host") == 1 && roles.Count(role => role == "Client") == 3,
        "one Host and three Clients");

    string[] actorMaps = roots.Select(root => CanonicalActors(root.GetProperty("session").GetProperty("actors")))
        .ToArray();
    Require(actorMaps.Distinct(StringComparer.Ordinal).Count() == 1, "identical Actor mapping");
    Require(roots.All(root => root.GetProperty("session").GetProperty("actors").GetArrayLength() == 4),
        "four Actors");

    string[] assemblyMaps = roots.Select(root => CanonicalAssemblies(root.GetProperty("assemblies"))).ToArray();
    Require(assemblyMaps.Distinct(StringComparer.Ordinal).Count() == 1, "identical assembly hashes");
    Require(roots.All(root =>
    {
        JsonElement cleanup = root.GetProperty("cleanup");
        return cleanup.GetProperty("noResidualSessionState").GetBoolean()
               && cleanup.GetProperty("duplicateLocalExecutionCount").GetInt32() == 0
               && cleanup.GetProperty("sessionState").GetString() == "Stopped";
    }), "clean endpoint shutdown");

    string[] executions = roots
        .SelectMany(root => root.GetProperty("localExecutionActionIds").EnumerateArray())
        .Select(element => element.GetString()!)
        .ToArray();
    Require(executions.Length == executions.Distinct(StringComparer.Ordinal).Count(),
        "globally exactly-once local execution");

    HashSet<string> protocolKinds = roots
        .SelectMany(root => root.GetProperty("protocol").EnumerateArray())
        .Select(element => element.GetProperty("kind").GetString()!)
        .ToHashSet(StringComparer.Ordinal);
    foreach (string required in new[]
             {
                 "Hello", "SessionAccepted", "ActorAssignment", "RootPublished", "PlanPublished",
                 "ActionPrepare", "ActionPrepared", "ActionCommit", "ActionAck", "Heartbeat",
             })
        Require(protocolKinds.Contains(required), $"protocol event {required}");

    HashSet<string> sharedPlans = roots
        .Select(root => root.GetProperty("plans").EnumerateArray()
            .Select(plan => plan.GetProperty("planId").GetString()!)
            .ToHashSet(StringComparer.Ordinal))
        .Aggregate((left, right) =>
        {
            left.IntersectWith(right);
            return left;
        });
    Require(sharedPlans.Count > 0, "at least one plan observed by all endpoints");

    JsonElement host = roots.Single(root =>
        root.GetProperty("session").GetProperty("role").GetString() == "Host");
    JsonElement[] hostPlans = host.GetProperty("plans").EnumerateArray().ToArray();
    Require(hostPlans.Length > 0, "Host plan evidence");
    string? outcome = hostPlans[^1].GetProperty("score").GetProperty("outcome").GetString();
    Require(outcome is "Victory" or "Defeat", "terminal Host outcome");

    Console.WriteLine(JsonSerializer.Serialize(new
    {
        status = "Passed",
        endpoints = 4,
        protocolVersion = 2,
        sessionId = sessionIds[0],
        sharedPlans = sharedPlans.Count,
        localExecutions = executions.Length,
        duplicateExecutions = 0,
        terminalOutcome = outcome,
        cleanup = "4_stopped",
    }));
}
finally
{
    foreach (JsonDocument document in documents)
        document.Dispose();
}

static void Require(bool condition, string requirement)
{
    if (!condition)
        throw new InvalidOperationException("Four-endpoint evidence failed: " + requirement + ".");
}

static string CanonicalActors(JsonElement actors)
    => string.Join('|', actors.EnumerateArray()
        .OrderBy(actor => actor.GetProperty("actorId").GetInt32())
        .Select(actor => string.Join(':',
            actor.GetProperty("actorId").GetInt32(),
            actor.GetProperty("networkIdentity").GetString(),
            actor.GetProperty("characterId").GetString(),
            actor.GetProperty("isHost").GetBoolean())));

static string CanonicalAssemblies(JsonElement assemblies)
    => string.Join('|', assemblies.EnumerateArray()
        .OrderBy(assembly => assembly.GetProperty("name").GetString(), StringComparer.Ordinal)
        .Select(assembly => string.Join(':',
            assembly.GetProperty("name").GetString(),
            assembly.GetProperty("version").GetString(),
            assembly.GetProperty("sha256").GetString())));
