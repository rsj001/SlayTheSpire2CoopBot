using System.Security.Cryptography;
using System.Text.Json;

if (args.Length is < 4 or > 5)
    throw new ArgumentException("Usage: CoopBot.LiveKit <repo-root> <game-root> <ritsu-root> <output-directory> [--deploy]");

string repoRoot = Full(args[0]);
string gameRoot = Full(args[1]);
string ritsuRoot = Full(args[2]);
string outputRoot = Full(args[3]);
bool deploy = args.Length == 5 && args[4] == "--deploy";
if (args.Length == 5 && !deploy)
    throw new ArgumentException($"Unknown option {args[4]}.");
if (outputRoot == repoRoot || outputRoot == gameRoot
    || outputRoot == Path.GetPathRoot(outputRoot))
    throw new InvalidOperationException("Live-kit output must be a dedicated directory, not a repository/game/filesystem root.");

string gameVersion = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(repoRoot, "coopbot", "CoopBot.json")))
    .RootElement.GetProperty("min_game_version").GetString()
    ?? throw new InvalidOperationException("CoopBot manifest has no min_game_version.");
string gameData = Path.Combine(
    gameRoot,
    OperatingSystem.IsWindows() ? "data_sts2_windows_x86_64" : "data_sts2_linuxbsd_x86_64");
string combatBuild = Path.Combine(repoRoot, ".godot", "mono", "temp", "bin", "Release", "CombatSolver.dll");
string cleanerBuild = Path.Combine(
    repoRoot,
    "tools",
    "CombatSolver.MemoryCleaner",
    "bin",
    "Release",
    "net48",
    "CombatSolver.MemoryCleaner.exe");
string coopBuild = Path.Combine(
    repoRoot,
    "coopbot",
    ".godot",
    "mono",
    "temp",
    "bin",
    "Release",
    "CoopBot.dll");

var copies = new (string Source, string Mod, string Name)[]
{
    (combatBuild, "CombatSolver", "CombatSolver.dll"),
    (cleanerBuild, "CombatSolver", "CombatSolver.MemoryCleaner.exe"),
    (Path.Combine(repoRoot, "CombatSolver.json"), "CombatSolver", "CombatSolver.json"),
    (Path.Combine(repoRoot, "LICENSE"), "CombatSolver", "LICENSE"),
    (Path.Combine(repoRoot, "THIRD_PARTY_NOTICES.md"), "CombatSolver", "THIRD_PARTY_NOTICES.md"),
    (coopBuild, "CoopBot", "CoopBot.dll"),
    (Path.Combine(repoRoot, "coopbot", "CoopBot.json"), "CoopBot", "CoopBot.json"),
    (Path.Combine(repoRoot, "LICENSE"), "CoopBot", "LICENSE"),
    (Path.Combine(repoRoot, "THIRD_PARTY_NOTICES.md"), "CoopBot", "THIRD_PARTY_NOTICES.md"),
};
foreach (var copy in copies)
{
    if (!File.Exists(copy.Source))
        throw new FileNotFoundException("Required live-kit input is missing.", copy.Source);
    string destination = Path.Combine(outputRoot, "mods", copy.Mod, copy.Name);
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    File.Copy(copy.Source, destination, overwrite: true);
}

string sts2 = Path.Combine(gameData, "sts2.dll");
string ritsu = Path.Combine(ritsuRoot, "compat", gameVersion, "STS2-RitsuLib.dll");
string ritsuRuntime = Path.Combine(ritsuRoot, "compat", gameVersion, "STS2-RitsuLib.Runtime.dll");
foreach (string dependency in new[] { sts2, ritsu, ritsuRuntime })
{
    if (!File.Exists(dependency))
        throw new FileNotFoundException("Required live-test dependency is missing.", dependency);
}

LiveKitFile[] files = copies
    .Select(copy => Path.Combine(outputRoot, "mods", copy.Mod, copy.Name))
    .Order(StringComparer.Ordinal)
    .Select(path => Describe(path, Path.GetRelativePath(outputRoot, path)))
    .ToArray();
LiveKitDependency[] dependencies =
[
    Dependency("sts2", gameVersion, sts2),
    Dependency("STS2-RitsuLib", gameVersion, ritsu),
    Dependency("STS2-RitsuLib.Runtime", gameVersion, ritsuRuntime),
];
LiveKitManifest manifest = new(
    SchemaVersion: 1,
    GeneratedAtUtc: DateTimeOffset.UtcNow,
    GameVersion: gameVersion,
    ProtocolVersion: 2,
    Files: files,
    Dependencies: dependencies);
Directory.CreateDirectory(outputRoot);
string manifestPath = Path.Combine(outputRoot, "live-test-manifest.json");
File.WriteAllBytes(manifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = true,
}));

if (deploy)
{
    foreach (var copy in copies)
    {
        string source = Path.Combine(outputRoot, "mods", copy.Mod, copy.Name);
        string destination = Path.Combine(gameRoot, "mods", copy.Mod, copy.Name);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, overwrite: true);
    }
}

Console.WriteLine(JsonSerializer.Serialize(new
{
    status = "Passed",
    outputDirectory = outputRoot,
    manifest = manifestPath,
    fileCount = files.Length,
    deployed = deploy,
}));

static string Full(string path) => Path.GetFullPath(path.Trim());

static string Sha256(string path)
    => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

static LiveKitFile Describe(string path, string relative)
    => new(relative.Replace('\\', '/'), new FileInfo(path).Length, Sha256(path));

static LiveKitDependency Dependency(string name, string version, string path)
    => new(name, version, Sha256(path));

internal sealed record LiveKitFile(string Path, long Bytes, string Sha256);
internal sealed record LiveKitDependency(string Name, string Version, string Sha256);
internal sealed record LiveKitManifest(
    int SchemaVersion,
    DateTimeOffset GeneratedAtUtc,
    string GameVersion,
    int ProtocolVersion,
    IReadOnlyList<LiveKitFile> Files,
    IReadOnlyList<LiveKitDependency> Dependencies);
