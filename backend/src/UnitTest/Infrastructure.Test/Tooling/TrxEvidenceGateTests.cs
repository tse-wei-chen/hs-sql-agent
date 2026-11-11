using HsSqlAgent.TestGovernance;
using Xunit;

namespace Infrastructure.Test.Tooling;

public sealed class TrxEvidenceGateTests
{
    [Theory]
    [InlineData(3, 2, 2, 0, 1)] // A skipped case cannot count as an all-green mandatory suite.
    [InlineData(3, 2, 2, 0, 0)] // A missing result cannot be silently omitted from counters.
    [InlineData(2, 2, 1, 0, 0)] // An executed but unaccounted case cannot count as passed.
    [InlineData(2, 1, 1, 0, 2)] // The skip count cannot exceed the unexecuted remainder.
    public void RequiredSuiteRejectsSkippedOrInconsistentTrxCounters(
        int total, int executed, int passed, int failed, int notExecuted)
    {
        var folder = WriteEvidence(total, executed, passed, failed, notExecuted);
        try
        {
            Assert.Throws<GovernanceException>(() => Governance.Verify(folder, "RequiredOfflineSuite"));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void RequiredSuiteAcceptsFullyExecutedPassingTrx()
    {
        var folder = WriteEvidence(total: 2, executed: 2, passed: 2, failed: 0, notExecuted: 0);
        try
        {
            var report = Governance.Verify(folder, "RequiredOfflineSuite");

            Assert.Equal(2, report.Total);
            Assert.Equal(2, report.Passed);
            Assert.Equal(0, report.NotExecuted);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static string WriteEvidence(int total, int executed, int passed, int failed, int notExecuted)
    {
        var folder = Path.Combine(Path.GetTempPath(), "hsa-trx-evidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "run.trx"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <TestRun>
              <ResultSummary>
                <Counters total="{total}" executed="{executed}" passed="{passed}" failed="{failed}" notExecuted="{notExecuted}" />
              </ResultSummary>
            </TestRun>
            """);
        File.WriteAllText(Path.Combine(folder, "coverage.cobertura.xml"), """
            <?xml version="1.0" encoding="utf-8"?>
            <coverage lines-valid="2" lines-covered="1" />
            """);
        return folder;
    }
}
