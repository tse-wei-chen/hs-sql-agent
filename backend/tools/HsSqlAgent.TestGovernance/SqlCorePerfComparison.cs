using System.Text.Json;

namespace HsSqlAgent.TestGovernance;

/// <summary>Three paired, six-provider release rounds; never hide missing workloads or changed SQL digests.</summary>
public static class SqlCorePerfComparison
{
    private static readonly string[] Metrics = ["MeanMicroseconds", "P95Microseconds", "MeanAllocatedBytes"];
    private static readonly string[] Settings = ["framework", "os", "processArchitecture", "iterations", "warmup"];

    public static object Compare(string folder)
    {
        var baseline = LoadRuns(folder, "base");
        var candidate = LoadRuns(folder, "head");
        var reference = baseline[0].Settings;
        foreach (var run in baseline.Concat(candidate))
            foreach (var setting in Settings)
                Require(run.Settings[setting] == reference[setting], "Runtime/settings/workload mismatch");

        var failures = new List<string>();
        var results = new List<object>();
        foreach (var key in ExpectedKeys().OrderBy(x => x.Provider, StringComparer.Ordinal).ThenBy(x => x.Case, StringComparer.Ordinal))
        {
            var b = baseline.Select(run => run.Rows[key]).ToArray();
            var h = candidate.Select(run => run.Rows[key]).ToArray();
            if (b.Concat(h).Select(row => row.Digest).Distinct(StringComparer.Ordinal).Count() != 1)
                failures.Add($"({key.Provider}, {key.Case}): emitted SQL/typed parameter values changed");

            var values = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var metric in Metrics)
            {
                var before = Median(b.Select(x => x.Metrics[metric]));
                var after = Median(h.Select(x => x.Metrics[metric]));
                values[metric] = new { @base = before, head = after, ratio = Math.Round(after / before, 4) };
            }
            foreach (var (metric, factor, slack) in new[]
            {
                ("MeanAllocatedBytes", 2.0, 4096.0),
                ("MeanMicroseconds", 3.0, 50.0),
                ("P95Microseconds", 3.0, 100.0),
            })
            {
                var before = Median(b.Select(x => x.Metrics[metric]));
                var after = Median(h.Select(x => x.Metrics[metric]));
                if (after > factor * before + slack)
                    failures.Add($"({key.Provider}, {key.Case}): {metric} exceeds {factor}x baseline + {slack}");
            }
            var row = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["provider"] = key.Provider, ["case"] = key.Case,
            };
            foreach (var (metric, value) in values) row[metric] = value;
            results.Add(row);
        }
        var report = new { schemaVersion = 1, results, failures };
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "comparison.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        if (failures.Count != 0) throw new GovernanceException(string.Join("\n", failures));
        return report;
    }

    private static List<Run> LoadRuns(string folder, string prefix)
    {
        var files = Directory.GetFiles(folder, prefix + "-*.json")
            .Order(StringComparer.Ordinal).ToArray();
        Require(files.Length == 3, $"Expected three {prefix} runs, got {files.Length}");
        var expected = ExpectedKeys();
        var runs = new List<Run>(3);
        foreach (var file in files)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            var root = document.RootElement;
            var iterations = Int(root, "iterations");
            Require(Int(root, "schemaVersion") == 2 || false, $"Non-comparable benchmark settings: {file}");
            Require(iterations >= 100 && Int(root, "warmup") >= 20,
                $"Non-comparable benchmark settings: {file}");
            var settings = Settings.ToDictionary(s => s, s =>
                root.TryGetProperty(s, out var value) ? value.GetRawText() : string.Empty);
            Require(root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array,
                $"Expected all 6 providers x 8 workloads: {file}");
            var rows = new Dictionary<(string Provider, string Case), PerfRow>();
            foreach (var item in results.EnumerateArray())
            {
                var key = (Text(item, "Provider"), Text(item, "Case"));
                Require(key.Item1 is not null && key.Item2 is not null,
                    $"Expected all 6 providers x 8 workloads: {file}");
                var typedKey = (Provider: key.Item1!, Case: key.Item2!);
                Require(expected.Contains(typedKey) && !rows.ContainsKey(typedKey),
                    $"Expected all 6 providers x 8 workloads: {file}");
                Require(Int(item, "Iterations") == iterations, $"Per-case iteration mismatch: {file}");
                var metrics = new Dictionary<string, double>();
                foreach (var metric in Metrics)
                {
                    Require(item.TryGetProperty(metric, out var number)
                            && number.ValueKind == JsonValueKind.Number
                            && number.TryGetDouble(out var value)
                            && double.IsFinite(value) && value > 0,
                        $"Invalid {metric}: {file}");
                    metrics[metric] = number.GetDouble();
                }
                rows.Add(typedKey, new PerfRow(Text(item, "OutputDigest") ?? "", metrics));
            }
            Require(rows.Count == expected.Count, $"Expected all 6 providers x 8 workloads: {file}");
            runs.Add(new Run(settings, rows));
        }
        return runs;
    }

    private static HashSet<(string Provider, string Case)> ExpectedKeys() =>
        (from provider in SqlCoreStageProfile.Providers
         from name in SqlCoreStageProfile.Cases
         select (provider, name)).ToHashSet();

    private static double Median(IEnumerable<double> values)
    {
        var a = values.Order().ToArray();
        return a.Length % 2 == 0 ? (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2 : a[a.Length / 2];
    }

    private static string? Text(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int Int(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var result) ? result : -1;

    private static void Require(bool valid, string message)
    {
        if (!valid) throw new GovernanceException(message);
    }

    private sealed record PerfRow(string Digest, Dictionary<string, double> Metrics);
    private sealed record Run(
        Dictionary<string, string> Settings,
        Dictionary<(string Provider, string Case), PerfRow> Rows);
}
