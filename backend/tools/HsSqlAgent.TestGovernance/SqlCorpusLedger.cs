using System.Text.Json;

namespace HsSqlAgent.TestGovernance;

/// <summary>
/// Inventory observed SQL regression fixtures across all six providers.
/// The manifest is not an SQL-standard denominator or a claim of live parity.
/// </summary>
public static class SqlCorpusLedger
{
    public static readonly string[] Providers = ["Postgres", "MySQL", "MsSqlServer", "Sqlite", "Oracle", "Firebird"];
    private static readonly string[] Families = ["DQL", "DML", "DDL", "DCL", "TCL", "Session", "Utility", "Procedural"];

    public static object Build(string manifestPath)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = manifest.RootElement;
        Require(root.ValueKind == JsonValueKind.Object && Int(root, "schemaVersion") == 1, "Expected schemaVersion 1 coverage manifest");
        Require(TryArray(root, "suites", out var suites) && suites.GetArrayLength() > 0,
            "Coverage manifest must define at least one suite");

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var seenFiles = new HashSet<string>(StringComparer.Ordinal);
        var byProvider = Providers.ToDictionary(p => p,
            p => new ProviderCounts(0, 0, Families.ToDictionary(f => f, _ => 0, StringComparer.Ordinal)),
            StringComparer.Ordinal);
        var reports = new List<object>();
        var directory = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;

        foreach (var suite in suites.EnumerateArray())
        {
            Require(suite.ValueKind == JsonValueKind.Object, "Coverage suite must be an object");
            var id = RequiredString(suite, "id");
            var file = RequiredString(suite, "file");
            Require(seenIds.Add(id), "Coverage suite id is missing or duplicate");
            Require(file.EndsWith(".json", StringComparison.Ordinal)
                    && file == Path.GetFileName(file) && !file.Contains('\\') && seenFiles.Add(file),
                $"{id}: invalid or repeated corpus file");
            var family = RequiredString(suite, "statementFamily");
            Require(Families.Contains(family, StringComparer.Ordinal), $"{id}: invalid statementFamily");
            Require(TryBoolean(suite, "requiresVersionProfile", out var versionRequired),
                $"{id}: requiresVersionProfile must be boolean");
            Require(TryBoolean(suite, "nativeOnly", out var nativeOnly), $"{id}: nativeOnly must be boolean");
            Require(suite.TryGetProperty("expectedByProvider", out var declared)
                    && declared.ValueKind == JsonValueKind.Object
                    && declared.EnumerateObject().Count() == Providers.Length
                    && declared.EnumerateObject().All(x => Providers.Contains(x.Name, StringComparer.Ordinal)),
                $"{id}: expectedByProvider must enumerate all six providers");

            var expected = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var provider in Providers)
            {
                Require(declared.TryGetProperty(provider, out var value) && value.ValueKind == JsonValueKind.Number
                        && value.TryGetInt32(out var count) && count >= 0,
                    $"{id}: expectedByProvider must enumerate all six providers");
                expected[provider] = count;
            }
            Require(expected.Values.Sum() > 0, $"{id}: expectedByProvider must enumerate all six providers");

            using var corpus = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, file)));
            var cases = corpus.RootElement;
            Require(cases.ValueKind == JsonValueKind.Array && cases.GetArrayLength() > 0,
                $"{id}: corpus must be a nonempty JSON array");
            var counts = Providers.ToDictionary(p => p, _ => 0, StringComparer.Ordinal);
            var versioned = Providers.ToDictionary(p => p, _ => 0, StringComparer.Ordinal);
            var names = new HashSet<string>(StringComparer.Ordinal);

            for (var index = 0; index < cases.GetArrayLength(); index++)
            {
                var item = cases[index];
                var label = $"{id}[{index}]";
                Require(item.ValueKind == JsonValueKind.Object, $"{label}: case must be an object");
                var name = RequiredString(item, "name", $"{label}: missing case name");
                Require(names.Add(name), $"{label}: duplicate case name '{name}'");
                var dialect = RequiredString(item, "dialect", $"{label}: invalid dialect");
                Require(Providers.Contains(dialect, StringComparer.Ordinal), $"{label}: invalid dialect '{dialect}'");
                _ = RequiredString(item, "sql", $"{label}: missing SQL text");
                var target = item.TryGetProperty("targetDialect", out var targetValue)
                    ? (targetValue.ValueKind == JsonValueKind.String ? targetValue.GetString() : null) : dialect;
                Require(target is not null && Providers.Contains(target, StringComparer.Ordinal),
                    $"{label}: invalid targetDialect '{target}'");
                Require(!nativeOnly || dialect == target,
                    $"{label}: targetDialect '{target}' differs from native source '{dialect}'");

                var sourceVersion = OptionalNonemptyString(item, "sourceVersion");
                var targetVersion = OptionalNonemptyString(item, "targetVersion");
                if (versionRequired)
                {
                    Require(sourceVersion is not null, $"{label}: sourceVersion required");
                    Require(targetVersion is not null, $"{label}: targetVersion required");
                }
                if (sourceVersion is not null && targetVersion is not null)
                    versioned[dialect]++;
                counts[dialect]++;
            }

            Require(Providers.All(p => counts[p] == expected[p]),
                $"{id}: expectedByProvider drift: declared counts disagree with actual corpus");

            foreach (var provider in Providers)
            {
                var current = byProvider[provider];
                current.TotalCases += counts[provider];
                current.VersionProfiledCases += versioned[provider];
                current.ByFamily[family] += counts[provider];
            }
            reports.Add(new
            {
                id, file, statementFamily = family, cases = cases.GetArrayLength(),
                byProvider = counts, versionProfiledCases = versioned.Values.Sum(),
            });
        }

        return new
        {
            schemaVersion = 1,
            evidenceLevel = "observed-compiler-regression-corpus",
            description = "Case inventory only; existing compiler matrix tests run separately in CI.",
            officialGrammarDenominator = (int?)null,
            liveExecutionProven = false,
            totalCases = byProvider.Values.Sum(x => x.TotalCases),
            providers = byProvider,
            suites = reports,
        };
    }

    private sealed class ProviderCounts(int totalCases, int versionProfiledCases, Dictionary<string, int> byFamily)
    {
        public int TotalCases { get; set; } = totalCases;
        public int VersionProfiledCases { get; set; } = versionProfiledCases;
        public Dictionary<string, int> ByFamily { get; } = byFamily;
    }

    private static bool TryArray(JsonElement item, string property, out JsonElement result) =>
        item.TryGetProperty(property, out result) && result.ValueKind == JsonValueKind.Array;

    private static bool TryBoolean(JsonElement item, string property, out bool result)
    {
        result = false;
        if (!item.TryGetProperty(property, out var value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return false;
        result = value.GetBoolean();
        return true;
    }

    private static int Int(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var count) ? count : -1;

    private static string? OptionalNonemptyString(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString() : null;

    private static string RequiredString(JsonElement item, string property, string? error = null) =>
        OptionalNonemptyString(item, property) ?? throw new GovernanceException(error ?? $"Invalid or missing {property}");

    private static void Require(bool valid, string reason)
    {
        if (!valid) throw new GovernanceException(reason);
    }
}
