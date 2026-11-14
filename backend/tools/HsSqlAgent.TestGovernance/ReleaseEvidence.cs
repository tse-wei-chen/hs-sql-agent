using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace HsSqlAgent.TestGovernance;

public interface IGitHubEvidence { JsonNode Get(string path); }

public sealed class GitHubEvidence : IGitHubEvidence
{
    public JsonNode Get(string path) => JsonNode.Parse(CommandProcess.Run("gh", ["api", path]).Output)
        ?? throw new GovernanceException($"Empty GitHub response: {path}");
}

public sealed class CandidatePendingException(string message) : Exception(message);

public sealed record ReleaseIdentity(string Repository, string Sha, string Token)
{
    public static ReleaseIdentity FromEnvironment() => new(
        Environment.GetEnvironmentVariable("GITHUB_REPOSITORY") ?? "",
        Environment.GetEnvironmentVariable("GITHUB_SHA") ?? "",
        Environment.GetEnvironmentVariable("GH_TOKEN") ?? "");

    public void Verify(bool requireSha = true)
    {
        if (Repository != "tse-wei-chen/hs-sql-agent" || string.IsNullOrWhiteSpace(Token)
            || (requireSha && !Regex.IsMatch(Sha, "^[0-9a-f]{40}$")))
            throw new GovernanceException("Cannot attest repository, commit or GitHub read credentials");
    }
}

public static class ReleaseEvidence
{
    public static readonly string[] RequiredJobs =
    [
        "admin-api-openapi", "admin-test", "auth-test", "common-test", "frontend-lint", "frontend-test",
        "infrastructure-test", "redis-approval-integration", "server-test", "sql-syntax-parity",
        "sqlagent-capability-stack", "sqlagent-fence-sqlserver", "sqlagent-firebird-ddl-parity",
        "sqlagent-oracle-ddl-parity", "sqlagent-package-smoke", "sqlagent-syntax-matrix", "sqlagent-test", "sqlagent-test-rest"
    ];

    private static string Text(JsonNode? node, string key) => node?[key]?.ToString() ?? "";

    public static void VerifyIssue(JsonNode issue)
    {
        var body = Text(issue, "body");
        var lines = body.Split('\n');
        if (Text(issue, "state") != "closed" || Text(issue, "state_reason") != "completed"
            || !lines.Any(line => Regex.IsMatch(line, @"^[ \t]*[-*][ \t]+\[[xX]\]"))
            || lines.Any(line => Regex.IsMatch(line, @"^[ \t]*[-*][ \t]+\[[ \t]+\]")))
            throw new GovernanceException("Release issue must be completed with a nonempty, fully checked checklist");
    }

    public static void SecurityGate(string root, string version, ReleaseIdentity identity, IGitHubEvidence github)
    {
        var protectedVersion = ReleaseMetadata.Read(root, "SecurityGateVersion");
        var releaseBase = version.StartsWith('v') ? version[1..] : version;
        releaseBase = releaseBase.Split('-', '+')[0];
        if (releaseBase != protectedVersion)
        {
            Console.WriteLine($"Security gate applies to {protectedVersion}; {version} is outside its scope");
            return;
        }
        identity.Verify(requireSha: false);
        foreach (var issue in new[] { 575, 634 })
            VerifyIssue(github.Get($"repos/{identity.Repository}/issues/{issue}"));
        Console.WriteLine("Develop security and owner publication issues are completed; exact-main CI remains mandatory");
    }

    public static JsonNode LatestRun(JsonNode response, string sha, string branch)
    {
        if (response["workflow_runs"] is not JsonArray runs)
            throw new GovernanceException("Missing workflow run evidence");
        var run = runs.Where(run => Text(run, "head_sha") == sha && Text(run, "head_branch") == branch
                && Text(run, "event") == "push")
            .OrderByDescending(run => run?["id"]?.GetValue<long>() ?? throw new GovernanceException("Invalid run identity"))
            .FirstOrDefault();
        return run ?? throw new CandidatePendingException("No exact-branch push workflow run");
    }

    public static void VerifyJobs(JsonNode response)
    {
        if (response["total_count"]?.GetValue<int>() != RequiredJobs.Length || response["jobs"] is not JsonArray jobs
            || jobs.Count != RequiredJobs.Length || !jobs.Select(job => Text(job, "name")).Order(StringComparer.Ordinal)
                .SequenceEqual(RequiredJobs.Order(StringComparer.Ordinal))
            || jobs.Any(job => Text(job, "status") != "completed" || Text(job, "conclusion") != "success"))
            throw new GovernanceException("Release requires all exact named Tests jobs, completed successfully");
    }

    private static long VerifyBranch(ReleaseIdentity identity, IGitHubEvidence github, string branch, bool pendingAllowed)
    {
        identity.Verify();
        var head = github.Get($"repos/{identity.Repository}/branches/{branch}");
        if (head["commit"]?["sha"]?.ToString() != identity.Sha)
            throw new GovernanceException($"Evidence SHA is not the current {branch} HEAD");
        JsonNode run;
        try
        {
            run = LatestRun(github.Get($"repos/{identity.Repository}/actions/workflows/test.yml/runs?head_sha={identity.Sha}&branch={branch}&event=push&per_page=100"), identity.Sha, branch);
        }
        catch (CandidatePendingException) when (!pendingAllowed)
        { throw new GovernanceException("No exact main push Tests run"); }
        if (Text(run, "status") != "completed")
        {
            if (pendingAllowed) throw new CandidatePendingException("Tests are still running");
            throw new GovernanceException("Tests have not completed");
        }
        if (Text(run, "conclusion") != "success") throw new GovernanceException("Newest Tests run is unsuccessful");
        var id = run["id"]!.GetValue<long>();
        VerifyJobs(github.Get($"repos/{identity.Repository}/actions/runs/{id}/jobs?per_page=100"));
        return id;
    }

    public static void MainHeadGate(ReleaseIdentity identity, IGitHubEvidence github)
    {
        var run = VerifyBranch(identity, github, "main", pendingAllowed: false);
        Console.WriteLine($"Verified main HEAD {identity.Sha}, Tests run {run} and all required jobs");
    }

    public static JsonObject DevelopCandidate(string root, string output, string branchRef, ReleaseIdentity identity, IGitHubEvidence github)
    {
        if (branchRef != "refs/heads/develop") throw new GovernanceException("Only a develop push is eligible");
        var version = ReleaseMetadata.Read(root, "VersionPrefix");
        if (!File.ReadAllLines(Path.Combine(root, "CHANGELOG.md")).Contains($"## [{version}] - Unreleased"))
            throw new GovernanceException("Matching unreleased changelog entry is missing");
        var run = VerifyBranch(identity, github, "develop", pendingAllowed: true);
        VerifyArtifacts(github.Get($"repos/{identity.Repository}/actions/runs/{run}/artifacts?per_page=100"));
        var report = new JsonObject
        {
            ["schemaVersion"] = 2, ["version"] = version, ["stage"] = "develop-release-candidate",
            ["sha"] = identity.Sha, ["testsRunId"] = run, ["testsJobsPassed"] = RequiredJobs.Length,
            ["advisoryAndCompilerArtifact"] = "security-dependency-advisory-evidence",
            ["providerProof"] = "six-provider mandatory physical positive/negative and fault CI jobs",
            ["publicationAuthorized"] = false, ["mainPromotionPerformed"] = false,
            ["remainingPublicationGate"] = "actual promoted main push SHA + completed issues #575 and #634"
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, report.ToJsonString(new() { WriteIndented = true }));
        Console.WriteLine($"Develop {version} verified at {identity.Sha}; no publication authorized");
        return report;
    }

    public static void VerifyArtifacts(JsonNode response)
    {
        if (response["artifacts"] is not JsonArray artifacts || artifacts.Count(artifact =>
                Text(artifact, "name") == "security-dependency-advisory-evidence"
                && artifact?["expired"]?.GetValue<bool>() == false && artifact?["size_in_bytes"]?.GetValue<long>() > 0) != 1)
            throw new GovernanceException("Exact-head advisory/compiler evidence is missing, ambiguous or expired");
    }
}
