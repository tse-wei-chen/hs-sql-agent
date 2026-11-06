using System.Text.Json;
using System.Text.Json.Nodes;
using HsSqlAgent.TestGovernance;
using Xunit;

namespace Infrastructure.Test;

public sealed class SqlCoreProofToolTests
{
    private static readonly string[] Providers = SqlCorpusLedger.Providers;

    [Fact]
    public void CorpusLedger_ReportsSixProviderFamilyCountsWithoutFakeCoverage()
    {
        using var fixture = new CorpusFixture();
        var report = JsonSerializer.SerializeToElement(SqlCorpusLedger.Build(fixture.ManifestPath),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(12, report.GetProperty("totalCases").GetInt32());
        Assert.Equal(2, report.GetProperty("suites").GetArrayLength());
        Assert.Equal(6, report.GetProperty("providers").EnumerateObject().Count());
        Assert.Equal("observed-compiler-regression-corpus", report.GetProperty("evidenceLevel").GetString());
        Assert.Equal(JsonValueKind.Null, report.GetProperty("officialGrammarDenominator").ValueKind);
        Assert.False(report.GetProperty("liveExecutionProven").GetBoolean());
        foreach (var provider in Providers)
        {
            var item = report.GetProperty("providers").GetProperty(provider);
            Assert.Equal(2, item.GetProperty("totalCases").GetInt32());
            Assert.Equal(1, item.GetProperty("byFamily").GetProperty("DQL").GetInt32());
            Assert.Equal(1, item.GetProperty("byFamily").GetProperty("DML").GetInt32());
            Assert.Equal(1, item.GetProperty("versionProfiledCases").GetInt32());
        }
    }

    [Theory]
    [InlineData("drop-case", "expectedByProvider")]
    [InlineData("duplicate", "duplicate")]
    [InlineData("cross-target", "targetDialect")]
    [InlineData("missing-version", "targetVersion")]
    [InlineData("unknown-dialect", "dialect")]
    [InlineData("bad-manifest", "expectedByProvider")]
    public void CorpusLedger_RejectsDriftAndUnsafeCases(string change, string diagnostic)
    {
        using var fixture = new CorpusFixture();
        fixture.Break(change);
        var error = Assert.ThrowsAny<Exception>(() => SqlCorpusLedger.Build(fixture.ManifestPath));
        Assert.Contains(diagnostic, error.Message);
    }

    [Fact]
    public void CheckedInSqlCorpus_StillMatchesObservedSixProviderBaseline()
    {
        var path = Path.Combine("backend", "src", "UnitTest", "SqlAgent.Test",
            "SyntaxCorpus", "sqlcore-coverage-manifest.json");
        var report = JsonSerializer.SerializeToElement(SqlCorpusLedger.Build(path),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(4883, report.GetProperty("totalCases").GetInt32());
        Assert.Equal(6, report.GetProperty("suites").GetArrayLength());
        var expected = new Dictionary<string, int>
        {
            ["Postgres"] = 543, ["MySQL"] = 987, ["MsSqlServer"] = 571,
            ["Sqlite"] = 864, ["Oracle"] = 979, ["Firebird"] = 939,
        };
        foreach (var (provider, count) in expected)
        {
            var item = report.GetProperty("providers").GetProperty(provider);
            Assert.Equal(count, item.GetProperty("totalCases").GetInt32());
            Assert.Equal(7, item.GetProperty("byFamily").GetProperty("DML").GetInt32());
        }
    }

    [Fact]
    public void StageProfile_AcceptsExactly48CompleteProviderWorkloads()
    {
        using var fixture = new ProfileFixture();
        Assert.Equal(48, SqlCoreStageProfile.Verify(fixture.Path));
    }

    [Theory]
    [InlineData("missing-case", "48")]
    [InlineData("duplicate-case", "duplicate")]
    [InlineData("missing-stage", "stage")]
    [InlineData("failed-stage", "failure")]
    [InlineData("negative-metric", "metric")]
    [InlineData("bad-sample", "sample")]
    [InlineData("duplicate-stage", "duplicate")]
    public void StageProfile_FailsClosedOnBrokenProof(string change, string diagnostic)
    {
        using var fixture = new ProfileFixture();
        fixture.Break(change);
        var error = Assert.ThrowsAny<Exception>(() => SqlCoreStageProfile.Verify(fixture.Path));
        Assert.Contains(diagnostic, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OpenApiProof_RejectsMissingAdminControllers()
    {
        using var temp = new TempJson();
        temp.Write(new { paths = new Dictionary<string, object>() });
        var error = Assert.Throws<GovernanceException>(() => AdminOpenApiProof.Verify(temp.Path));
        Assert.Contains("Expected all Admin controller actions", error.Message);
    }

    private sealed class CorpusFixture : IDisposable
    {
        private readonly TempJson _json = new();
        private readonly JsonObject _manifest;
        private readonly JsonArray _query;
        private readonly JsonArray _dml;
        public string ManifestPath => _json.Path;

        public CorpusFixture()
        {
            _query = new JsonArray();
            _dml = new JsonArray();
            foreach (var provider in Providers)
            {
                _query.Add(new JsonObject
                {
                    ["name"] = provider + "-query", ["dialect"] = provider,
                    ["targetDialect"] = provider, ["sourceVersion"] = "16.0",
                    ["targetVersion"] = "16.0", ["sql"] = "SELECT 1",
                });
                _dml.Add(new JsonObject
                {
                    ["name"] = provider + "-dml", ["dialect"] = provider, ["sql"] = "DELETE FROM t WHERE id = 1",
                });
            }
            var expected = new JsonObject();
            foreach (var provider in Providers) expected[provider] = 1;
            _manifest = new JsonObject
            {
                ["schemaVersion"] = 1,
                ["suites"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["id"] = "native-versioned-query", ["file"] = "query.json",
                        ["statementFamily"] = "DQL", ["requiresVersionProfile"] = true,
                        ["nativeOnly"] = true, ["expectedByProvider"] = expected.DeepClone(),
                    },
                    new JsonObject
                    {
                        ["id"] = "ordinary-dml", ["file"] = "dml.json",
                        ["statementFamily"] = "DML", ["requiresVersionProfile"] = false,
                        ["nativeOnly"] = true, ["expectedByProvider"] = expected.DeepClone(),
                    },
                },
            };
            Save();
        }

        public void Break(string change)
        {
            switch (change)
            {
                case "drop-case": _query.RemoveAt(0); break;
                case "duplicate": _query[1]!["name"] = _query[0]!["name"]!.GetValue<string>(); break;
                case "cross-target": _query[1]!["targetDialect"] = "Postgres"; break;
                case "missing-version": ((JsonObject)_query[1]!).Remove("targetVersion"); break;
                case "unknown-dialect": _dml[0]!["dialect"] = "UnknownDB"; break;
                case "bad-manifest": ((JsonObject)_manifest["suites"]![0]!["expectedByProvider"]!).Remove("Firebird"); break;
                default: throw new ArgumentOutOfRangeException(nameof(change));
            }
            Save();
        }
        private void Save()
        {
            _json.Write(_manifest);
            File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(_json.Path)!, "query.json"), _query.ToJsonString());
            File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(_json.Path)!, "dml.json"), _dml.ToJsonString());
        }
        public void Dispose() => _json.Dispose();
    }

    private sealed class ProfileFixture : IDisposable
    {
        private readonly TempJson _temp = new();
        private readonly JsonObject _report;
        public string Path => _temp.Path;

        public ProfileFixture()
        {
            var rows = new JsonArray();
            foreach (var provider in SqlCoreStageProfile.Providers)
            foreach (var name in SqlCoreStageProfile.Cases)
            {
                var stages = new JsonArray();
                foreach (var stage in SqlCoreStageProfile.RequiredStages)
                    stages.Add(new JsonObject
                    {
                        ["Name"] = stage, ["Samples"] = 2, ["MedianMicroseconds"] = 4.0,
                        ["MedianAllocatedBytes"] = 128.0, ["MedianWorkUnits"] = 5.0, ["AllSucceeded"] = true,
                    });
                rows.Add(new JsonObject
                {
                    ["Provider"] = provider, ["Case"] = name, ["OutputDigest"] = new string('A', 64),
                    ["Stages"] = stages,
                });
            }
            _report = new JsonObject { ["schemaVersion"] = 1, ["iterations"] = 2, ["warmup"] = 1, ["results"] = rows };
            Save();
        }

        public void Break(string change)
        {
            var rows = (JsonArray)_report["results"]!;
            var row = (JsonObject)rows[0]!;
            var stages = (JsonArray)row["Stages"]!;
            switch (change)
            {
                case "missing-case": rows.RemoveAt(0); break;
                case "duplicate-case": rows[1]!["Provider"] = row["Provider"]!.GetValue<string>(); rows[1]!["Case"] = row["Case"]!.GetValue<string>(); break;
                case "missing-stage": stages.RemoveAt(stages.Count - 1); break;
                case "failed-stage": stages[0]!["AllSucceeded"] = false; break;
                case "negative-metric": stages[0]!["MedianAllocatedBytes"] = -1; break;
                case "bad-sample": stages[0]!["Samples"] = 1; break;
                case "duplicate-stage": stages.Add(stages[0]!.DeepClone()); break;
                default: throw new ArgumentOutOfRangeException(nameof(change));
            }
            Save();
        }

        private void Save() => _temp.Write(_report);
        public void Dispose() => _temp.Dispose();
    }

    private sealed class TempJson : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hssqlagent-proof-" + Guid.NewGuid().ToString("N"), "fixture.json");
        public void Write(object value)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(value));
        }
        public void Dispose() => Directory.Delete(System.IO.Path.GetDirectoryName(Path)!, recursive: true);
    }
}
