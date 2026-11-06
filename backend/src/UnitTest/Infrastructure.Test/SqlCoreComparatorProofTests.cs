using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using HsSqlAgent.TestGovernance;
using Xunit;

namespace Infrastructure.Test;

public sealed class SqlCoreComparatorProofTests
{
    [Fact]
    public void AllThreeIdenticalRounds_PreserveSixProviderCorpus()
    {
        using var f = new PerfFixture();
        var report = JsonSerializer.SerializeToElement(SqlCorePerfComparison.Compare(f.Directory));
        Assert.Equal(48, report.GetProperty("results").GetArrayLength());
        Assert.Empty(report.GetProperty("failures").EnumerateArray());
        Assert.True(File.Exists(Path.Combine(f.Directory, "comparison.json")));
    }

    [Theory]
    [InlineData("remove-case", "all 6 providers")]
    [InlineData("substitute-case", "all 6 providers")]
    [InlineData("different-digest", "emitted SQL")]
    [InlineData("regress-mean", "exceeds")]
    [InlineData("regress-p95", "exceeds")]
    [InlineData("regress-alloc", "exceeds")]
    [InlineData("runtime-drift", "Runtime/settings")]
    [InlineData("zero-metric", "Invalid")]
    [InlineData("negative-metric", "Invalid")]
    public void RegressionProof_DoesNotDropOrReplaceEvidence(string change, string diagnostic)
    {
        using var f = new PerfFixture(change);
        var error = Assert.Throws<GovernanceException>(() => SqlCorePerfComparison.Compare(f.Directory));
        Assert.Contains(diagnostic, error.Message);
    }

    [Fact]
    public void SourceDelta_PartitionsCommittedChangesAndReportsEachBucket()
    {
        using var f = new GitFixture();
        f.Write("backend/src/Modules/HsSqlAgent.SqlCore/RewriteParser.fs", "old\nkeep\n");
        var before = f.Commit();
        f.Write("backend/src/Modules/HsSqlAgent.SqlCore/RewriteParser.fs", "new\nkeep\nextra\n");
        f.Write("backend/src/UnitTest/SqlAgent.Test/Smoke.cs", "test1\ntest2\n");
        f.Write(".github/scripts/test_code_delta_contract.py", "assert True\n");
        f.Write("backend/src/Modules/HsSqlAgent.SqlCore/README.md", "documentation\n");
        f.Write("backend/src/Modules/SqlAgent.Service/Migrations/Initial.cs", "up\ndown\n");
        f.Write(".github/workflows/test.yml", "workflow\n");
        var after = f.Commit();
        var result = SqlCoreCodeDelta.Summarize(f.Directory, before, after);

        Assert.Equal(1, result.SchemaVersion);
        Assert.Equal(before, result.BaseCommit);
        Assert.Equal(after, result.HeadCommit);
        Assert.Equal((2, 1, 1), (result.Categories["production"].Added, result.Categories["production"].Deleted, result.Categories["production"].Files));
        Assert.Equal((3, 0, 2), (result.Categories["tests"].Added, result.Categories["tests"].Deleted, result.Categories["tests"].Files));
        Assert.Equal(1, result.Categories["docs"].Added);
        Assert.Equal(2, result.Categories["generatedMigrations"].Added);
        Assert.Equal(1, result.Categories["other"].Added);
        Assert.Equal(0, result.BinaryFiles);
        Assert.Contains("## SqlCore PR code delta", SqlCoreCodeDelta.Markdown(result));
    }

    [Fact]
    public void GitRenameAcrossTestAndProduction_RemainsVisibleAsDeleteAndAdd()
    {
        using var f = new GitFixture();
        f.Write("backend/src/UnitTest/SqlAgent.Test/Moved.cs", "one\ntwo\n");
        var before = f.Commit();
        Directory.CreateDirectory(Path.Combine(f.Directory, "backend/src/Modules/HsSqlAgent.SqlCore"));
        f.Git("mv", "backend/src/UnitTest/SqlAgent.Test/Moved.cs",
            "backend/src/Modules/HsSqlAgent.SqlCore/Moved.cs");
        var after = f.Commit();
        var result = SqlCoreCodeDelta.Summarize(f.Directory, before, after);
        Assert.Equal(2, result.Categories["production"].Added);
        Assert.Equal(2, result.Categories["tests"].Deleted);
    }

    [Fact]
    public void GitDelta_IgnoresUncommittedWorkingTreeChanges()
    {
        using var f = new GitFixture();
        f.Write("backend/src/Modules/HsSqlAgent.SqlCore/RewriteParser.fs", "before\n");
        var before = f.Commit();
        f.Write("backend/src/Modules/HsSqlAgent.SqlCore/RewriteParser.fs", "after\n");
        var after = f.Commit();
        f.Write("backend/src/Modules/HsSqlAgent.SqlCore/RewriteParser.fs", "uncommitted\n");
        Assert.Equal(1, SqlCoreCodeDelta.Summarize(f.Directory, before, after).Categories["production"].Added);
    }

    [Fact]
    public void GitDelta_ReportsBinaryChangeWithoutInventingLineCount()
    {
        using var f = new GitFixture();
        var before = f.Commit();
        f.WriteBinary("backend/src/Modules/HsSqlAgent.SqlCore/Binary.fs", [0, 1, 2]);
        var after = f.Commit();
        var report = SqlCoreCodeDelta.Summarize(f.Directory, before, after);
        Assert.Equal(1, report.BinaryFiles);
        Assert.Equal(1, report.Categories["production"].Files);
        Assert.Equal(0, report.Categories["production"].Added);
    }

    [Fact]
    public void GitDelta_FailsClosedOnUnknownRevision()
    {
        using var f = new GitFixture();
        var before = f.Commit();
        Assert.Throws<GovernanceException>(() => SqlCoreCodeDelta.Summarize(f.Directory, before, "nonexistent-revision"));
    }

    private sealed class PerfFixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "hssqlagent-perf-" + Guid.NewGuid().ToString("N"));
        public PerfFixture(string? change = null)
        {
            System.IO.Directory.CreateDirectory(Directory);
            var rows = new JsonArray();
            foreach (var provider in SqlCoreStageProfile.Providers)
            foreach (var name in SqlCoreStageProfile.Cases)
                rows.Add(new JsonObject
                {
                    ["Provider"] = provider, ["Case"] = name, ["Iterations"] = 300,
                    ["MeanMicroseconds"] = 100.0, ["P95Microseconds"] = 200.0,
                    ["MeanAllocatedBytes"] = 10000.0, ["OutputDigest"] = new string('A', 64),
                });
            var baseline = new JsonObject
            {
                ["schemaVersion"] = 2, ["iterations"] = 300, ["warmup"] = 100,
                ["framework"] = "test-runtime", ["os"] = "test-os",
                ["processArchitecture"] = "X64", ["results"] = rows,
            };
            var candidate = (JsonObject)baseline.DeepClone();
            var candidateRows = (JsonArray)candidate["results"]!;
            var first = (JsonObject)candidateRows[0]!;
            switch (change)
            {
                case "remove-case": candidateRows.RemoveAt(0); break;
                case "substitute-case": first["Case"] = "unsupported-substitute"; break;
                case "different-digest": first["OutputDigest"] = new string('B', 64); break;
                case "regress-mean": first["MeanMicroseconds"] = 1000000.0; break;
                case "regress-p95": first["P95Microseconds"] = 1000000.0; break;
                case "regress-alloc": first["MeanAllocatedBytes"] = 1000000.0; break;
                case "runtime-drift": candidate["framework"] = "other-runtime"; break;
                case "zero-metric": first["MeanMicroseconds"] = 0.0; break;
                case "negative-metric": first["MeanMicroseconds"] = -1.0; break;
                case null: break;
                default: throw new ArgumentOutOfRangeException(nameof(change));
            }
            for (var round = 1; round <= 3; round++)
            {
                File.WriteAllText(Path.Combine(Directory, $"base-{round}.json"), baseline.ToJsonString());
                File.WriteAllText(Path.Combine(Directory, $"head-{round}.json"), candidate.ToJsonString());
            }
        }
        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }

    private sealed class GitFixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "hssqlagent-git-" + Guid.NewGuid().ToString("N"));
        public GitFixture()
        {
            System.IO.Directory.CreateDirectory(Directory);
            Git("init", "-q");
            Git("config", "user.name", "CI Fixture");
            Git("config", "user.email", "ci-fixture@example.invalid");
        }
        public void Write(string path, string data) => WriteBinary(path, System.Text.Encoding.UTF8.GetBytes(data));
        public void WriteBinary(string path, byte[] data)
        {
            var full = Path.Combine(Directory, path);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, data);
        }
        public string Commit()
        {
            Git("add", "-A");
            Git("commit", "--allow-empty", "-qm", "fixture");
            return Git("rev-parse", "HEAD").Trim();
        }
        public string Git(params string[] args)
        {
            var psi = new ProcessStartInfo("git") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
            psi.ArgumentList.Add("-C");
            psi.ArgumentList.Add(Directory);
            foreach (var arg in args) psi.ArgumentList.Add(arg);
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            var error = p.StandardError.ReadToEnd();
            p.WaitForExit();
            Assert.True(p.ExitCode == 0, error);
            return output;
        }
        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}
