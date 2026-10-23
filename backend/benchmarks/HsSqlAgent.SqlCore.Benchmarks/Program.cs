using System.Diagnostics;
using System.Text.Json;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;

if (SqlCoreDepthProbe.TryRun(args)) return;

var options = BenchmarkOptions.Parse(args);
var cases = new[]
{
    new BenchmarkCase("query-filter-order", "SELECT id, name FROM users WHERE id >= 42 ORDER BY id", false),
    new BenchmarkCase("query-cte", "WITH filtered AS (SELECT id FROM users WHERE id >= 5) SELECT id FROM filtered", false),
    new BenchmarkCase("dml-update", "UPDATE users SET name = 'Alice' WHERE id = 10", true),
    new BenchmarkCase("dml-delete", "DELETE FROM users WHERE id = 10", true)
};
var results = new List<BenchmarkResult>();
foreach (var provider in Enum.GetValues<SqlAgentToolType>())
{
    var context = new SqlPlanValidationContext("sqlcore-benchmark-v1");
    foreach (var benchmarkCase in cases)
    {
        CompiledSqlCommand Compile() => benchmarkCase.IsDml
            ? SqlCoreFacade.CompileDml(benchmarkCase.Sql, provider, provider, context)
            : SqlCoreFacade.CompileQuery(
                benchmarkCase.Sql, provider, provider, context, new SqlExecutionPlanPolicy(100));

        var expected = Compile();
        for (var i = 0; i < options.Warmup; i++)
            AssertEquivalent(expected, Compile(), provider, benchmarkCase.Name);

        var measurements = new long[options.Iterations];
        var bytes = new long[options.Iterations];
        for (var i = 0; i < options.Iterations; i++)
        {
            var beforeBytes = GC.GetAllocatedBytesForCurrentThread();
            var beforeTicks = Stopwatch.GetTimestamp();
            var command = Compile();
            var afterTicks = Stopwatch.GetTimestamp();
            var afterBytes = GC.GetAllocatedBytesForCurrentThread();
            AssertEquivalent(expected, command, provider, benchmarkCase.Name);

            measurements[i] = afterTicks - beforeTicks;
            bytes[i] = afterBytes - beforeBytes;
        }

        Array.Sort(measurements);
        var p95Index = Math.Max(0, (int)Math.Ceiling(options.Iterations * 0.95) - 1);
        var unit = 1_000_000.0 / Stopwatch.Frequency;
        results.Add(new BenchmarkResult(
            Provider: provider.ToString(),
            Case: benchmarkCase.Name,
            Iterations: options.Iterations,
            MeanMicroseconds: Math.Round(measurements.Average() * unit, 3),
            P95Microseconds: Math.Round(measurements[p95Index] * unit, 3),
            MeanAllocatedBytes: Math.Round(bytes.Average(), 1)));
    }
}
var report = new
{
    schemaVersion = 1,
    startedUtc = DateTimeOffset.UtcNow.ToString("O"),
    framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
    processArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
    iterations = options.Iterations,
    warmup = options.Warmup,
    results
};
var output = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
if (options.Output is { } path)
{
    var fullPath = Path.GetFullPath(path);
    Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
    File.WriteAllText(fullPath, output);
}
else
{
    Console.WriteLine(output);
}

static void AssertEquivalent(
    CompiledSqlCommand expected,
    CompiledSqlCommand actual,
    SqlAgentToolType provider,
    string name)
{
    if (!string.Equals(expected.Sql, actual.Sql, StringComparison.Ordinal) ||
        expected.Parameters.Length != actual.Parameters.Length ||
        !string.Equals(expected.PlanFingerprint, actual.PlanFingerprint, StringComparison.Ordinal))
    {
        throw new InvalidOperationException(
            $"Non-deterministic SqlCore compilation: {provider}/{name}.");
    }
}

internal sealed record BenchmarkCase(string Name, string Sql, bool IsDml);
internal sealed record BenchmarkResult(
    string Provider,
    string Case,
    int Iterations,
    double MeanMicroseconds,
    double P95Microseconds,
    double MeanAllocatedBytes);

internal sealed record BenchmarkOptions(int Iterations, int Warmup, string? Output)
{
    public static BenchmarkOptions Parse(string[] args)
    {
        var smoke = args.Contains("--smoke", StringComparer.Ordinal);
        var iterations = smoke ? 2 : 500;
        var warmup = smoke ? 1 : 20;
        string? output = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--smoke") continue;
            if (args[i] == "--iterations" && i + 1 < args.Length &&
                int.TryParse(args[++i], out var parsedIterations))
            {
                iterations = parsedIterations;
            }
            else if (args[i] == "--warmup" && i + 1 < args.Length &&
                int.TryParse(args[++i], out var parsedWarmup))
            {
                warmup = parsedWarmup;
            }
            else if (args[i] == "--output" && i + 1 < args.Length)
            {
                output = args[++i];
            }
            else
            {
                throw new ArgumentException("Usage: --smoke [--iterations N] [--warmup N] [--output path]");
            }
        }
        if (iterations is < 1 or > 100_000 || warmup is < 0 or > 100_000)
            throw new ArgumentOutOfRangeException(nameof(args),
                "Iterations must be 1..100000 and warmup must be 0..100000.");
        return new BenchmarkOptions(iterations, warmup, output);
    }
}
