using System.Text.Json.Nodes;
using HsSqlAgent.TestGovernance;
using Xunit;

namespace Infrastructure.Test;

public sealed class SqlCorePairedReportGateTests
{
    [Theory]
    [InlineData("script", 12)]
    [InlineData("catalog", 12)]
    [InlineData("catalog-lookup", 2)]
    [InlineData("binding", 12)]
    [InlineData("view-cascade", 2)]
    [InlineData("normalization", 12)]
    [InlineData("validation", 12)]
    public void AllSevenBenchmarkReports_RequirePairedDigestParityAndCompleteRows(string profile, int outputRows)
    {
        using var f = new PairedFixture(profile);
        Assert.Equal(outputRows, SqlCorePairedReportGate.Verify(profile, f.Directory));
        Assert.True(File.Exists(System.IO.Path.Combine(f.Directory, f.Artifact)));
    }

    [Fact]
    public void ExactlySevenPairedReportsAreCoveredByXunit()
    {
        Assert.Equal(7, SqlCorePairedReportGate.Profiles.Length);
        Assert.Equal(7, SqlCorePairedReportGate.Profiles.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData("script", "missing-case", "expected")]
    [InlineData("catalog", "duplicate-case", "duplicate")]
    [InlineData("binding", "digest-drift", "parity")]
    [InlineData("view-cascade", "missing-case", "expected")]
    [InlineData("catalog-lookup", "wrong-case", "lookup")]
    [InlineData("normalization", "missing-stage", "stage")]
    [InlineData("validation", "metric-invalid", "invalid")]
    [InlineData("script", "zero-baseline", "invalid")]
    public void TamperedPairedSamples_FailClosedWithoutProducingFalseSpeedups(
        string profile, string mutation, string message)
    {
        using var f = new PairedFixture(profile);
        f.Break(mutation);
        var error = Assert.Throws<GovernanceException>(() => SqlCorePairedReportGate.Verify(profile, f.Directory));
        Assert.Contains(message, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingPairedRunIsNeverSilentlyExcluded()
    {
        using var f = new PairedFixture("catalog");
        File.Delete(System.IO.Path.Combine(f.Directory, "catalog-head-3.json"));
        Assert.Throws<FileNotFoundException>(() => SqlCorePairedReportGate.Verify("catalog", f.Directory));
    }

    private sealed class PairedFixture : IDisposable
    {
        private readonly string _prefix;
        private readonly string _profile;
        public string Directory { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hs-paired-" + Guid.NewGuid().ToString("N"));
        public string Artifact => _profile switch
        {
            "script" => "script-comparison.md",
            "catalog" => "catalog-comparison.md",
            "catalog-lookup" => "catalog-lookup-comparison.md",
            "binding" => "catalog-binding-comparison.md",
            "view-cascade" => "view-cascade-comparison.md",
            "normalization" => "regex-normalization-comparison.md",
            "validation" => "validation-fastpath-comparison.md",
            _ => throw new ArgumentOutOfRangeException(),
        };

        public PairedFixture(string profile)
        {
            _profile = profile;
            _prefix = profile switch
            {
                "normalization" => "regex-stage",
                "validation" => "validation-stage",
                _ => profile,
            };
            System.IO.Directory.CreateDirectory(Directory);
            var rows = new JsonArray();
            if (profile == "catalog-lookup")
            {
                rows.Add(Row("", "lookup-96"));
                rows.Add(Row("", "lookup-256"));
            }
            else if (profile == "view-cascade")
            {
                rows.Add(Row("Postgres", "cascade-24"));
                rows.Add(Row("Postgres", "cascade-48"));
            }
            else
            {
                foreach (var provider in SqlCoreStageProfile.Providers)
                foreach (var name in profile is "normalization" or "validation"
                    ? SqlCoreStageProfile.Cases
                    : new[] { "workload-a", "workload-b" })
                    rows.Add(Row(provider, name));
            }
            var report = new JsonObject { ["results"] = rows };
            for (var i = 1; i <= 3; i++)
                foreach (var revision in new[] { "base", "head" })
                    File.WriteAllText(ReportPath(revision, i), report.ToJsonString());
        }

        public void Break(string mutation)
        {
            var head = JsonNode.Parse(File.ReadAllText(ReportPath("head", 1)))!.AsObject();
            var rows = head["results"]!.AsArray();
            var first = rows[0]!.AsObject();
            switch (mutation)
            {
                case "missing-case": rows.RemoveAt(0); break;
                case "duplicate-case": rows[1]!["Case"] = first["Case"]!.GetValue<string>(); break;
                case "digest-drift": first["OutputDigest"] = new string('B', 64); break;
                case "wrong-case": first["Case"] = "lookup-100"; break;
                case "missing-stage":
                    ((JsonArray)first["Stages"]!).RemoveAt(0); break;
                case "metric-invalid":
                    ((JsonObject)((JsonArray)first["Stages"]!)[0]!)["MedianAllocatedBytes"] = -1; break;
                case "zero-baseline":
                {
                    var b = JsonNode.Parse(File.ReadAllText(ReportPath("base", 1)))!.AsObject();
                    ((JsonObject)((JsonArray)b["results"]!)[0]!)["MeanAllocatedBytes"] = 0;
                    for (var i = 1; i <= 3; i++)
                        File.WriteAllText(ReportPath("base", i), b.ToJsonString());
                    return;
                }
                default: throw new ArgumentOutOfRangeException(nameof(mutation));
            }
            for (var i = 1; i <= 3; i++)
                File.WriteAllText(ReportPath("head", i), head.ToJsonString());
        }

        private JsonObject Row(string provider, string name)
        {
            var row = new JsonObject
            {
                ["Case"] = name, ["OutputDigest"] = new string('A', 64),
                ["MeanAllocatedBytes"] = 10000, ["MeanMicroseconds"] = 100,
            };
            if (_profile != "catalog-lookup") row["Provider"] = provider;
            if (_profile is "normalization" or "validation")
            {
                var stages = new JsonArray();
                foreach (var stage in SqlCoreStageProfile.RequiredStages)
                    stages.Add(new JsonObject
                    {
                        ["Name"] = stage, ["MedianAllocatedBytes"] = 1000,
                        ["MedianMicroseconds"] = 100,
                    });
                row["Stages"] = stages;
            }
            return row;
        }

        private string ReportPath(string side, int round) =>
            System.IO.Path.Combine(Directory, $"{_prefix}-{side}-{round}.json");

        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}
