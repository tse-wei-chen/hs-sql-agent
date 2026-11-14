using System.Globalization;
using System.Text;
using System.Text.Json;

namespace HsSqlAgent.TestGovernance;

/// <summary>
/// Replace seven inline Python paired-benchmark reports with a single strict
/// same-runner, three-round C# validator. Reject missing workloads or digest
/// drift; never print a ratio when its denominator is invalid.
/// </summary>
public static class SqlCorePairedReportGate
{
    private sealed record Policy(
        string Prefix, string Artifact, string Title, string Columns, int Rows,
        bool Lookup = false, bool View = false, string? Stage = null,
        bool RequirePositiveCandidate = false);

    private static readonly Dictionary<string, Policy> Policies = new(StringComparer.Ordinal)
    {
        ["script"] = new("script", "script-comparison.md",
            "### Script analysis reuse (median of 3 same-runner rounds)",
            "| Dialect / workload | Candidate/base allocation | Candidate/base mean time |", 12,
            RequirePositiveCandidate: true),
        ["catalog"] = new("catalog", "catalog-comparison.md",
            "### Virtual Catalog stress comparison (median of three paired runs)",
            "| Dialect / workload | Candidate/base allocated | Candidate/base mean time |", 12,
            RequirePositiveCandidate: true),
        ["catalog-lookup"] = new("catalog-lookup", "catalog-lookup-comparison.md",
            "### Catalog lookup microbenchmark (512 exact probes, median of 3 paired runs)",
            "| Catalog | Candidate/base allocation | Candidate/base mean time |", 2, Lookup: true),
        ["binding"] = new("binding", "catalog-binding-comparison.md",
            "### Name/Type Binding shared Virtual Catalog projection (3 paired rounds)",
            "| Dialect / workload | Candidate/base allocation | Candidate/base mean latency |", 12),
        ["view-cascade"] = new("view-cascade", "view-cascade-comparison.md",
            "### Proven reversed View chain DROP TABLE CASCADE (3 paired rounds)",
            "| Chain depth | Candidate/base allocations | Candidate/base mean latency |", 2,
            View: true, RequirePositiveCandidate: true),
        ["normalization"] = new("regex-stage", "regex-normalization-comparison.md",
            "### Proven source Regex stage fast-path (three paired rounds)",
            "| Provider / deep AST | Normalization allocation candidate/base | Normalization latency candidate/base |", 48,
            Stage: "Normalization"),
        ["validation"] = new("validation-stage", "validation-fastpath-comparison.md",
            "### Fully proven Filter + Join validation AST fast-path (three paired rounds)",
            "| Dialect / deep AST | Validation allocations candidate/base | Validation time candidate/base |", 48,
            Stage: "Validation"),
    };

    public static string[] Profiles => Policies.Keys.ToArray();

    public static int Verify(string profile, string directory)
    {
        if (!Policies.TryGetValue(profile, out var policy))
            throw new GovernanceException($"Unknown paired-report policy: {profile}");

        var runs = new Dictionary<string, List<Dictionary<(string Provider, string Case), JsonElement>>>();
        foreach (var revision in new[] { "base", "head" })
        {
            var series = new List<Dictionary<(string Provider, string Case), JsonElement>>();
            for (var round = 1; round <= 3; round++)
            {
                var path = Path.Combine(directory, $"{policy.Prefix}-{revision}-{round}.json");
                using var file = JsonDocument.Parse(File.ReadAllText(path));
                var root = file.RootElement;
                Require(root.TryGetProperty("results", out var items) && items.ValueKind == JsonValueKind.Array,
                    $"{profile}: {revision}-{round} missing results array");
                var rows = new Dictionary<(string Provider, string Case), JsonElement>();
                foreach (var item in items.EnumerateArray())
                {
                    Require(item.ValueKind == JsonValueKind.Object, $"{profile}: invalid workload row");
                    var provider = policy.Lookup ? "" : Text(item, "Provider");
                    var name = Text(item, "Case");
                    Require(provider is not null && name is not null, $"{profile}: missing workload identity");
                    Require(rows.TryAdd((provider!, name!), item.Clone()),
                        $"{profile}: duplicate workload key in {revision} round {round}");
                }
                Require(rows.Count == policy.Rows, $"{profile}: expected {policy.Rows} workloads for {revision}-{round}, found {rows.Count}");
                ValidateExpectedKeys(profile, policy, rows.Keys.ToHashSet());
                series.Add(rows);
            }
            runs[revision] = series;
        }

        var reference = runs["base"][0].Keys.ToHashSet();
        foreach (var series in runs.Values)
            foreach (var round in series)
                Require(round.Keys.ToHashSet().SetEquals(reference),
                    $"{profile}: missing/replaced workload between base/head rounds");

        var lines = new List<string> { policy.Title, policy.Columns, "|---|---:|---:|" };
        var outputRows = 0;
        foreach (var key in reference.OrderBy(k => k.Provider, StringComparer.Ordinal).ThenBy(k => k.Case, StringComparer.Ordinal))
        {
            var before = runs["base"].Select(x => x[key]).ToArray();
            var after = runs["head"].Select(x => x[key]).ToArray();
            var digests = before.Concat(after).Select(x => Text(x, "OutputDigest")).ToHashSet(StringComparer.Ordinal);
            Require(digests.Count == 1 && digests.First() is { Length: > 0 },
                $"{profile}: compiler output/digest parity drift at {key}");

            if (policy.Stage is not null)
            {
                var stageKeys = StageNames(before[0]);
                foreach (var item in before.Concat(after))
                    Require(StageNames(item).SetEquals(stageKeys),
                        $"{profile}: compiler stage set changed at {key}");
                if (key.Case is not ("query-derived-32" or "query-scalar-32"))
                    continue;
            }

            var metricNames = policy.Stage is null
                ? new[] { "MeanAllocatedBytes", "MeanMicroseconds" }
                : ["MedianAllocatedBytes", "MedianMicroseconds"];
            var ratios = new List<string>(2);
            foreach (var metric in metricNames)
            {
                var b = Median(before.Select(x => policy.Stage is null
                    ? Number(x, metric) : Number(FindStage(x, policy.Stage), metric)));
                var h = Median(after.Select(x => policy.Stage is null
                    ? Number(x, metric) : Number(FindStage(x, policy.Stage), metric)));
                Require(double.IsFinite(b) && double.IsFinite(h) && b > 0
                        && (policy.RequirePositiveCandidate ? h > 0 : h >= 0),
                    $"{profile}: invalid measured {metric} for {key}");
                ratios.Add((h / b).ToString("F3", CultureInfo.InvariantCulture) + "x");
            }
            var label = policy.Lookup ? key.Case + " tables" : policy.View ? key.Case : $"{key.Provider} / {key.Case}";
            lines.Add($"| {label} | {ratios[0]} | {ratios[1]} |");
            outputRows++;
        }

        Require(outputRows == (policy.Stage is null ? policy.Rows : 12),
            $"{profile}: benchmark rows omitted or replaced");
        var markdown = string.Join("\n", lines) + "\n";
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, policy.Artifact), markdown);
        Console.Write(markdown);
        var summary = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        if (!string.IsNullOrWhiteSpace(summary) && File.Exists(summary))
            File.AppendAllText(summary, markdown);
        return outputRows;
    }

    private static void ValidateExpectedKeys(string profile, Policy policy, HashSet<(string Provider, string Case)> keys)
    {
        if (policy.Lookup)
        {
            Require(keys.SetEquals([("", "lookup-96"), ("", "lookup-256")]),
                "Catalog lookup requires exactly 96 and 256-table probes");
        }
        else if (policy.View)
        {
            Require(keys.SetEquals([("Postgres", "cascade-24"), ("Postgres", "cascade-48")]),
                "View-cascade requires both 24 and 48-stage PostgreSQL chains");
        }
        else if (policy.Stage is not null)
        {
            var expected = (from provider in SqlCoreStageProfile.Providers
                            from name in SqlCoreStageProfile.Cases
                            select (provider, name)).ToHashSet();
            Require(keys.SetEquals(expected), $"{profile}: missing/unsupported six-provider stage profile case");
        }
        else
        {
            var providers = SqlCoreStageProfile.Providers.ToHashSet(StringComparer.Ordinal);
            Require(keys.All(k => providers.Contains(k.Provider))
                    && keys.Select(k => k.Provider).Distinct(StringComparer.Ordinal).Count() == 6
                    && keys.GroupBy(k => k.Provider, StringComparer.Ordinal).All(g => g.Count() == 2),
                $"{profile}: expected six providers times two workloads");
        }
    }

    private static HashSet<string> StageNames(JsonElement row) =>
        Stages(row).Select(stage => Text(stage, "Name") ?? "")
            .ToHashSet(StringComparer.Ordinal);

    private static JsonElement FindStage(JsonElement row, string name)
    {
        var matches = Stages(row).Where(s => Text(s, "Name") == name).ToArray();
        Require(matches.Length == 1, $"Missing or duplicate {name} stage");
        return matches[0];
    }

    private static JsonElement[] Stages(JsonElement row) =>
        row.TryGetProperty("Stages", out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().ToArray() : [];

    private static string? Text(JsonElement row, string property) =>
        row.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static double Number(JsonElement row, string property) =>
        row.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out var number) ? number : double.NaN;

    private static double Median(IEnumerable<double> values)
    {
        var array = values.Order().ToArray();
        return array.Length % 2 == 0
            ? (array[array.Length / 2 - 1] + array[array.Length / 2]) / 2
            : array[array.Length / 2];
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new GovernanceException(message);
    }
}
