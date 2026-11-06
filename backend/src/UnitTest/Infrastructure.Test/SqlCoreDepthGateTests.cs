using System.Text.Json;
using System.Text.Json.Nodes;
using HsSqlAgent.TestGovernance;
using Xunit;

namespace Infrastructure.Test;

public sealed class SqlCoreDepthGateTests
{
    [Theory]
    [InlineData("depth", 2)]
    [InlineData("small-stack", 2)]
    [InlineData("mixed-depth", 6)]
    [InlineData("query-phases", 9)]
    [InlineData("mixed-cte-derived", 6)]
    [InlineData("nested-set", 6)]
    [InlineData("set-small", 4)]
    [InlineData("cte-small", 4)]
    [InlineData("mixed-small", 4)]
    [InlineData("lateral", 4)]
    [InlineData("correlated", 12)]
    [InlineData("prefixed", 6)]
    [InlineData("double", 6)]
    [InlineData("triple", 6)]
    [InlineData("siblings", 6)]
    [InlineData("comb", 6)]
    [InlineData("facts", 6)]
    [InlineData("correlated-in", 6)]
    [InlineData("projection", 12)]
    [InlineData("paging", 36)]
    public void AllDepthProbePolicies_RequireRealSuccessfulProofOrExactNegativeGrammar(string policy, int samples)
    {
        using var fixture = new ProbeFixture(policy, samples);
        Assert.Equal(samples, SqlCoreDepthProbeGate.Verify(policy, fixture.Path));
    }

    [Fact]
    public void ExactlyTwentyPoliciesRemainCoveredByRegressionFixtures()
    {
        Assert.Equal(20, SqlCoreDepthProbeGate.Profiles.Length);
        Assert.Equal(SqlCoreDepthProbeGate.Profiles.Length,
            SqlCoreDepthProbeGate.Profiles.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData("correlated", "resourceRejections", "rejection")]
    [InlineData("mixed-depth", "worker-failed", "compile")]
    [InlineData("lateral", "missing-type", "proof")]
    [InlineData("double", "over-budget", "allocation")]
    [InlineData("facts", "inspection-work", "Inspection")]
    [InlineData("nested-set", "negative-metric", "negative")]
    [InlineData("projection", "missing-case", "projection")]
    [InlineData("paging", "grammar-offset", "diagnostic")]
    [InlineData("paging", "wrong-parameter-count", "compiler stage")]
    [InlineData("paging", "duplicate-sample", "duplicated")]
    [InlineData("paging", "wrong-parse-stage", "diagnostic")]
    public void MutatedUnsafeReports_CannotPassWithoutMatchingProof(string policy, string mutation, string message)
    {
        var n = policy switch
        {
            "mixed-depth" or "lateral" => policy == "lateral" ? 4 : 6,
            "correlated" or "double" or "facts" or "nested-set" => policy == "correlated" ? 12 : 6,
            "projection" => 12,
            "paging" => 36,
            _ => throw new ArgumentOutOfRangeException(nameof(policy)),
        };
        using var fixture = new ProbeFixture(policy, n);
        fixture.Break(mutation);
        var error = Assert.Throws<GovernanceException>(() => SqlCoreDepthProbeGate.Verify(policy, fixture.Path));
        Assert.Contains(message, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class ProbeFixture : IDisposable
    {
        private readonly JsonObject _root;
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hs-depth-" + Guid.NewGuid().ToString("N"), "probe.json");

        public ProbeFixture(string profile, int samples)
        {
            var observations = new JsonArray();
            if (profile == "paging")
            {
                foreach (var provider in SqlCoreStageProfile.Providers)
                foreach (var shape in new[] { "nested-paging-typed-schema", "invalid-nested-paging" })
                foreach (var depth in new[] { 128, 512, 2048 })
                    observations.Add(Sample(shape, depth, provider, invalid: shape.StartsWith("invalid-", StringComparison.Ordinal)));
            }
            else
            {
                for (var index = 0; index < samples; index++)
                {
                    var shape = profile == "projection"
                        ? index < 6 ? "nested-scalar-subquery-typed-schema" : "nested-case-scalar-typed-schema"
                        : profile + (index % 2 == 0 ? "-typed-schema" : "");
                    var depth = index == samples - 1 ? 2048 : 128;
                    observations.Add(Sample(shape, depth, "Postgres", profile == "facts"));
                }
            }
            _root = new JsonObject
            {
                ["samples"] = observations.Count,
                ["failures"] = 0,
                ["crashesOrTimeouts"] = 0,
                ["resourceRejections"] = 0,
                ["observations"] = observations,
            };
            Save();
        }

        public void Break(string mutation)
        {
            var observations = (JsonArray)_root["observations"]!;
            var first = (JsonObject)observations[0]!;
            var worker = (JsonObject)first["worker"]!;
            var phases = (JsonArray)worker["stageMetrics"]!;
            switch (mutation)
            {
                case "resourceRejections": _root["resourceRejections"] = 1; break;
                case "worker-failed": worker["success"] = false; break;
                case "missing-type":
                    for (var i = phases.Count - 1; i >= 0; i--)
                        if (phases[i]!["Stage"]!.GetValue<string>() == "SchemaType") phases.RemoveAt(i);
                    break;
                case "over-budget":
                    var last = (JsonObject)observations[^1]!;
                    ((JsonObject)last["worker"]!)["allocatedBytes"] = 220000001;
                    break;
                case "inspection-work":
                    var inspection = phases.First(p => p!["Stage"]!.GetValue<string>() == "Inspection")!;
                    inspection["WorkUnits"] = 0;
                    break;
                case "negative-metric": phases[0]!["AllocatedBytes"] = -1; break;
                case "missing-case": observations.RemoveAt(observations.Count - 1); break;
                case "grammar-offset":
                    var negative = (JsonObject)observations[3]!;
                    ((JsonObject)negative["worker"]!)["diagnosticOffset"] = 0;
                    break;
                case "wrong-parameter-count":
                    worker["parameterCount"] = 0;
                    break;
                case "duplicate-sample":
                    observations[1] = observations[0]!.DeepClone();
                    break;
                case "wrong-parse-stage":
                    var invalidItem = (JsonObject)observations[3]!;
                    ((JsonArray)((JsonObject)invalidItem["worker"]!)["stageMetrics"]!)[0]!["Stage"] = "Validation";
                    break;
                default: throw new ArgumentOutOfRangeException(nameof(mutation));
            }
            Save();
        }

        private static JsonObject Sample(string shape, int depth, string dialect, bool inspection = false, bool invalid = false)
        {
            var phases = new JsonArray();
            if (invalid)
                phases.Add(Phase("Parsing", depth, success: false));
            else
            {
                foreach (var name in new[] { "Parsing", "Binding", "Normalization", "Validation", "Policy", "Rendering" })
                    phases.Add(Phase(name, depth));
                if (shape.EndsWith("-typed-schema", StringComparison.Ordinal))
                {
                    phases.Add(Phase("SchemaBinding", depth));
                    phases.Add(Phase("SchemaType", depth));
                }
                if (inspection) phases.Add(Phase("Inspection", depth));
            }
            var worker = new JsonObject
            {
                ["success"] = !invalid,
                ["allocatedBytes"] = 1024,
                ["stageMetrics"] = phases,
                ["expectedGrammarRejection"] = invalid,
                ["diagnosticCode"] = invalid ? "SQL_PARSE_GRAMMAR" : null,
                ["diagnosticOffset"] = invalid ? 18 * depth + 32 : 0,
                ["parameterCount"] = 2 * depth,
            };
            return new JsonObject
            {
                ["shape"] = shape, ["dialect"] = dialect, ["depth"] = depth,
                ["processExitCode"] = 0, ["stackBytes"] = 262144,
                ["worker"] = worker,
            };
        }

        private static JsonObject Phase(string stage, int depth, bool success = true) => new()
        {
            ["Stage"] = stage, ["Success"] = success,
            ["ElapsedMicroseconds"] = 10, ["AllocatedBytes"] = 1024, ["WorkUnits"] = depth + 1,
        };
        private void Save()
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, _root.ToJsonString());
        }
        public void Dispose() => Directory.Delete(System.IO.Path.GetDirectoryName(Path)!, recursive: true);
    }
}
