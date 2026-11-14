using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HsSqlAgent.TestGovernance;
using Xunit;

namespace Infrastructure.Test;

public sealed class CiGovernanceTests
{
    private static string Root
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "backend", "Directory.Build.props")))
                directory = directory.Parent;
            return directory?.FullName ?? throw new InvalidOperationException("Repository root not found");
        }
    }
    private static readonly ReleaseIdentity Identity = new("tse-wei-chen/hs-sql-agent", new string('a', 40), "offline-token");
    private static string Workflow => File.ReadAllText(Path.Combine(Root, ".github", "workflows", "test.yml"));
    private static string Manifest => GovernanceResources.Read("server-integration.tsv");
    private static string TestRoot => Path.Combine(Root, "backend", "src", "UnitTest", "HsSqlAgent.Server.Test");

    [Fact]
    public void PackageConsumerSmoke_EmbedsItsSourceInTheMainGovernanceAssembly()
    {
        // The .cs part of PackageConsumer.cs.txt is a valid language tag.
        // Without an explicit neutral resource, the SDK may move it into
        // a culture satellite and package-smoke cannot find the source.
        var source = GovernanceResources.Read("PackageConsumer.cs.txt");
        Assert.Contains("var services = new ServiceCollection();", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SqlCoreSyntaxDepthGates_UseExplicitGovernanceTargetFramework()
    {
        var missingFramework = Workflow.Split('\n')
            .Where(line => line.Contains("dotnet run", StringComparison.Ordinal)
                && line.Contains("backend/tools/HsSqlAgent.TestGovernance", StringComparison.Ordinal)
                && !line.Contains("--framework net10.0", StringComparison.Ordinal))
            .ToArray();
        Assert.Empty(missingFramework);
    }

    [Fact]
    public void SharedMetadata_AcceptsCanonicalVersionAndRejectsMissingDuplicateOrMalformedValues()
    {
        using var fixture = new Fixture();
        var version = ReleaseMetadata.Read(Root, "VersionPrefix");
        var file = Path.Combine(fixture.Root, "backend", "Directory.Build.props");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, $"<Project><VersionPrefix>{version}</VersionPrefix></Project>");
        Assert.Equal(version, ReleaseMetadata.Read(fixture.Root, "VersionPrefix"));
        foreach (var xml in new[] { "<Project/>", "<Project><VersionPrefix /></Project>",
                     $"<Project><VersionPrefix>{version}-rc.1</VersionPrefix></Project>",
                     $"<Project><VersionPrefix>{version}</VersionPrefix><VersionPrefix>{version}</VersionPrefix></Project>" })
        {
            File.WriteAllText(file, xml);
            Assert.Throws<GovernanceException>(() => ReleaseMetadata.Read(fixture.Root, "VersionPrefix"));
        }
    }

    [Theory]
    [InlineData("clean", true)]
    [InlineData("missing-nuget", false)]
    [InlineData("nuget-high", false)]
    [InlineData("nuget-numeric-critical", false)]
    [InlineData("node-high", false)]
    [InlineData("node-critical", false)]
    [InlineData("registry-error", false)]
    [InlineData("missing-node", false)]
    [InlineData("moderate", true)]
    public void DependencyAdvisories_PreserveAllFormerPositiveAndNegativeControls(string scenario, bool accepted)
    {
        var nuget = JsonNode.Parse("""{"version":1,"projects":[{"frameworks":[{"topLevelPackages":[{"vulnerabilities":[]}],"transitivePackages":[]}]}]}""")!;
        var node = JsonNode.Parse("""{"metadata":{"vulnerabilities":{"low":0,"moderate":0,"high":0,"critical":0}}}""")!;
        if (scenario == "missing-nuget") nuget = new JsonObject();
        if (scenario.StartsWith("nuget-", StringComparison.Ordinal) || scenario == "moderate")
            nuget["projects"]![0]!["frameworks"]![0]!["topLevelPackages"]![0]!["vulnerabilities"] =
                new JsonArray(new JsonObject { ["severity"] = scenario == "nuget-numeric-critical" ? JsonValue.Create(3)
                    : JsonValue.Create(scenario == "moderate" ? "Moderate" : "High") });
        if (scenario is "node-high" or "node-critical") node["metadata"]!["vulnerabilities"]![scenario[5..]] = 1;
        if (scenario == "registry-error") node = new JsonObject { ["error"] = "registry unavailable" };
        if (scenario == "missing-node") node = new JsonObject();
        var error = Record.Exception(() => DependencySecurity.Verify(nuget.ToJsonString(), node.ToJsonString()));
        if (accepted) Assert.Null(error); else Assert.IsType<GovernanceException>(error);
    }

    [Theory]
    [InlineData("open", "completed", "- [x] done")]
    [InlineData("unexpected", "completed", "- [x] done")]
    [InlineData("closed", "not_planned", "- [x] done")]
    [InlineData("closed", "completed", "")]
    [InlineData("closed", "completed", "- [ ] outstanding")]
    [InlineData("closed", "completed", "- [x] reviewed\n- [ ] outstanding")]
    public void ReleaseIssues_RejectIncompleteEvidence(string state, string reason, string body) =>
        Assert.Throws<GovernanceException>(() => ReleaseEvidence.VerifyIssue(new JsonObject
        { ["state"] = state, ["state_reason"] = reason, ["body"] = body }));

    [Theory]
    [InlineData(575)]
    [InlineData(634)]
    public void SecurityGate_RequiresBothIndependentIssues_AndIncludesPrereleases(int issue)
    {
        var version = ReleaseMetadata.Read(Root, "SecurityGateVersion");
        foreach (var tag in new[] { version, "v" + version, version + "-rc.1", "v" + version + "+build.1" })
        {
            var github = new FakeGitHub { IncompleteIssue = issue };
            Assert.Throws<GovernanceException>(() => ReleaseEvidence.SecurityGate(Root, tag, Identity, github));
        }
    }

    [Fact]
    public void SecurityGate_AcceptsCompletedIssues_RejectsApiFailureAndInvalidIdentity()
    {
        var version = ReleaseMetadata.Read(Root, "SecurityGateVersion");
        ReleaseEvidence.SecurityGate(Root, version, Identity, new FakeGitHub());
        Assert.Throws<GovernanceException>(() => ReleaseEvidence.SecurityGate(Root, version, Identity, new FakeGitHub { Fail = true }));
        Assert.Throws<GovernanceException>(() => ReleaseEvidence.SecurityGate(Root, version, Identity with { Token = "" }, new FakeGitHub()));
        Assert.Throws<GovernanceException>(() => ReleaseEvidence.SecurityGate(Root, version, Identity with { Repository = "someone/fork" }, new FakeGitHub()));
        var baseline = ReleaseMetadata.Read(Root, "PackageApiBaselineVersion");
        ReleaseEvidence.SecurityGate(Root, baseline, Identity with { Token = "" }, new FakeGitHub { Fail = true });
    }

    [Theory]
    [InlineData("clean", false)]
    [InlineData("wrong-head", true)]
    [InlineData("api-failure", true)]
    [InlineData("missing-run", true)]
    [InlineData("wrong-sha", true)]
    [InlineData("wrong-branch", true)]
    [InlineData("wrong-event", true)]
    [InlineData("newer-failed", true)]
    [InlineData("newer-pending", true)]
    [InlineData("missing-job", true)]
    [InlineData("impostor-job", true)]
    [InlineData("failed-job", true)]
    public void MainHeadGate_RejectsStaleWrongOrIncompleteCi(string scenario, bool rejected)
    {
        var github = new FakeGitHub { Scenario = scenario };
        var error = Record.Exception(() => ReleaseEvidence.MainHeadGate(Identity, github));
        if (rejected) Assert.IsType<GovernanceException>(error); else Assert.Null(error);
    }

    [Theory]
    [InlineData("missing-run")]
    [InlineData("wrong-branch")]
    [InlineData("wrong-sha")]
    [InlineData("newer-pending")]
    public void DevelopCandidate_DistinguishesPendingEvidence(string scenario)
    {
        using var fixture = new Fixture();
        Assert.Throws<CandidatePendingException>(() => ReleaseEvidence.DevelopCandidate(Root, Path.Combine(fixture.Root, "report.json"),
            "refs/heads/develop", Identity, new FakeGitHub { Branch = "develop", Scenario = scenario }));
    }

    [Theory]
    [InlineData("wrong-head")]
    [InlineData("newer-failed")]
    [InlineData("missing-job")]
    [InlineData("impostor-job")]
    [InlineData("failed-job")]
    [InlineData("api-failure")]
    [InlineData("missing-artifact")]
    [InlineData("expired-artifact")]
    public void DevelopCandidate_RejectsInvalidOrMissingEvidence(string scenario)
    {
        using var fixture = new Fixture();
        Assert.Throws<GovernanceException>(() => ReleaseEvidence.DevelopCandidate(Root, Path.Combine(fixture.Root, "report.json"),
            "refs/heads/develop", Identity, new FakeGitHub { Branch = "develop", Scenario = scenario }));
    }

    [Fact]
    public void DevelopCandidate_ProducesReadOnlyProof_AndNeverAuthorizesMain()
    {
        using var fixture = new Fixture();
        var path = Path.Combine(fixture.Root, "report.json");
        var report = ReleaseEvidence.DevelopCandidate(Root, path, "refs/heads/develop", Identity, new FakeGitHub { Branch = "develop" });
        Assert.False(report["publicationAuthorized"]!.GetValue<bool>());
        Assert.False(report["mainPromotionPerformed"]!.GetValue<bool>());
        Assert.Equal(ReleaseEvidence.RequiredJobs.Length, report["testsJobsPassed"]!.GetValue<int>());
        Assert.True(File.Exists(path));
        Assert.Throws<GovernanceException>(() => ReleaseEvidence.DevelopCandidate(Root, path, "refs/heads/main", Identity, new FakeGitHub()));
        Assert.Throws<GovernanceException>(() => ReleaseEvidence.DevelopCandidate(Root, path, "refs/heads/develop", Identity with { Token = "" }, new FakeGitHub()));
    }

    [Theory]
    [InlineData("valid", true)]
    [InlineData("wrong-job", false)]
    [InlineData("stale-class", false)]
    [InlineData("duplicate", false)]
    [InlineData("missing-fault", false)]
    [InlineData("missing-selector", false)]
    [InlineData("disconnected-filter", false)]
    [InlineData("missing-count", false)]
    [InlineData("missing-oracle", false)]
    [InlineData("firebird-count", false)]
    [InlineData("missing-query", false)]
    [InlineData("missing-postgres", false)]
    [InlineData("sqlite-count", false)]
    [InlineData("missing-splitbrain", false)]
    [InlineData("empty", false)]
    public void IntegrationOwnership_PreservesManifestAndExactCaseContracts(string scenario, bool accepted)
    {
        var manifest = Manifest;
        var workflow = Workflow;
        string RemoveSuite(string suite, string? job = null) => string.Join('\n', manifest.Split('\n')
            .Where(line => !line.StartsWith(suite + "\t", StringComparison.Ordinal) || (job is not null && !line.Contains("\t" + job + "\t", StringComparison.Ordinal))));
        switch (scenario)
        {
            case "wrong-job": manifest = manifest.Replace("RedisDmlApprovalIntegrationTests\tredis-approval-integration", "RedisDmlApprovalIntegrationTests\tserver-test", StringComparison.Ordinal); break;
            case "stale-class": manifest = manifest.Replace("RedisDmlApprovalIntegrationTests", "RedisDmlApprovalIntegrationTestsRenamed", StringComparison.Ordinal); break;
            case "duplicate": manifest += "\n" + manifest.Split('\n').First(line => line.StartsWith("RedisDmlApprovalIntegrationTests\t", StringComparison.Ordinal)); break;
            case "missing-fault": manifest = RemoveSuite("PostgresNativeFaultTestcontainersTests"); break;
            case "missing-selector": workflow = workflow.Replace("FullyQualifiedName~RedisProcessOutageTestcontainersTests", "NO_CASES", StringComparison.Ordinal); break;
            case "disconnected-filter": workflow = workflow.Replace("server-integration --default-filter", "NO_FILTER", StringComparison.Ordinal); break;
            case "missing-count": workflow = workflow.Replace("physical-tests 1", "physical-tests 0", StringComparison.Ordinal); break;
            case "missing-oracle": manifest = RemoveSuite("ProviderTcpPartitionTestcontainersTests", "sqlagent-oracle-ddl-parity"); break;
            case "firebird-count":
                var block = WorkflowContracts.Job(workflow, "sqlagent-firebird-ddl-parity");
                workflow = workflow.Replace(block, block.Replace("physical-tests 1", "physical-tests 0", StringComparison.Ordinal), StringComparison.Ordinal); break;
            case "missing-query": manifest = RemoveSuite("QueryNativeFunctionLiveTests", "sqlagent-test"); break;
            case "missing-postgres": manifest = RemoveSuite("PostgresPhysicalHostInjectionTests"); break;
            case "sqlite-count":
                var step = WorkflowContracts.Step(WorkflowContracts.Job(workflow, "server-test"), "FullyQualifiedName~SqlitePhysicalHostInjectionTests");
                workflow = workflow.Replace(step, step.Replace("physical-tests 11", "physical-tests 0", StringComparison.Ordinal), StringComparison.Ordinal); break;
            case "missing-splitbrain": manifest = RemoveSuite("RedisIndependentPrimariesTestcontainersTests"); break;
            case "empty": manifest = ""; break;
        }
        var error = Record.Exception(() => ServerIntegrationOwnership.Audit(manifest, workflow, TestRoot));
        if (accepted) Assert.Null(error); else Assert.IsType<GovernanceException>(error);
    }

    [Fact]
    public void DefaultFilter_ExcludesEveryOwnedIntegrationSuiteInManifestOrder()
    {
        var filter = ServerIntegrationOwnership.DefaultFilter(Manifest);
        foreach (var item in ServerIntegrationOwnership.Parse(Manifest)) Assert.Contains("FullyQualifiedName!~" + item.Suite, filter, StringComparison.Ordinal);
    }

    [Fact]
    public void PushParity_PreservesBothBranchTriggersAndRejectsFourFalseGreenVariants()
    {
        WorkflowContracts.VerifyPushParity(Workflow);
        foreach (var required in new[] { "if: github.event_name == 'pull_request' || github.event_name == 'push'",
                     "github.event.pull_request.base.sha || (github.event_name == 'push' && github.event.before)",
                     " \"$BASE_SHA\" == \"$GITHUB_SHA\" ", "git fetch origin \"$BASE_SHA\" --depth=1" })
            Assert.Throws<GovernanceException>(() => WorkflowContracts.VerifyPushParity(Workflow.Replace(required, "REMOVED", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("net8.0", 6)]
    [InlineData("net10.0", 11)]
    public void PhysicalEvidence_DeduplicatesSummary_AndRejectsSkipsWrongCountsOrMissingFramework(string framework, int expected)
    {
        var line = $"suite.dll ({framework}|x64) passed [+{expected}/x0/?0]";
        PhysicalTestCases.Verify(line + "\n" + line, framework, expected);
        foreach (var invalid in new[] { "", line.Replace("/x0", "/x1", StringComparison.Ordinal), line.Replace("/?0", "/?1", StringComparison.Ordinal), line.Replace($"+{expected}", $"+{expected + 1}", StringComparison.Ordinal) })
            Assert.Throws<GovernanceException>(() => PhysicalTestCases.Verify(invalid, framework, expected));
    }

    [Fact]
    public void SyntaxParity_RejectsLostCasesAndStaleAllowlist()
    {
        using var fixture = new Fixture();
        var baseline = Path.Combine(fixture.Root, "base.json");
        var head = Path.Combine(fixture.Root, "head.json");
        var allowed = Path.Combine(fixture.Root, "allowed.json");
        const string outcome = """[{"name":"query","success":true,"stage":"compile","semantic":{"statementFamily":"Select","cteCount":0,"setOperationCount":0,"subqueryCount":0,"namedTableReferences":["items"],"commandKind":"Query","returnsRows":true}}]""";
        File.WriteAllText(baseline, outcome);
        File.WriteAllText(head, outcome);
        File.WriteAllText(allowed, "[]");
        Assert.Equal(0, SqlSyntaxParity.Execute(["compare", baseline, head, allowed]));
        File.WriteAllText(head, "[]");
        Assert.Equal(1, SqlSyntaxParity.Execute(["compare", baseline, head, allowed]));
        File.WriteAllText(head, outcome);
        File.WriteAllText(allowed, "[\"stale\"]");
        Assert.Equal(1, SqlSyntaxParity.Execute(["compare", baseline, head, allowed]));
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("hssqlagent-governance-").FullName;
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed class FakeGitHub : IGitHubEvidence
    {
        public string Branch { get; init; } = "main";
        public string Scenario { get; init; } = "clean";
        public int IncompleteIssue { get; init; }
        public bool Fail { get; init; }
        public JsonNode Get(string path)
        {
            if (Fail || Scenario == "api-failure") throw new GovernanceException("Offline API failure");
            if (path.Contains("/issues/", StringComparison.Ordinal)) return new JsonObject
            { ["state"] = path.EndsWith('/' + IncompleteIssue.ToString(), StringComparison.Ordinal) ? "open" : "closed", ["state_reason"] = "completed", ["body"] = "- [x] completed" };
            if (path.Contains("/branches/", StringComparison.Ordinal)) return new JsonObject { ["commit"] = new JsonObject { ["sha"] = Scenario == "wrong-head" ? new string('b', 40) : Identity.Sha } };
            if (path.Contains("/runs?", StringComparison.Ordinal))
            {
                JsonObject Run(long id, string status = "completed", string conclusion = "success") => new()
                { ["id"] = id, ["head_sha"] = Scenario == "wrong-sha" ? new string('b', 40) : Identity.Sha,
                    ["head_branch"] = Scenario == "wrong-branch" ? "other" : Branch, ["event"] = Scenario == "wrong-event" ? "pull_request" : "push",
                    ["status"] = status, ["conclusion"] = conclusion };
                var runs = new JsonArray();
                if (Scenario != "missing-run") runs.Add(Run(100));
                if (Scenario == "newer-failed") runs.Add(Run(101, conclusion: "failure"));
                if (Scenario == "newer-pending") runs.Add(Run(101, status: "in_progress"));
                return new JsonObject { ["workflow_runs"] = runs };
            }
            if (path.Contains("/jobs?", StringComparison.Ordinal))
            {
                var jobs = new JsonArray();
                foreach (var name in ReleaseEvidence.RequiredJobs.Skip(Scenario == "missing-job" ? 1 : 0))
                    jobs.Add(new JsonObject { ["name"] = Scenario == "impostor-job" && jobs.Count == 0 ? "impostor" : name, ["status"] = "completed", ["conclusion"] = Scenario == "failed-job" ? "failure" : "success" });
                return new JsonObject { ["total_count"] = jobs.Count, ["jobs"] = jobs };
            }
            if (path.Contains("/artifacts?", StringComparison.Ordinal)) return new JsonObject { ["artifacts"] = Scenario == "missing-artifact" ? new JsonArray() : new JsonArray(new JsonObject
            { ["name"] = "security-dependency-advisory-evidence", ["expired"] = Scenario == "expired-artifact", ["size_in_bytes"] = 128L }) };
            throw new GovernanceException("Unexpected API path: " + path);
        }
    }
}
