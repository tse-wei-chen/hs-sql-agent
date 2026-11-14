namespace HsSqlAgent.TestGovernance;

public static class CiCommands
{
    public static int Execute(string[] args)
    {
        string Argument(int index) => args.Length > index ? args[index] : throw new GovernanceException($"Missing argument for {args[0]}");
        string Option(string name, string fallback)
        {
            var index = Array.IndexOf(args, name);
            return index < 0 ? fallback : Argument(index + 1);
        }
        var root = Path.GetFullPath(Option("--root", "."));
        var github = new GitHubEvidence();
        switch (args[0])
        {
            case "syntax-parity": return SqlSyntaxParity.Execute(args[1..]);
            case "release-property": Console.WriteLine(ReleaseMetadata.Read(root, Argument(1))); break;
            case "release-security":
                ReleaseEvidence.SecurityGate(root, Argument(1), ReleaseIdentity.FromEnvironment(), github); break;
            case "release-head": ReleaseEvidence.MainHeadGate(ReleaseIdentity.FromEnvironment(), github); break;
            case "develop-candidate":
                ReleaseEvidence.DevelopCandidate(root,
                    Option("--output", Environment.GetEnvironmentVariable("HS_SQL_AGENT_CANDIDATE_REPORT") ?? "artifacts/release-candidate/develop.json"),
                    Environment.GetEnvironmentVariable("GITHUB_REF") ?? "", ReleaseIdentity.FromEnvironment(), github); break;
            case "dependency-audit":
                if (args.Contains("--verify"))
                {
                    var index = Array.IndexOf(args, "--verify");
                    DependencySecurity.Verify(File.ReadAllText(Argument(index + 1)), File.ReadAllText(Argument(index + 2)));
                }
                else DependencySecurity.Audit(root, Option("--output", Environment.GetEnvironmentVariable("HS_SQL_AGENT_SECURITY_AUDIT_DIR") ?? Path.Combine(root, "artifacts", "security")));
                break;
            case "server-integration":
                var manifestPath = Option("--manifest", Environment.GetEnvironmentVariable("HS_SQL_AGENT_SERVER_SUITE_MANIFEST") ?? "");
                var manifest = manifestPath.Length == 0 ? GovernanceResources.Read("server-integration.tsv") : File.ReadAllText(manifestPath);
                if (args.Contains("--default-filter")) Console.WriteLine(ServerIntegrationOwnership.DefaultFilter(manifest));
                else ServerIntegrationOwnership.Audit(manifest,
                    File.ReadAllText(Option("--workflow", Environment.GetEnvironmentVariable("HS_SQL_AGENT_SERVER_SUITE_WORKFLOW") ?? Path.Combine(root, ".github", "workflows", "test.yml"))),
                    Option("--test-root", Environment.GetEnvironmentVariable("HS_SQL_AGENT_SERVER_SUITE_TEST_ROOT") ?? Path.Combine(root, "backend", "src", "UnitTest", "HsSqlAgent.Server.Test")));
                break;
            case "physical-tests": PhysicalTestCases.Run(int.Parse(Argument(1)), Argument(2), Argument(3)); break;
            case "migration-baseline": PackageCompatibility.VerifyMigrations(root); break;
            case "package-api": PackageCompatibility.VerifyApi(root, Path.GetFullPath(Argument(1)), Argument(2)); break;
            case "package-smoke": PackageConsumerSmoke.Run(root, Argument(1)); break;
            default: throw new GovernanceException($"Unknown governance command: {args[0]}");
        }
        return 0;
    }
}
