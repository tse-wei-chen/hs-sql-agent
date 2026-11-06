using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace HsSqlAgent.TestGovernance;

/// <summary>
/// Fail-closed, dependency-free CI checks. SQL grammar corpus counts are not code coverage.
/// Each reported coverage percentage is scoped to that xUnit run's instrumented assemblies.
/// </summary>
public static class Governance
{
    private static readonly string[] MeasuredJobs =
    [
        "common-test", "infrastructure-test", "auth-test", "admin-test",
    ];

    private static readonly Regex TestProjectInvocation = new(
        @"dotnet test\s+--project\s+(?<path>backend/src/UnitTest/[\w.]+/[\w.]+\.csproj)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex NoOpAssertion = new(
        @"\bAssert\s*\.\s*True\s*\(\s*true\s*\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex WorkflowJobHeader = new(
        @"(?m)^  (?<job>[a-zA-Z][\w-]*):[ \t]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static OwnershipReport Audit(string repositoryRoot)
    {
        var root = Path.GetFullPath(repositoryRoot);
        var unitTests = Path.Combine(root, "backend", "src", "UnitTest");
        if (!Directory.Exists(unitTests))
            throw new GovernanceException($"Missing test root: {unitTests}");

        string[] projects = Directory.EnumerateDirectories(unitTests)
            .SelectMany(folder => Directory.EnumerateFiles(folder, "*.csproj", SearchOption.TopDirectoryOnly))
            .Select(file => Relative(root, file))
            .Order(StringComparer.Ordinal)
            .ToArray();

        if (projects.Length == 0)
            throw new GovernanceException("No backend xUnit projects were found");

        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "test.yml"));
        var invoked = TestProjectInvocation.Matches(workflow)
            .Select(match => match.Groups["path"].Value)
            .ToHashSet(StringComparer.Ordinal);

        var errors = projects.Where(project => !invoked.Contains(project))
            .Select(project => $"No CI test invocation for: {project}")
            .ToList();

        foreach (var job in MeasuredJobs)
        {
            var body = JobBody(workflow, job);
            if (body is null)
            {
                errors.Add($"Missing measured offline test job: {job}");
                continue;
            }

            if (!body.Contains("--coverlet", StringComparison.Ordinal)
                || !body.Contains("--report-xunit-trx", StringComparison.Ordinal)
                || !body.Contains($"--results-directory artifacts/test-evidence/{job}", StringComparison.Ordinal)
                || !body.Contains("HsSqlAgent.TestGovernance", StringComparison.Ordinal)
                || !body.Contains($"verify --directory artifacts/test-evidence/{job}", StringComparison.Ordinal))
            {
                errors.Add($"{job}: missing native MTP TRX/Cobertura collection or validation");
            }
        }

        foreach (var file in Directory.EnumerateFiles(unitTests, "*.cs", SearchOption.AllDirectories))
        {
            if (NoOpAssertion.IsMatch(File.ReadAllText(file)))
                errors.Add($"No-op test assertion: {Relative(root, file)}");
        }

        if (errors.Count > 0)
            throw new GovernanceException(string.Join(Environment.NewLine, errors));

        return new OwnershipReport(projects, MeasuredJobs);
    }

    public static string[] FlatServiceTests(string repositoryRoot)
    {
        var root = Path.GetFullPath(repositoryRoot);
        var results = new List<string>();
        foreach (var project in new[] { "SqlAgent.Test", "HsSqlAgent.Server.Test" })
        {
            var directory = Path.Combine(root, "backend", "src", "UnitTest", project, "Services");
            if (Directory.Exists(directory))
                results.AddRange(Directory.EnumerateFiles(directory, "*.cs", SearchOption.TopDirectoryOnly)
                    .Select(file => Relative(root, file)));
        }
        return results.Order(StringComparer.Ordinal).ToArray();
    }

    public static EvidenceReport Verify(string directory, string suite)
    {
        if (string.IsNullOrWhiteSpace(suite))
            throw new GovernanceException("An explicit test suite name is required");

        var trx = XDocument.Load(ExactlyOne(directory, f => f.EndsWith(".trx", StringComparison.OrdinalIgnoreCase), "*.trx"));
        var counters = trx.Descendants().FirstOrDefault(
            e => e.Name.LocalName == "Counters" && e.Parent?.Name.LocalName == "ResultSummary");
        if (counters is null)
            throw new GovernanceException("TRX is missing ResultSummary/Counters");

        var total = IntAttribute(counters, "total");
        var executed = IntAttribute(counters, "executed");
        var passed = IntAttribute(counters, "passed");
        var failed = IntAttribute(counters, "failed");
        var notExecuted = IntAttribute(counters, "notExecuted", required: false);

        if (!(total > 0 && executed > 0 && passed > 0 && failed == 0
              && total >= executed && executed >= passed && notExecuted >= 0))
        {
            throw new GovernanceException(
                $"{suite}: empty or failed test run (total={total}, executed={executed}, passed={passed}, failed={failed})");
        }

        var coverage = XDocument.Load(ExactlyOne(directory,
            f => Path.GetFileName(f).StartsWith("coverage.cobertura", StringComparison.Ordinal)
                 && f.EndsWith(".xml", StringComparison.OrdinalIgnoreCase),
            "coverage.cobertura*.xml"));
        var root = coverage.Root;
        if (root?.Name.LocalName != "coverage")
            throw new GovernanceException("Expected a Cobertura coverage root");

        var linesValid = IntAttribute(root, "lines-valid");
        var linesCovered = IntAttribute(root, "lines-covered");
        if (linesValid <= 0 || linesCovered < 0 || linesCovered > linesValid)
            throw new GovernanceException("Cobertura has no measurable production lines or invalid counts");

        return new EvidenceReport(
            suite, total, executed, passed, failed, notExecuted,
            linesCovered, linesValid, Math.Round(100d * linesCovered / linesValid, 2),
            "This suite's instrumented assemblies only; not whole-repository coverage");
    }

    private static string? JobBody(string workflow, string name)
    {
        var headers = WorkflowJobHeader.Matches(workflow);
        for (var i = 0; i < headers.Count; i++)
        {
            if (!string.Equals(headers[i].Groups["job"].Value, name, StringComparison.Ordinal))
                continue;
            var start = headers[i].Index + headers[i].Length;
            var end = i + 1 < headers.Count ? headers[i + 1].Index : workflow.Length;
            return workflow[start..end];
        }
        return null;
    }

    private static string Relative(string root, string file) =>
        Path.GetRelativePath(root, file).Replace('\\', '/');

    private static string ExactlyOne(string directory, Func<string, bool> match, string pattern)
    {
        if (!Directory.Exists(directory))
            throw new GovernanceException($"Expected exactly one {pattern} in {directory}, found 0");

        var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Where(match).Take(2).ToArray();
        if (files.Length != 1)
            throw new GovernanceException($"Expected exactly one {pattern} in {directory}, found {files.Length}{(files.Length == 2 ? " or more" : "")}");
        return files[0];
    }

    private static int IntAttribute(XElement element, string name, bool required = true)
    {
        var value = (string?)element.Attribute(name);
        if (!required && value is null)
            return 0;
        if (value is null || !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            throw new GovernanceException($"Invalid or missing XML counter: {name}");
        return parsed;
    }
}

public sealed record OwnershipReport(string[] ProjectsWithCiOwner, string[] MeasuredJobs);
public sealed record EvidenceReport(
    string Suite, int Total, int Executed, int Passed, int Failed, int NotExecuted,
    int LinesCovered, int LinesValid, double LineCoveragePercent, string Scope);
public sealed class GovernanceException(string message) : Exception(message);
