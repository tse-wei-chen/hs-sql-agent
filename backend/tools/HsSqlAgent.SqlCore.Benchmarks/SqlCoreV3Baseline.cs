using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Ddl;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;

/// <summary>
/// U0 / T05 / T06: static, repeatable CLR API/grammar/compile and latency
/// observations. This never opens a database or authorizes SQL execution.
/// </summary>
internal static class SqlCoreV3Baseline
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly SqlAgentToolType[] Dialects =
    [
        SqlAgentToolType.Postgres, SqlAgentToolType.MySQL, SqlAgentToolType.MsSqlServer,
        SqlAgentToolType.Sqlite, SqlAgentToolType.Oracle, SqlAgentToolType.Firebird
    ];

    private static readonly (string Name, string Sql)[] Families =
    [
        ("dql", "SELECT 1"),
        ("dml", "DELETE FROM users WHERE id = 1"),
        ("ddl", "CREATE TABLE users(id INTEGER)"),
        ("dcl", "GRANT SELECT ON users TO analyst"),
        ("tcl", "COMMIT"),
        ("session", "SET search_path TO public"),
        ("utility", "EXPLAIN SELECT 1"),
        ("procedural", "CALL run_job()"),
        ("unknown", "MYSTERY 1"),
        ("invalid-query", "SELECT FROM"),
        ("invalid-mutation", "UPDATE users SET WHERE id = 1")
    ];

    public static bool TryRun(string[] args)
    {
        var mode = args.FirstOrDefault(arg => arg is "--v3-abi" or "--v3-inventory" or "--v3-perf");
        if (mode is null) return false;
        var filtered = args.Where(arg => arg != mode).ToArray();
        var options = CliOptions.Parse(filtered);
        object report = mode switch
        {
            "--v3-abi" => CapturePublicAbi(),
            "--v3-inventory" => CaptureInventory(),
            _ => Measure(options)
        };
        var json = JsonSerializer.Serialize(report, JsonOptions);
        if (options.Output is { } path)
        {
            var full = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, json);
        }
        else Console.WriteLine(json);
        return true;
    }

    private static object CapturePublicAbi()
    {
        // GetExportedTypes excludes F# internal typestate and domain DU by contract.
        // Freeze ALL externally visible CLR signatures, not just the facade.
        var asm = typeof(SqlCoreFacade).Assembly;
        var signatures = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var type in asm.GetExportedTypes())
        {
            var typeName = TypeName(type);
            signatures.Add("type " + typeName + " : " + TypeName(type.BaseType));
            foreach (var nested in type.GetInterfaces())
                signatures.Add("interface " + typeName + " : " + TypeName(nested));
            var flags = BindingFlags.Public | BindingFlags.DeclaredOnly |
                        BindingFlags.Instance | BindingFlags.Static;
            foreach (var field in type.GetFields(flags))
                signatures.Add("field " + typeName + "." + field.Name +
                               " : " + TypeName(field.FieldType));
            foreach (var prop in type.GetProperties(flags))
                signatures.Add("property " + typeName + "." + prop.Name +
                               " : " + TypeName(prop.PropertyType) +
                               " [" + (prop.GetMethod?.IsPublic == true ? "get" : "") +
                               (prop.SetMethod?.IsPublic == true ? ",set" : "") + "]");
            foreach (var evt in type.GetEvents(flags))
                signatures.Add("event " + typeName + "." + evt.Name +
                               " : " + TypeName(evt.EventHandlerType));
            foreach (var method in type.GetMethods(flags).Where(m => !m.IsSpecialName))
                signatures.Add("method " + typeName + "." + method.Name +
                               "(" + string.Join(",", method.GetParameters().Select(Parameter)) +
                               ") : " + TypeName(method.ReturnType) +
                               (method.IsStatic ? " static" : ""));
            foreach (var ctor in type.GetConstructors(flags))
                signatures.Add("ctor " + typeName +
                               "(" + string.Join(",", ctor.GetParameters().Select(Parameter)) + ")");
        }
        // Stable across builds, machines and .NET runtime versions; method argument
        // names/default values do not form CLR binary signatures.
        return new
        {
            schemaVersion = 1,
            assembly = asm.GetName().Name,
            exportedTypes = asm.GetExportedTypes().Length,
            signatures = signatures.ToArray(),
            sha256 = Digest(string.Join("\n", signatures))
        };
    }

    private static string Parameter(ParameterInfo parameter) =>
        (parameter.IsOut ? "out " : "") + TypeName(parameter.ParameterType);

    private static string TypeName(Type? type)
    {
        if (type is null) return "<none>";
        if (type.IsByRef) return TypeName(type.GetElementType()) + "&";
        if (type.IsArray) return TypeName(type.GetElementType()) + "[]";
        if (type.IsGenericParameter) return "!" + type.GenericParameterPosition;
        if (!type.IsGenericType) return type.FullName ?? type.Name;
        var name = type.GetGenericTypeDefinition().FullName ?? type.Name;
        return name + "<" + string.Join(",", type.GetGenericArguments().Select(TypeName)) + ">";
    }

    private static object CaptureInventory()
    {
        var rows = new List<object>();
        foreach (var provider in Dialects)
        {
            foreach (var (name, sql) in Families)
            {
                var result = SqlCoreFacade.ParseScript(sql, provider);
                rows.Add(new
                {
                    provider = provider.ToString(),
                    familyCase = name,
                    source = sql,
                    batchCount = result.BatchCount,
                    statements = result.Statements.Select(s => new
                    {
                        family = s.Kind.ToString(), parseStatus = s.Status.ToString(),
                        s.BatchIndex, spanStart = s.Span.Start, spanLength = s.Span.Length,
                        code = s.Diagnostic?.Code,
                        stage = s.Diagnostic?.Stage.ToString(),
                        category = s.Diagnostic?.Category.ToString(),
                        diagnosticStart = s.Diagnostic?.Span?.Start,
                        diagnosticLength = s.Diagnostic?.Span?.Length
                    }).ToArray()
                });
            }
            var context = new SqlPlanValidationContext("sqlcore-v3-u0-inventory");
            var policy = new SqlExecutionPlanPolicy();
            var compilations = new (string Name, Func<object> Execute)[]
            {
                ("query-valid", () => SqlCoreFacade.TryCompileQuery("SELECT 1", provider, provider, context, policy)),
                ("query-invalid", () => SqlCoreFacade.TryCompileQuery("SELECT FROM", provider, provider, context, policy)),
                ("query-wrong-family", () => SqlCoreFacade.TryCompileQuery("DELETE FROM users WHERE id=1", provider, provider, context, policy)),
                ("dml-valid", () => SqlCoreFacade.TryCompileDml("DELETE FROM users WHERE id=1", provider, provider, context)),
                ("dml-invalid", () => SqlCoreFacade.TryCompileDml("UPDATE users SET WHERE id=1", provider, provider, context)),
                ("dml-wrong-family", () => SqlCoreFacade.TryCompileDml("SELECT 1", provider, provider, context)),
                ("ddl-valid", () => SqlCoreFacade.TryCompileDdl("CREATE TABLE users(id INTEGER)", new DdlCompilationOptions(provider, provider))),
                ("ddl-invalid", () => SqlCoreFacade.TryCompileDdl("CREATE TABLE users(", new DdlCompilationOptions(provider, provider))),
                ("ddl-wrong-family", () => SqlCoreFacade.TryCompileDdl("GRANT SELECT ON users TO analyst", new DdlCompilationOptions(provider, provider)))
            };
            foreach (var item in compilations)
            {
                var captured = Describe(item.Execute());
                rows.Add(new { provider = provider.ToString(), compilerCase = item.Name, result = captured });
            }
        }
        const string go = "SELECT 1\nGO -- batch\nSELECT 2";
        var batch = SqlCoreFacade.ParseScript(go, SqlAgentToolType.MsSqlServer);
        return new
        {
            schemaVersion = 1,
            baselineContract = "U0 / F06 / T05 / T06",
            dialectCount = Dialects.Length,
            familyCount = 8,
            parseScriptGrantsExecution = false,
            rows,
            sqlServerGo = new { batch.BatchCount, statements = batch.Statements.Select(s =>
                new { s.BatchIndex, kind = s.Kind.ToString(), s.Text, start = s.Span.Start, length = s.Span.Length }).ToArray() }
        };
    }

    private static object Measure(CliOptions options)
    {
        var rows = new List<object>();
        foreach (var provider in Dialects)
        {
            var context = new SqlPlanValidationContext("sqlcore-v3-u0-benchmark");
            var policy = new SqlExecutionPlanPolicy();
            var workload = new List<(string Name, int SourceLength, Func<object> Run)>();
            foreach (var (name, sql) in Families)
                workload.Add(("parse-" + name, sql.Length, () => SqlCoreFacade.ParseScript(sql, provider)));

            // Fixed scan corpus, NOT a fabricated count of internal lexer passes.
            // Actual token/AST traversal counts require separately instrumented stage evidence.
            foreach (var count in new[] { 64, 256 })
            {
                var sql = string.Concat(Enumerable.Repeat("SELECT 1; ", count));
                workload.Add(("token-scan-script-" + count, sql.Length,
                    () => SqlCoreFacade.ParseScript(sql, provider)));
            }
            workload.Add(("compile-query", 8, () =>
                SqlCoreFacade.TryCompileQuery("SELECT 1", provider, provider, context, policy)));
            workload.Add(("compile-dml", 30, () =>
                SqlCoreFacade.TryCompileDml("DELETE FROM users WHERE id=1", provider, provider, context)));
            workload.Add(("compile-ddl", 30, () =>
                SqlCoreFacade.TryCompileDdl("CREATE TABLE users(id INTEGER)", new DdlCompilationOptions(provider, provider))));

            foreach (var (name, sourceLength, run) in workload)
            {
                var expected = Serialize(Describe(run()));
                for (var i = 0; i < options.Warmup; i++)
                    if (Serialize(Describe(run())) != expected)
                        throw new InvalidOperationException("Non-deterministic warmup: " + provider + "/" + name);
                var elapsed = new long[options.Iterations];
                var bytes = new long[options.Iterations];
                for (var i = 0; i < options.Iterations; i++)
                {
                    var beforeBytes = GC.GetAllocatedBytesForCurrentThread();
                    var started = Stopwatch.GetTimestamp();
                    var result = run();
                    var ticks = Stopwatch.GetTimestamp() - started;
                    var afterBytes = GC.GetAllocatedBytesForCurrentThread();
                    elapsed[i] = ticks;
                    bytes[i] = afterBytes - beforeBytes;
                    // Signature hashing is deliberately outside the measured region.
                    if (Serialize(Describe(result)) != expected)
                        throw new InvalidOperationException("Parity drift: " + provider + "/" + name);
                }
                Array.Sort(elapsed);
                Array.Sort(bytes);
                rows.Add(new
                {
                    provider = provider.ToString(),
                    @case = name,
                    sourceLength,
                    iterations = options.Iterations,
                    p50Microseconds = Round(elapsed[(elapsed.Length - 1) / 2] * 1_000_000.0 / Stopwatch.Frequency),
                    p95Microseconds = Round(elapsed[Math.Max(0, (int)Math.Ceiling(elapsed.Length * .95) - 1)] * 1_000_000.0 / Stopwatch.Frequency),
                    p50AllocatedBytes = bytes[(bytes.Length - 1) / 2],
                    meanAllocatedBytesPerOperation = Round(bytes.Average()),
                    outputDigest = Digest(expected)
                });
            }
        }
        return new
        {
            schemaVersion = 1,
            corpusVersion = "sqlcore-v3-u0-2026-11-16",
            benchmarkType = "observation; not an absolute performance gate",
            framework = RuntimeInformation.FrameworkDescription,
            os = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            options.Warmup, options.Iterations,
            measuredTokenScans = (int?)null,
            measuredAstWalks = (int?)null,
            results = rows
        };
    }

    private static object Describe(object value) => value switch
    {
        SqlScriptParseResult parse => new
        {
            parse.BatchCount,
            Statements = parse.Statements.Select(s => new
            {
                s.Kind, s.Status, s.BatchIndex, s.Span.Start, s.Span.Length,
                Code = s.Diagnostic?.Code, Stage = s.Diagnostic?.Stage
            }).ToArray()
        },
        SqlCoreTryResult<CompiledSqlCommand> result => new
        {
            result.Success, result.ErrorCode,
            Sql = result.Value?.Sql,
            Kind = result.Value?.Kind,
            Fingerprint = result.Value?.PlanFingerprint,
            Parameters = result.Value?.Parameters.Select(p => new
            {
                p.Name, Type = p.Value?.GetType().FullName, p.Value
            }).ToArray(),
            Diagnostics = result.TypedDiagnostics.Select(d => new
            {
                d.Code, d.Stage, d.Category, Start = d.Span?.Start, Length = d.Span?.Length
            }).ToArray()
        },
        SqlCoreTryResult<CompiledDdlPlan> result => new
        {
            result.Success, result.ErrorCode,
            Sql = result.Value?.Command.Sql,
            Fingerprint = result.Value?.PlanFingerprint,
            Diagnostics = result.TypedDiagnostics.Select(d => new
            {
                d.Code, d.Stage, d.Category, Start = d.Span?.Start, Length = d.Span?.Length
            }).ToArray()
        },
        _ => throw new ArgumentException("Unexpected baseline capture type: " + value.GetType().FullName)
    };

    private static string Serialize(object value) => JsonSerializer.Serialize(value);
    private static double Round(double value) => Math.Round(value, 3);
    private static string Digest(string input) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    private sealed record CliOptions(int Iterations, int Warmup, string? Output)
    {
        public static CliOptions Parse(string[] args)
        {
            int iterations = 80, warmup = 20;
            string? output = null;
            for (var i = 0; i < args.Length; i++)
            {
                if (args[i] == "--output" && i + 1 < args.Length) output = args[++i];
                else if (args[i] == "--iterations" && i + 1 < args.Length &&
                         int.TryParse(args[++i], out var number)) iterations = number;
                else if (args[i] == "--warmup" && i + 1 < args.Length &&
                         int.TryParse(args[++i], out var number)) warmup = number;
                else throw new ArgumentException("Usage: --v3-abi|--v3-inventory|--v3-perf [--output path] [--iterations N] [--warmup N]");
            }
            if (iterations is < 1 or > 100_000 || warmup is < 0 or > 100_000)
                throw new ArgumentOutOfRangeException(nameof(args));
            return new CliOptions(iterations, warmup, output);
        }
    }
}
