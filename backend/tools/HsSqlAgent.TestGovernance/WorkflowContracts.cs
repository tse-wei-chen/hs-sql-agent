using System.Text.RegularExpressions;

namespace HsSqlAgent.TestGovernance;

public static class WorkflowContracts
{
    public static string Job(string workflow, string job)
    {
        var lines = workflow.Replace("\r\n", "\n").Split('\n');
        var start = Array.FindIndex(lines, line => line == $"  {job}:");
        if (start < 0) return "";
        var end = Array.FindIndex(lines, start + 1, line => Regex.IsMatch(line, @"^  [A-Za-z][\w-]*:"));
        return string.Join('\n', lines[(start + 1)..(end < 0 ? lines.Length : end)]);
    }

    public static string Step(string job, string selector)
    {
        return Regex.Split(job, @"(?m)(?=^      - (?:name:|uses:))")
            .FirstOrDefault(step => step.Contains(selector, StringComparison.Ordinal)) ?? "";
    }

    public static void VerifyPushParity(string workflow)
    {
        var block = Job(workflow, "sql-syntax-parity");
        foreach (var required in new[]
        {
            "if: github.event_name == 'pull_request' || github.event_name == 'push'",
            "github.event.pull_request.base.sha || (github.event_name == 'push' && github.event.before)",
            " \"$BASE_SHA\" == \"$GITHUB_SHA\" ", "git fetch origin \"$BASE_SHA\" --depth=1"
        })
            if (!block.Contains(required, StringComparison.Ordinal))
                throw new GovernanceException($"Develop/main syntax parity may not be skipped or self-compared: {required}");
    }
}

public sealed record IntegrationAssignment(string Suite, string Job, string Selector);

public static class ServerIntegrationOwnership
{
    public static IntegrationAssignment[] Parse(string manifest)
    {
        var assignments = new List<IntegrationAssignment>();
        foreach (var line in manifest.Replace("\r\n", "\n").Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
            var fields = line.Split('\t');
            if (fields.Length != 3 || !Regex.IsMatch(fields[0], @"^[A-Za-z][A-Za-z0-9_]*$")
                || !Regex.IsMatch(fields[1], @"^[a-z][a-z0-9-]*$")
                || !Regex.IsMatch(fields[2], @"^FullyQualifiedName~[A-Za-z0-9_.]+$"))
                throw new GovernanceException($"Malformed integration assignment: {line}");
            var assignment = new IntegrationAssignment(fields[0], fields[1], fields[2]);
            if (assignments.Contains(assignment)) throw new GovernanceException($"Duplicate integration assignment: {line}");
            assignments.Add(assignment);
        }
        if (assignments.Count == 0) throw new GovernanceException("Empty integration manifest would include destructive tests in the unit shard");
        return assignments.ToArray();
    }

    public static string DefaultFilter(string manifest) => string.Join('&', Parse(manifest).Select(item => item.Suite)
        .Distinct(StringComparer.Ordinal).Select(suite => "FullyQualifiedName!~" + suite));

    public static void Audit(string manifest, string workflow, string testRoot)
    {
        var assignments = Parse(manifest);
        var sources = Directory.EnumerateFiles(testRoot, "*.cs", SearchOption.AllDirectories).Select(File.ReadAllText).ToArray();
        foreach (var item in assignments)
        {
            if (item.Selector != $"FullyQualifiedName~{item.Suite}"
                && !item.Selector.StartsWith($"FullyQualifiedName~{item.Suite}.", StringComparison.Ordinal))
                throw new GovernanceException($"Selector escapes suite: {item}");
            if (!WorkflowContracts.Job(workflow, item.Job).Contains(item.Selector, StringComparison.Ordinal))
                throw new GovernanceException($"No positive selector in owning job: {item}");
            if (!sources.Any(source => Regex.IsMatch(source, @"class\s+" + Regex.Escape(item.Suite) + @"([^\w]|$)")))
                throw new GovernanceException($"Stale integration class: {item.Suite}");
        }
        foreach (var suite in new[] { "PostgresNativeFaultTestcontainersTests", "RedisProcessOutageTestcontainersTests",
                     "NetworkFaultTestcontainersTests", "ProviderTcpPartitionTestcontainersTests", "RedisIndependentPrimariesTestcontainersTests" })
            if (!assignments.Any(item => item.Suite == suite)) throw new GovernanceException($"Missing fault suite: {suite}");
        if (!workflow.Contains("server-integration --default-filter", StringComparison.Ordinal))
            throw new GovernanceException("Default server shard is disconnected from its audited manifest");
        if (!workflow.Contains("physical-tests 5", StringComparison.Ordinal)
            || Regex.Matches(workflow, "physical-tests 1(?:\\s|$)").Count < 2)
            throw new GovernanceException("Backend/process and isolated TCP-partition case gates are missing");

        void Require(string suite, string job, string selector, int expected)
        {
            if (!assignments.Contains(new(suite, job, selector))) throw new GovernanceException($"Missing required assignment: {suite}/{job}");
            var step = WorkflowContracts.Step(WorkflowContracts.Job(workflow, job), selector);
            if (!Regex.IsMatch(step, @"physical-tests " + expected + @"(?:\s|$)"))
                throw new GovernanceException($"Missing exact {expected}-case no-skip gate: {selector}/{job}");
        }
        foreach (var (job, provider, count) in new[] { ("sqlagent-test", "MySql", 3), ("sqlagent-fence-sqlserver", "SqlServer", 2),
                     ("sqlagent-oracle-ddl-parity", "Oracle", 4), ("sqlagent-firebird-ddl-parity", "Firebird", 1) })
        {
            Require("ProviderTcpPartitionTestcontainersTests", job, $"FullyQualifiedName~ProviderTcpPartitionTestcontainersTests.{provider}_", 1);
            Require("QueryNativeFunctionLiveTests", job, $"FullyQualifiedName~QueryNativeFunctionLiveTests.{provider}_", count);
        }
        Require("RedisIndependentPrimariesTestcontainersTests", "server-test", "FullyQualifiedName~RedisIndependentPrimariesTestcontainersTests", 1);
        foreach (var suite in new[] { "PostgresPhysicalHostInjectionTests", "SqlitePhysicalHostInjectionTests" })
            Require(suite, "server-test", $"FullyQualifiedName~{suite}", 11);
        Console.WriteLine($"Server integration ownership passed: {assignments.Select(item => item.Suite).Distinct().Count()} suites, {assignments.Length} assignments");
    }
}
