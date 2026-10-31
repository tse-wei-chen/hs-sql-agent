using System.Diagnostics;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Script;
using HsSqlAgent.SqlCore.Models;

if (SqlCoreCapabilityStackProbe.TryRun(args)) return;
if (SqlCoreDepthProbe.TryRun(args)) return;

var catalogStress = args.Contains("--catalog-stress", StringComparer.Ordinal);
var scriptMode = catalogStress || args.Contains("--script-analysis", StringComparer.Ordinal);
var options = BenchmarkOptions.Parse(scriptMode
    ? args.Where(arg => arg is not ("--script-analysis" or "--catalog-stress")).ToArray()
    : args);
if (scriptMode)
{
    // Opt-in same-runner base/head measurement, never part of SQL compiler timings.
    // Both revisions get this identical harness from the candidate checkout.
    var workloads = catalogStress
        ? new[]
        {
            (Name: "catalog-create-24", Sql: CatalogScript(24)),
            (Name: "catalog-create-96", Sql: CatalogScript(96))
        }
        : new[]
        {
            (Name: "four-queries", Sql: "SELECT 1 AS a; SELECT 2 AS b; SELECT 3 AS c; SELECT 4 AS d;"),
            (Name: "catalog-dml-effects",
                Sql: "CREATE TABLE users(id INTEGER, name VARCHAR(40));" +
                     " SELECT id FROM users; INSERT INTO users(id) VALUES(1);" +
                     " UPDATE users SET name='Ada' WHERE id=1; DELETE FROM users WHERE id=1;")
        };
    var rows = new List<object>();
    foreach (var provider in Enum.GetValues<SqlAgentToolType>())
    foreach (var workload in workloads)
    {
        var profile = catalogStress && provider == SqlAgentToolType.MsSqlServer
            ? new SqlProviderCapabilityProfile(provider, new Version(16, 0), default, null,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                { ["CATALOG_COLLATION"] = "Latin1_General_100_CI_AS" })
            : null;
        SqlScriptAnalysisResult Analyze() => SqlCoreFacade.AnalyzeScript(
            workload.Sql, provider, SqlCatalogState.KnownEmpty(), profile);
        string Signature(SqlScriptAnalysisResult analysis) => JsonSerializer.Serialize(
            analysis.Statements.Select(statement => new {
                statement.Syntax.Kind, ParseStatus = statement.Syntax.Status, statement.Status,
                statement.CatalogBinding, statement.NameBinding, statement.TypeBinding,
                statement.Certainty, statement.EffectCoverage,
                Diagnostics = statement.Diagnostics.Select(d => d.Code).ToArray(),
                Changes = statement.Changes.Select(c => new { c.Operation, c.TableName, c.Certainty }).ToArray(),
                Effects = statement.DetailedEffects.Select(e =>
                    new { e.Kind, e.Operation, e.ObjectName, e.Certainty,
                          Dependencies = e.Dependencies.ToArray() }).ToArray()
            }).ToArray());
        var expected = Analyze();
        if (catalogStress)
        {
            var expectedTables = workload.Name == "catalog-create-24" ? 24 : 96;
            if (!expected.FinalCatalog.IsClosedWorld
                || expected.FinalCatalog.Tables.Count != expectedTables
                || expected.Statements.Take(expectedTables).Any(statement =>
                    statement.Status != SqlStatementAnalysisStatus.Analyzed))
                throw new InvalidOperationException(
                    $"Catalog stress workload is not a proven DDL transition: {provider}/{workload.Name}.");
        }
        var digest = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(Signature(expected))));
        for (var warmup = 0; warmup < options.Warmup; warmup++) Analyze();
        var elapsed = new long[options.Iterations];
        var allocated = new long[options.Iterations];
        for (var iteration = 0; iteration < options.Iterations; iteration++)
        {
            var beforeBytes = GC.GetAllocatedBytesForCurrentThread();
            var beforeTicks = Stopwatch.GetTimestamp();
            var actual = Analyze();
            var afterTicks = Stopwatch.GetTimestamp();
            var afterBytes = GC.GetAllocatedBytesForCurrentThread();
            if (actual.Statements.Count != expected.Statements.Count)
                throw new InvalidOperationException("Script analysis statement count changed.");
            elapsed[iteration] = afterTicks - beforeTicks;
            allocated[iteration] = afterBytes - beforeBytes;
        }
        Array.Sort(elapsed);
        var unit = 1_000_000.0 / Stopwatch.Frequency;
        var p95Index = Math.Max(0, (int)Math.Ceiling(options.Iterations * 0.95) - 1);
        rows.Add(new {
            Provider = provider.ToString(), Case = workload.Name, OutputDigest = digest,
            MeanMicroseconds = Math.Round(elapsed.Average() * unit, 3),
            P95Microseconds = Math.Round(elapsed[p95Index] * unit, 3),
            MeanAllocatedBytes = Math.Round(allocated.Average(), 1)
        });
    }
    var scriptReport = JsonSerializer.Serialize(new { schemaVersion = 1, results = rows },
        new JsonSerializerOptions { WriteIndented = true });
    if (options.Output is { } destination)
    {
        var full = Path.GetFullPath(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, scriptReport);
    }
    else Console.WriteLine(scriptReport);
    return;
}
var cases = new[]
{
    new BenchmarkCase("query-filter-order", "SELECT id, name FROM users WHERE id >= 42 ORDER BY id", false),
    new BenchmarkCase("query-cte", "WITH filtered AS (SELECT id FROM users WHERE id >= 5) SELECT id FROM filtered", false),
    new BenchmarkCase("dml-update", "UPDATE users SET name = 'Alice' WHERE id = 10", true),
    new BenchmarkCase("dml-delete", "DELETE FROM users WHERE id = 10", true),
    new BenchmarkCase("dml-insert", "INSERT INTO users (id, name) VALUES (10, 'Alice')", true),
    new BenchmarkCase("query-union", "SELECT id FROM users WHERE id = 11 UNION ALL SELECT id FROM users WHERE id = 22", false),
    new BenchmarkCase("query-derived-32", NestedDerived(32), false),
    new BenchmarkCase("query-scalar-32", NestedScalar(32), false)
};
if (options.StageProfile)
{
    SqlCoreStageProfiler.Run(cases, options);
    return;
}
var results = new List<BenchmarkResult>();
foreach (var provider in Enum.GetValues<SqlAgentToolType>())
{
    var context = new SqlPlanValidationContext("sqlcore-benchmark-v1");
    foreach (var benchmarkCase in cases)
    {
        CompiledSqlCommand Compile() => benchmarkCase.Compile(provider, context);

        var expected = Compile();
        for (var i = 0; i < options.Warmup; i++)
            BenchmarkCase.AssertEquivalent(expected, Compile(), provider, benchmarkCase.Name);

        var measurements = new long[options.Iterations];
        var bytes = new long[options.Iterations];
        for (var i = 0; i < options.Iterations; i++)
        {
            var beforeBytes = GC.GetAllocatedBytesForCurrentThread();
            var beforeTicks = Stopwatch.GetTimestamp();
            var command = Compile();
            var afterTicks = Stopwatch.GetTimestamp();
            var afterBytes = GC.GetAllocatedBytesForCurrentThread();
            BenchmarkCase.AssertEquivalent(expected, command, provider, benchmarkCase.Name);

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
            MeanAllocatedBytes: Math.Round(bytes.Average(), 1),
            OutputDigest: BenchmarkCase.OutputDigest(expected)));
    }
}
var report = new
{
    schemaVersion = 2,
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

static string CatalogScript(int tableCount) =>
    string.Join(" ", Enumerable.Range(0, tableCount).Reverse()
        .Select(index => $"CREATE TABLE t_{index:000} (id INTEGER);")) +
    " SELECT id FROM t_000;";

static string NestedDerived(int depth)
{
    var sql = "SELECT id FROM users WHERE id = 11";
    for (var i = 0; i < depth; i++)
        sql = $"SELECT d{i}.id FROM ({sql}) d{i}";
    return sql;
}

static string NestedScalar(int depth)
{
    var sql = "SELECT id FROM users WHERE id = 11";
    for (var i = 0; i < depth; i++)
        sql = $"SELECT ({sql}) AS id FROM users";
    return sql;
}

internal sealed record BenchmarkCase(string Name, string Sql, bool IsDml)
{
    public CompiledSqlCommand Compile(SqlAgentToolType provider, SqlPlanValidationContext context) =>
        IsDml
            ? SqlCoreFacade.CompileDml(Sql, provider, provider, context)
            : SqlCoreFacade.CompileQuery(Sql, provider, provider, context, new SqlExecutionPlanPolicy(100));

    public static string OutputDigest(CompiledSqlCommand command) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { command.Sql,
                Parameters = command.Parameters.Select(p => new {
                    Type = p.Value?.GetType().FullName, p.Value }) }))));

    public static void AssertEquivalent(
        CompiledSqlCommand expected,
        CompiledSqlCommand actual,
        SqlAgentToolType provider,
        string name)
    {
        if (!string.Equals(expected.Sql, actual.Sql, StringComparison.Ordinal) ||
            !expected.Parameters.Select(p => p.Value).SequenceEqual(actual.Parameters.Select(p => p.Value)) ||
            !string.Equals(expected.PlanFingerprint, actual.PlanFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Non-deterministic SqlCore compilation: {provider}/{name}.");
    }
}
internal sealed record BenchmarkResult(
    string Provider,
    string Case,
    int Iterations,
    double MeanMicroseconds,
    double P95Microseconds,
    double MeanAllocatedBytes,
    string OutputDigest);

internal sealed record BenchmarkOptions(int Iterations, int Warmup, string? Output, bool StageProfile)
{
    public static BenchmarkOptions Parse(string[] args)
    {
        var smoke = args.Contains("--smoke", StringComparer.Ordinal);
        var stageProfile = args.Contains("--stage-profile", StringComparer.Ordinal);
        var iterations = smoke ? 2 : 500;
        var warmup = smoke ? 1 : 20;
        string? output = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "--smoke" or "--stage-profile") continue;
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
                throw new ArgumentException("Usage: [--smoke] [--stage-profile] [--iterations N] [--warmup N] [--output path]");
            }
        }
        if (iterations is < 1 or > 100_000 || warmup is < 0 or > 100_000)
            throw new ArgumentOutOfRangeException(nameof(args),
                "Iterations must be 1..100000 and warmup must be 0..100000.");
        return new BenchmarkOptions(iterations, warmup, output, stageProfile);
    }
}
