using HsSqlAgent.TestGovernance;
using Xunit;

namespace Infrastructure.Test;

public sealed class TestGovernanceContractsTests
{
    [Fact]
    public void VerifiedXunitAndCoberturaEvidence_ReportsActualDenominator()
    {
        using var fixture = new TemporaryRepository();
        fixture.WriteEvidence();
        var report = Governance.Verify(fixture.Root, "Common.Test");
        Assert.Equal(2, report.Total);
        Assert.Equal(2, report.Passed);
        Assert.Equal(50.0, report.LineCoveragePercent);
        Assert.Equal(6, report.LinesValid);
        Assert.Contains("not whole-repository", report.Scope);
    }

    [Fact]
    public void MissingOrAmbiguousTrx_FailsClosed()
    {
        using var fixture = new TemporaryRepository();
        Assert.Throws<GovernanceException>(() => Governance.Verify(fixture.Root, "Common.Test"));
        fixture.WriteEvidence();
        File.WriteAllText(Path.Combine(fixture.Root, "extra.trx"),
            File.ReadAllText(Path.Combine(fixture.Root, "run.trx")));
        var error = Assert.Throws<GovernanceException>(() => Governance.Verify(fixture.Root, "Common.Test"));
        Assert.Contains("exactly one *.trx", error.Message);
    }

    [Fact]
    public void MissingCoverage_FailsClosedRatherThanReportingZero()
    {
        using var fixture = new TemporaryRepository();
        fixture.WriteEvidence();
        File.Delete(fixture.CoveragePath);
        var error = Assert.Throws<GovernanceException>(() => Governance.Verify(fixture.Root, "Common.Test"));
        Assert.Contains("coverage.cobertura", error.Message);
    }

    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(3, 0, 0, 0)]
    [InlineData(1, 1, 0, 1)]
    public void EmptySkippedOrFailedTestRun_FailsClosed(int total, int executed, int passed, int failed)
    {
        using var fixture = new TemporaryRepository();
        fixture.WriteEvidence(total, executed, passed, failed);
        var error = Assert.Throws<GovernanceException>(() => Governance.Verify(fixture.Root, "Common.Test"));
        Assert.Contains("empty or failed", error.Message);
    }

    [Fact]
    public void ZeroInstrumentedProductionLines_IsNotValidCoverage()
    {
        using var fixture = new TemporaryRepository();
        fixture.WriteEvidence(covered: 0, valid: 0);
        var error = Assert.Throws<GovernanceException>(() => Governance.Verify(fixture.Root, "Common.Test"));
        Assert.Contains("no measurable production lines", error.Message);
    }

    [Fact]
    public void TimestampedMtpCobertura_IsAcceptedWithNonzeroDenominator()
    {
        using var fixture = new TemporaryRepository();
        fixture.WriteEvidence();
        var renamed = Path.Combine(Path.GetDirectoryName(fixture.CoveragePath)!, "coverage.cobertura.061126123456789.xml");
        File.Move(fixture.CoveragePath, renamed);
        Assert.Equal(50.0, Governance.Verify(fixture.Root, "Common.Test").LineCoveragePercent);
    }

    [Fact]
    public void SkippedTests_AreExplicitlyCounted()
    {
        using var fixture = new TemporaryRepository();
        fixture.WriteEvidence(total: 3, executed: 2, passed: 2, skipped: 1);
        var report = Governance.Verify(fixture.Root, "Common.Test");
        Assert.Equal(1, report.NotExecuted);
    }

    [Fact]
    public void OwnershipGate_RejectsProjectsWithoutCiInvocation()
    {
        using var fixture = new TemporaryRepository();
        fixture.CreateProjectsAndWorkflow(skipCommon: true);
        var error = Assert.Throws<GovernanceException>(() => Governance.Audit(fixture.Root));
        Assert.Contains("No CI test invocation for: backend/src/UnitTest/Common.Test/Common.Test.csproj", error.Message);
    }

    [Fact]
    public void OwnershipGate_RejectsNoOpAssertTrueTrue()
    {
        using var fixture = new TemporaryRepository();
        fixture.CreateProjectsAndWorkflow();
        var source = Path.Combine(fixture.Root, "backend", "src", "UnitTest", "Common.Test", "Smoke.cs");
        File.WriteAllText(source, "[Fact] public void Placeholder() { " + "Assert." + "True( true ); }");
        var error = Assert.Throws<GovernanceException>(() => Governance.Audit(fixture.Root));
        Assert.Contains("No-op test assertion", error.Message);
    }

    [Fact]
    public void OwnershipGate_AcceptsCompleteCliAndMtpContracts()
    {
        using var fixture = new TemporaryRepository();
        fixture.CreateProjectsAndWorkflow();
        var report = Governance.Audit(fixture.Root);
        Assert.Equal(4, report.ProjectsWithCiOwner.Length);
        Assert.Equal(4, report.MeasuredJobs.Length);
    }

    [Fact]
    public void LayoutGate_RejectsOnlyLegacyTopLevelServicesSources()
    {
        using var fixture = new TemporaryRepository();
        var bad = fixture.Write("backend/src/UnitTest/SqlAgent.Test/Services/Flat.cs", "// prohibited");
        fixture.Write("backend/src/UnitTest/HsSqlAgent.Server.Test/Services/Dml/Nested.cs", "// allowed");
        fixture.Write("backend/src/UnitTest/Auth.Test/Services/Foo.cs", "// owned elsewhere");
        var violation = Assert.Single(Governance.FlatServiceTests(fixture.Root));
        Assert.Equal(Path.GetRelativePath(fixture.Root, bad).Replace('\\', '/'), violation);
    }

    private sealed class TemporaryRepository : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "hssqlagent-test-governance-" + Guid.NewGuid().ToString("N"));
        public string CoveragePath => Path.Combine(Root, "coverage", "coverage.cobertura.xml");

        public TemporaryRepository() => Directory.CreateDirectory(Root);

        public string Write(string path, string content)
        {
            var full = Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
            return full;
        }

        public void WriteEvidence(int total = 2, int executed = 2, int passed = 2,
                                  int failed = 0, int skipped = 0, int covered = 3, int valid = 6)
        {
            Write("run.trx",
                "<TestRun xmlns=\"http://microsoft.com/schemas/VisualStudio/TeamTest/2010\">" +
                "<ResultSummary outcome=\"Completed\"><Counters " +
                $"total=\"{total}\" executed=\"{executed}\" passed=\"{passed}\" failed=\"{failed}\" notExecuted=\"{skipped}\"" +
                " /></ResultSummary></TestRun>");
            Write("coverage/coverage.cobertura.xml",
                $"<coverage lines-covered=\"{covered}\" lines-valid=\"{valid}\" line-rate=\"0.5\" />");
        }

        public void CreateProjectsAndWorkflow(bool skipCommon = false)
        {
            string[] jobs = ["common-test", "infrastructure-test", "auth-test", "admin-test"];
            string[] projects = ["Common.Test", "Infrastructure.Test", "Auth.Test", "Admin.Test"];

            var workflow = "jobs:\n";
            for (var i = 0; i < jobs.Length; i++)
            {
                var path = $"backend/src/UnitTest/{projects[i]}/{projects[i]}.csproj";
                Write(path, "<Project />");
                workflow += $"  {jobs[i]}:\n    steps:\n";
                if (!skipCommon || i > 0)
                    workflow += $"      - run: dotnet test --project {path} --report-xunit-trx --coverlet --results-directory artifacts/test-evidence/{jobs[i]}\n";
                workflow += $"      - run: dotnet run --project backend/tools/HsSqlAgent.TestGovernance -- verify --directory artifacts/test-evidence/{jobs[i]} --suite {projects[i]}\n";
            }
            Write(".github/workflows/test.yml", workflow);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
