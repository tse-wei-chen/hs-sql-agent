using System.Text.Json;
using HsSqlAgent.TestGovernance;

static string Option(string[] args, string key, string? fallback = null)
{
    var index = Array.IndexOf(args, key);
    if (index >= 0 && index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
        return args[index + 1];
    return fallback ?? throw new GovernanceException($"Missing required option: {key}");
}

try
{
    if (args.Length == 0)
        throw new GovernanceException("Usage: audit [--root .] | layout [--root .] | verify --directory <path> --suite <name> | release-version --root . --tag vX.Y.Z");

    var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
    switch (args[0])
    {
        case "release-version":
            Console.WriteLine($"Verified release v{ReleaseVersionGate.Verify(Option(args, "--root", "."), Option(args, "--tag"))}");
            break;
        case "audit":
            Console.WriteLine(JsonSerializer.Serialize(Governance.Audit(Option(args, "--root", ".")), options));
            break;
        case "layout":
        {
            var violations = Governance.FlatServiceTests(Option(args, "--root", "."));
            if (violations.Length > 0)
                throw new GovernanceException($"Test layout violation: {violations.Length} file(s) directly in Services/:\n"
                    + string.Join("\n", violations.Take(25).Select(path => " - " + path))
                    + "\nChoose a domain subdirectory; preserve test identities.");
            Console.WriteLine("Test layout OK: no C# files directly in the legacy Services/ buckets.");
            break;
        }
        case "verify":
        {
            var directory = Option(args, "--directory");
            var evidence = Governance.Verify(directory, Option(args, "--suite"));
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "summary.json"), JsonSerializer.Serialize(evidence, options) + "\n");
            Console.WriteLine(JsonSerializer.Serialize(evidence, options));

            var summaryPath = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
            if (!string.IsNullOrWhiteSpace(summaryPath) && File.Exists(summaryPath))
                File.AppendAllText(summaryPath,
                    $"\n### {evidence.Suite} — measured test evidence\n"
                    + $"Passed: {evidence.Passed} / {evidence.Total} (not executed: {evidence.NotExecuted}). "
                    + $"Instrumented line coverage: {evidence.LineCoveragePercent:F2}% "
                    + $"({evidence.LinesCovered}/{evidence.LinesValid}).\n\n"
                    + "This is **suite-scoped**, not whole-repository code coverage.\n");
            break;
        }
        default:
            throw new GovernanceException($"Unknown test governance command: {args[0]}");
    }
}
catch (Exception error) when (error is GovernanceException or IOException or UnauthorizedAccessException
                              or System.Xml.XmlException or ArgumentException)
{
    Console.Error.WriteLine($"test governance gate failed: {error.Message}");
    Environment.ExitCode = 1;
}
