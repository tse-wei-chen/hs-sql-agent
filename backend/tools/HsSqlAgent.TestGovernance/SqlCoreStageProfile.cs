using System.Text.Json;

namespace HsSqlAgent.TestGovernance;

/// <summary>Enforce complete six-provider, eight-workload, six-stage compiled SQL telemetry.</summary>
public static class SqlCoreStageProfile
{
    public static readonly string[] Providers = ["Sqlite", "MsSqlServer", "MySQL", "Postgres", "Oracle", "Firebird"];
    public static readonly string[] Cases =
    [
        "query-filter-order", "query-cte", "dml-update", "dml-delete",
        "dml-insert", "query-union", "query-derived-32", "query-scalar-32",
    ];
    public static readonly string[] RequiredStages = ["Parsing", "Binding", "Normalization", "Validation", "Policy", "Rendering"];

    public static int Verify(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        Require(Int(root, "schemaVersion") == 1, "Unrecognized stage-profile schema");
        var iterations = Int(root, "iterations");
        Require(iterations >= 2, "Stage-profile sample count requires at least two iterations");
        Require(Int(root, "warmup") >= 1, "Stage profile must have warmup");
        Require(root.TryGetProperty("results", out var rows) && rows.ValueKind == JsonValueKind.Array
                && rows.GetArrayLength() == Providers.Length * Cases.Length,
            "Stage profile requires all 48 provider/workload cases");

        var expected = (from provider in Providers from name in Cases select (provider, name)).ToHashSet();
        var seen = new HashSet<(string, string)>();
        foreach (var row in rows.EnumerateArray())
        {
            Require(row.ValueKind == JsonValueKind.Object, "Invalid provider/workload case");
            var key = (Text(row, "Provider"), Text(row, "Case"));
            Require(expected.Contains(key) && seen.Add(key),
                "Missing/duplicate/unsupported provider case in 48-case corpus");
            var digest = Text(row, "OutputDigest");
            Require(digest is not null && digest.Length == 64, "Missing typed output digest");
            Require(row.TryGetProperty("Stages", out var stages) && stages.ValueKind == JsonValueKind.Array,
                "Missing required compiler stage telemetry");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var stage in stages.EnumerateArray())
            {
                Require(stage.ValueKind == JsonValueKind.Object, "Invalid compiler stage telemetry");
                var name = Text(stage, "Name");
                Require(name is not null && names.Add(name), "duplicate compiler stage telemetry");
                Require(Int(stage, "Samples") == iterations,
                    $"Invalid compiler-stage sample count: {key}/{name}");
                Require(stage.TryGetProperty("AllSucceeded", out var success)
                        && success.ValueKind == JsonValueKind.True,
                    $"Compiler-stage failure: {key}/{name}");
                foreach (var metric in new[] { "MedianMicroseconds", "MedianAllocatedBytes", "MedianWorkUnits" })
                {
                    Require(stage.TryGetProperty(metric, out var value)
                            && value.ValueKind == JsonValueKind.Number
                            && value.TryGetDouble(out var number)
                            && double.IsFinite(number) && number >= 0,
                        $"Invalid stage metric {metric}: {key}/{name}");
                }
            }
            Require(RequiredStages.All(names.Contains), "Missing required compiler stage telemetry");
        }
        Require(seen.SetEquals(expected), "Missing 48-case compiler stage corpus entries");
        return seen.Count;
    }

    private static int Int(JsonElement parent, string property) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(property, out var item)
            && item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var result) ? result : -1;
    private static string? Text(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
    private static void Require(bool valid, string reason)
    {
        if (!valid) throw new GovernanceException(reason);
    }
}
