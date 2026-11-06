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
        throw new GovernanceException("Usage: audit|layout|verify|sql-corpus|stage-profile|admin-openapi|perf-compare|code-delta|release-version|depth-probe|paired-report (see workflow for options)");

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
        case "sql-corpus":
        {
            var manifest = Option(args, "--manifest");
            var target = Option(args, "--output");
            var report = SqlCorpusLedger.Build(manifest);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
            var json = JsonSerializer.Serialize(report, options) + "\n";
            File.WriteAllText(target, json);
            using var document = JsonDocument.Parse(json);
            Console.WriteLine($"Verified {document.RootElement.GetProperty("suites").GetArrayLength()} observed SQL corpora / "
                + $"{document.RootElement.GetProperty("totalCases").GetInt32()} cases across 6 providers");
            Console.WriteLine($"Evidence artifact: {target} (no official coverage percentage)");
            break;
        }
        case "stage-profile":
        {
            var count = SqlCoreStageProfile.Verify(Option(args, "--file"));
            Console.WriteLine($"Verified {count} complete compiler-stage provider/workload cases.");
            break;
        }
        case "admin-openapi":
        {
            var count = AdminOpenApiProof.Verify(Option(args, "--file"));
            Console.WriteLine($"Verified {count} Admin REST operations, typed contracts and credential metadata.");
            break;
        }
        case "perf-compare":
        {
            var folder = Option(args, "--directory");
            var report = SqlCorePerfComparison.Compare(folder);
            Console.WriteLine($"Verified six-provider, 48-workload three-pair benchmark baseline: {folder}");
            break;
        }
        case "code-delta":
        {
            var repo = Option(args, "--repo");
            var baseline = Option(args, "--base");
            var candidate = Option(args, "--head");
            var target = Option(args, "--output");
            var result = SqlCoreCodeDelta.Summarize(repo, baseline, candidate);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
            File.WriteAllText(target, JsonSerializer.Serialize(result, options) + "\n");
            Console.WriteLine(SqlCoreCodeDelta.Markdown(result));
            break;
        }
        case "depth-probe":
        {
            var profile = Option(args, "--profile");
            var path = Option(args, "--file");
            Console.WriteLine($"Depth-probe gate: {SqlCoreDepthProbeGate.Verify(profile, path)} observations validated.");
            break;
        }
        case "paired-report":
        {
            var profile = Option(args, "--profile");
            var directory = Option(args, "--directory");
            Console.WriteLine($"Validated {SqlCorePairedReportGate.Verify(profile, directory)} paired {profile} workloads.");
            break;
        }
        default:
            throw new GovernanceException($"Unknown test governance command: {args[0]}");
    }
}
catch (Exception error) when (error is GovernanceException or IOException or UnauthorizedAccessException
                              or System.Xml.XmlException or ArgumentException or JsonException)
{
    Console.Error.WriteLine($"test governance gate failed: {error.Message}");
    Environment.ExitCode = 1;
}
