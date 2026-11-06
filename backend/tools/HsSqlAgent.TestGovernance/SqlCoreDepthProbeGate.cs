using System.Text.Json;

namespace HsSqlAgent.TestGovernance;

/// <summary>
/// Native replacement for the isolated 256 KiB SQL depth-probe assertions.
/// These are actual compilation, stage-evidence and bounded allocation gates;
/// a resource refusal must not be silently counted as a successful compile.
/// </summary>
public static class SqlCoreDepthProbeGate
{
    private static readonly string[] CoreStages =
        ["Parsing", "Binding", "Normalization", "Validation", "Policy", "Rendering"];

    private sealed record Policy(
        int Samples,
        bool CheckStages = true,
        long? MaxTotal2048 = null,
        long? MaxRender2048 = null,
        bool CheckResourceRejections = true,
        bool CheckNonnegativeStageMetrics = false,
        bool CheckFactsInspection = false,
        bool Projection = false);

    private static readonly Dictionary<string, Policy> Policies = new(StringComparer.Ordinal)
    {
        ["mixed-depth"] = new(6, CheckStages: false, MaxTotal2048: 120_000_000, CheckResourceRejections: false),
        ["query-phases"] = new(9, MaxTotal2048: 120_000_000, CheckResourceRejections: false, CheckNonnegativeStageMetrics: true),
        ["mixed-cte-derived"] = new(6, MaxTotal2048: 120_000_000, MaxRender2048: 60_000_000, CheckResourceRejections: false),
        ["nested-set"] = new(6, MaxTotal2048: 120_000_000, CheckResourceRejections: false, CheckNonnegativeStageMetrics: true),
        ["set-small"] = new(4, CheckResourceRejections: false),
        ["cte-small"] = new(4, CheckResourceRejections: false),
        ["mixed-small"] = new(4, MaxTotal2048: 120_000_000, CheckResourceRejections: false),
        ["lateral"] = new(4, MaxTotal2048: 120_000_000, MaxRender2048: 60_000_000, CheckResourceRejections: false, CheckNonnegativeStageMetrics: true),
        ["correlated"] = new(12, MaxTotal2048: 120_000_000, MaxRender2048: 60_000_000),
        ["prefixed"] = new(6, MaxTotal2048: 180_000_000, MaxRender2048: 60_000_000),
        ["double"] = new(6, MaxTotal2048: 220_000_000, MaxRender2048: 90_000_000),
        ["triple"] = new(6, MaxTotal2048: 310_000_000, MaxRender2048: 120_000_000),
        ["siblings"] = new(6, MaxTotal2048: 260_000_000, MaxRender2048: 100_000_000),
        ["comb"] = new(6, MaxTotal2048: 280_000_000, MaxRender2048: 120_000_000),
        ["facts"] = new(6, MaxTotal2048: 180_000_000, CheckFactsInspection: true),
        ["correlated-in"] = new(6, MaxTotal2048: 120_000_000, MaxRender2048: 60_000_000),
        ["projection"] = new(12, Projection: true),
    };

    public static string[] Profiles => Policies.Keys.Concat(["paging", "depth", "small-stack"]).ToArray();

    public static int Verify(string profile, string file)
    {
        using var source = JsonDocument.Parse(File.ReadAllText(file));
        var report = source.RootElement;
        Require(report.ValueKind == JsonValueKind.Object, $"{profile}: report must be a JSON object");
        Require(report.TryGetProperty("observations", out var observations)
                && observations.ValueKind == JsonValueKind.Array, $"{profile}: observations missing");

        if (profile == "depth" || profile == "small-stack")
        {
            Require(Int(report, "samples") >= 0 && Int(report, "crashesOrTimeouts") >= 0,
                $"{profile}: missing observational report counters");
            Require(profile != "depth" || Int(report, "failures") >= 0,
                "Depth observations must retain failure counts");
            Require(profile != "small-stack" || (Int(report, "resourceRejections") >= 0
                    && Int(report, "unexpectedFailures") >= 0),
                "Constrained-stack observations must retain resource rejection/failure counts");
            Console.WriteLine($"Observed {profile}: {observations.GetArrayLength()} diagnostic probes (observational only).");
            foreach (var sample in observations.EnumerateArray())
            {
                Require(Text(sample, "shape") is not null && Text(sample, "dialect") is not null
                        && Int(sample, "depth") >= 0
                        && sample.TryGetProperty("processExitCode", out var exit)
                        && exit.ValueKind == JsonValueKind.Number && exit.TryGetInt32(out _),
                    $"{profile}: missing observational worker identity or exit evidence");
                Console.WriteLine($"  {Text(sample, "shape")} / {Text(sample, "dialect")} depth={Int(sample, "depth")}");
            }
            return observations.GetArrayLength();
        }
        if (profile == "paging")
            return VerifyPaging(report, observations);

        Require(Policies.TryGetValue(profile, out var policy), $"Unknown depth-probe policy: {profile}");
        Require(Int(report, "samples") == policy.Samples && observations.GetArrayLength() == policy.Samples,
            $"{profile}: expected {policy.Samples} actual probe samples");
        Require(Int(report, "failures") == 0 && Int(report, "crashesOrTimeouts") == 0,
            $"{profile}: a SQL probe failed, crashed or timed out");
        if (policy.CheckResourceRejections)
            Require(Int(report, "resourceRejections") == 0,
                $"{profile}: resource rejection cannot substitute for successful compilation");

        var scalarCount = 0;
        var caseCount = 0;
        foreach (var item in observations.EnumerateArray())
        {
            var shape = Text(item, "shape") ?? throw new GovernanceException($"{profile}: missing shape");
            var depth = Int(item, "depth");
            var worker = Object(item, "worker");
            Require(Bool(worker, "success") && Int(item, "processExitCode") == 0,
                $"{profile}: worker must compile successfully at {shape}/{depth}, not merely refuse or exit");

            var metrics = Metrics(worker);
            var succeeded = metrics.Where(m => Bool(m, "Success"))
                .Select(m => Text(m, "Stage")).Where(s => s is not null).ToHashSet(StringComparer.Ordinal);
            if (policy.CheckStages)
            {
                var required = new HashSet<string>(CoreStages, StringComparer.Ordinal);
                if (shape.EndsWith("-typed-schema", StringComparison.Ordinal))
                    required.UnionWith(["SchemaBinding", "SchemaType"]);
                if (policy.CheckFactsInspection) required.Add("Inspection");
                Require(required.IsSubsetOf(succeeded!),
                    $"{profile}: a successful compilation omitted typed or required proof stages at {shape}/{depth}");
            }
            if (policy.CheckNonnegativeStageMetrics)
                foreach (var phase in metrics)
                    foreach (var name in new[] { "ElapsedMicroseconds", "AllocatedBytes", "WorkUnits" })
                        Require(Number(phase, name) >= 0,
                            $"{profile}: negative or missing stage resource metric {name} at {shape}/{depth}");

            if (policy.CheckFactsInspection)
            {
                var inspection = metrics.Where(m => Text(m, "Stage") == "Inspection").ToArray();
                Require(inspection.Length == 1 && Number(inspection[0], "WorkUnits") >= depth
                        && Number(inspection[0], "AllocatedBytes") >= 0,
                    $"{profile}: Query Facts Inspection work must account for every nested Query node");
                if (depth == 2048)
                    Require(Number(inspection[0], "AllocatedBytes") <= 40_000_000,
                        $"{profile}: 2048 Query Facts Inspection exceeded 40MB allocation");
            }

            var maxTotal = policy.MaxTotal2048;
            var maxRender = policy.MaxRender2048;
            if (policy.Projection)
            {
                var scalar = shape.Contains("scalar-subquery", StringComparison.Ordinal);
                if (scalar) scalarCount++; else caseCount++;
                maxTotal = scalar ? 32_000_000 : 90_000_000;
                maxRender = scalar ? 4_000_000 : 30_000_000;
            }

            if (depth == 2048)
            {
                if (maxTotal.HasValue)
                    Require(IsNonnegativeInteger(worker, "allocatedBytes")
                            && Number(worker, "allocatedBytes") <= maxTotal.Value,
                        $"{profile}: 2048 total allocation exceeded {maxTotal} bytes at {shape}");
                if (maxRender.HasValue)
                {
                    var rendering = metrics.Where(m => Text(m, "Stage") == "Rendering").ToArray();
                    Require(rendering.Length == 1 && Number(rendering[0], "AllocatedBytes") >= 0
                            && Number(rendering[0], "AllocatedBytes") <= maxRender.Value,
                        $"{profile}: 2048 Rendering exceeded {maxRender} bytes at {shape}");
                }
            }
            Console.WriteLine($"  {shape} depth={depth} compiled=true bytes={Number(worker, "allocatedBytes")}");
        }
        if (policy.Projection)
            Require(scalarCount == 6 && caseCount == 6,
                "All six scalar and six CASE projection probes must compile on 256 KiB");
        Console.WriteLine($"Validated {profile}: {policy.Samples}/{policy.Samples} real compiled SQL depth probes.");
        return policy.Samples;
    }

    private static int VerifyPaging(JsonElement report, JsonElement observations)
    {
        var providers = SqlCoreStageProfile.Providers;
        var shapes = new[] { "nested-paging-typed-schema", "invalid-nested-paging" };
        var depths = new[] { 128, 512, 2048 };
        var expected = (from provider in providers
                        from shape in shapes
                        from depth in depths
                        select (provider, shape, depth)).ToHashSet();
        Require(observations.GetArrayLength() == 36 && Int(report, "samples") == 36
                && Int(report, "failures") == 0 && Int(report, "crashesOrTimeouts") == 0,
            "Paging corpus is missing, refused, crashed or timed out");
        var seen = new HashSet<(string, string, int)>();
        foreach (var item in observations.EnumerateArray())
        {
            var provider = Text(item, "dialect") ?? "";
            var shape = Text(item, "shape") ?? "";
            var depth = Int(item, "depth");
            var key = (provider, shape, depth);
            Require(expected.Contains(key) && seen.Add(key),
                $"Paging corpus is missing, duplicated or replaced: {key}");
            Require(Int(item, "processExitCode") == 0 && Int(item, "stackBytes") == 262144,
                $"Paging probe exited unexpectedly or did not run on a 256KiB stack: {key}");
            var worker = Object(item, "worker");
            var phases = Metrics(worker);
            if (shape == "invalid-nested-paging")
            {
                Require(!Bool(worker, "success") && Bool(worker, "expectedGrammarRejection")
                        && Text(worker, "diagnosticCode") == "SQL_PARSE_GRAMMAR"
                        && Int(worker, "diagnosticOffset") == 18 * depth + 32
                        && phases.Length == 1 && Text(phases[0], "Stage") == "Parsing"
                        && !Bool(phases[0], "Success"),
                    $"Negative paging changed its first syntax diagnostic or reached later stages: {key}");
                Require(IsNonnegativeInteger(worker, "allocatedBytes")
                        && Number(worker, "allocatedBytes") <= 30_000_000,
                    $"Negative paging exceeds 30MB allocation: {key}");
            }
            else
            {
                var required = CoreStages.Concat(["SchemaBinding", "SchemaType"]).ToHashSet(StringComparer.Ordinal);
                var names = phases.Select(p => Text(p, "Stage")).ToArray();
                Require(Bool(worker, "success")
                        && IsJsonNullOrMissing(worker, "diagnosticCode")
                        && Int(worker, "parameterCount") == 2 * depth
                        && names.Length == required.Count
                        && names.All(name => name is not null && required.Contains(name))
                        && phases.All(p => Bool(p, "Success") && Number(p, "WorkUnits") > 0),
                    $"Positive paging did not complete every typed compiler stage: {key}");
                var rendering = phases.SingleOrDefault(p => Text(p, "Stage") == "Rendering");
                Require(IsNonnegativeInteger(worker, "allocatedBytes")
                        && Number(worker, "allocatedBytes") <= 120_000_000
                        && rendering.ValueKind == JsonValueKind.Object
                        && Number(rendering, "AllocatedBytes") <= 60_000_000
                        && Number(rendering, "AllocatedBytes") >= 0,
                    $"Paging allocation exceeded existing deep-query ceilings: {key}");
            }
        }
        Require(seen.SetEquals(expected), "Missing paging provider/shape/depth cases");
        Console.WriteLine("Six-provider paging: 18 full successes and 18 exact syntax negatives on 256KiB.");
        return 36;
    }

    private static JsonElement Object(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value : throw new GovernanceException($"Missing proof object {name}");

    private static JsonElement[] Metrics(JsonElement worker) =>
        worker.TryGetProperty("stageMetrics", out var metrics) && metrics.ValueKind == JsonValueKind.Array
            ? metrics.EnumerateArray().ToArray() : [];

    private static string? Text(JsonElement owner, string name) =>
        owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int Int(JsonElement owner, string name) =>
        owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed) ? parsed : -1;

    private static bool Bool(JsonElement owner, string name) =>
        owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.True;

    private static bool IsNonnegativeInteger(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var parsed) && parsed >= 0;

    private static bool IsJsonNullOrMissing(JsonElement owner, string name) =>
        !owner.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null;

    private static double Number(JsonElement owner, string name) =>
        owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var parsed)
            && double.IsFinite(parsed) ? parsed : double.NaN;

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new GovernanceException(message);
    }
}
