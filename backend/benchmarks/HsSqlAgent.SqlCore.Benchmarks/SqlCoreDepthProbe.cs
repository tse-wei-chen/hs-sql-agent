using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.SqlParsing;
using HsSqlAgent.SqlCore.Enums;

/// <summary>
/// Isolated process per depth/shape. A CLR StackOverflowException cannot be
/// safely caught in-process; this harness records crashes and timeouts without
/// terminating the parent benchmark/CI job.
/// </summary>
internal static class SqlCoreDepthProbe
{
    private static readonly string[] Shapes =
        ["redundant-grouping", "nested-scalar-subquery", "prefix-not", "left-deep-and"];
    // Opt-in shape: alternates SELECT/WHERE/EXISTS Query boundaries rather
    // than only repeating an inert scalar projection. Keep the historic
    // default 72-case CI depth floor intact; use --shapes to measure this
    // additional resource class in an isolated subprocess.
    private static readonly string[] OptionalShapes =
        ["mixed-query-graph", "nested-derived-source", "nested-cte", "nested-cte-typed-schema",
         "nested-set", "nested-set-typed-schema", "mixed-cte-derived",
         "mixed-cte-derived-typed-schema", "nested-lateral-derived",
         "nested-lateral-derived-typed-schema", "nested-joined-lateral",
         "nested-joined-lateral-typed-schema", "nested-correlated-exists",
         "nested-correlated-exists-typed-schema",
         "nested-correlated-in", "nested-correlated-in-typed-schema",
         "nested-scalar-subquery-typed-schema", "nested-case-scalar",
         "nested-case-scalar-typed-schema",
         "nested-prefixed-joined-lateral", "nested-prefixed-joined-lateral-typed-schema",
         "nested-double-joined-lateral", "nested-double-joined-lateral-typed-schema",
         "nested-triple-joined-lateral", "nested-triple-joined-lateral-typed-schema",
         "two-deep-sibling-lateral", "two-deep-sibling-lateral-typed-schema",
         "lateral-exists-comb-siblings", "lateral-exists-comb-siblings-typed-schema",
         "facts-joined-lateral", "facts-joined-lateral-typed-schema"];

    private sealed record StageSample(string Stage, long ElapsedMicroseconds,
        long AllocatedBytes, long WorkUnits, bool Success);

    private sealed class Observer<T>(Action<T> onNext) : IObserver<T>
    {
        public void OnNext(T item) => onNext(item);
        public void OnCompleted() { }
        public void OnError(Exception _) { }
    }

    private static string NestDerived(int depth)
    {
        var sql = "SELECT id FROM users";
        for (var level = 0; level < depth; level++)
            sql = "SELECT d.id FROM (" + sql + ") d";
        return sql;
    }

    private static string NestLateralDerived(int depth)
    {
        var sql = "SELECT id FROM users";
        for (var level = 0; level < depth; level++)
            sql = "SELECT l.id FROM LATERAL (" + sql + ") AS l";
        return sql;
    }

    // JOINed LATERAL sees the immediately preceding users u at every
    // level; the innermost query resolves u.id from its parent scope.
    private static string NestJoinedLateral(int depth)
    {
        var sql = "SELECT u.id AS id";
        for (var level = 0; level < depth; level++)
            sql = "SELECT l.id FROM users u JOIN LATERAL (" + sql + ") l ON l.id = u.id";
        return sql;
    }

    // The innermost SELECT has no FROM, so its u.id is genuinely
    // correlated with the outer scope, not a local same-named table.
    // A preceding regular JOIN introduces v, which the second JOIN
    // LATERAL must see at each level, without losing outer correlation.
    private static string NestPrefixedJoinedLateral(int depth)
    {
        var sql = "SELECT v.id AS id";
        for (var level = 0; level < depth; level++)
            sql = "SELECT l.id FROM users u JOIN users v ON v.id = u.id "
                + "JOIN LATERAL (" + sql + ") l ON l.id = v.id";
        return sql;
    }

    // Two sequential correlated LATERALs within every SELECT: the second
    // body must see the first derived source while resolving through
    // nested Query ancestors on a constrained CLR thread stack.
    private static string NestDoubleJoinedLateral(int depth)
    {
        var sql = "SELECT a.id AS id";
        for (var level = 0; level < depth; level++)
            sql = "SELECT b.id FROM users u "
                + "JOIN LATERAL (SELECT u.id AS id) a ON a.id = u.id "
                + "JOIN LATERAL (" + sql + ") b ON b.id = a.id";
        return sql;
    }

    // An arbitrary-length INNER JOIN prefix is resolved in source order:
    // A sees users u; B sees A; the last LATERAL C sees B and owns the
    // recursive nested Query edge that must be suspended on the heap.
    private static string NestTripleJoinedLateral(int depth)
    {
        var sql = "SELECT b.id AS id";
        for (var level = 0; level < depth; level++)
            sql = "SELECT c.id FROM users u "
                + "JOIN LATERAL (SELECT u.id AS id) a ON TRUE "
                + "JOIN LATERAL (SELECT a.id AS id) b ON TRUE "
                + "JOIN LATERAL (" + sql + ") c ON TRUE";
        return sql;
    }

    // Both sibling LATERAL sources are independently deep. A's innermost
    // query resolves root u; B's innermost query resolves preceding alias a.
    // Neither is allowed to borrow the other sibling's future row shape.
    private static string TwoDeepSiblingLateral(int depth)
    {
        var left = "SELECT u.id AS id";
        var right = "SELECT a.id AS id";
        for (var level = 0; level < depth; level++)
        {
            left = "SELECT x.id AS id FROM LATERAL (" + left + ") x";
            right = "SELECT x.id AS id FROM LATERAL (" + right + ") x";
        }
        return "SELECT b.id FROM users u "
            + "JOIN LATERAL (" + left + ") a ON TRUE "
            + "JOIN LATERAL (" + right + ") b ON TRUE";
    }

    // At every level, the main Query branches into a lateral Source child
    // AND a correlated EXISTS Query under WHERE. Both independent sibling
    // LATERAL trees repeat this mixed Source/Expr graph.
    private static string LateralExistsCombSiblings(int depth)
    {
        static string Comb(string leaf, int count)
        {
            var sql = leaf;
            for (var level = 0; level < count; level++)
                sql = "SELECT x.id AS id FROM LATERAL (" + sql
                    + ") x WHERE EXISTS (SELECT x.id)";
            return sql;
        }

        var left = Comb("SELECT u.id AS id", depth);
        var right = Comb("SELECT a.id AS id", depth);
        return "SELECT b.id FROM users u "
            + "JOIN LATERAL (" + left + ") a ON TRUE "
            + "JOIN LATERAL (" + right + ") b ON TRUE";
    }

    private static string NestCorrelatedExists(int depth)
    {
        var sql = "SELECT u.id";
        for (var level = 0; level < depth; level++)
            sql = "SELECT u.id FROM users u WHERE EXISTS (" + sql + ")";
        return sql;
    }

    private static string NestCorrelatedIn(int depth)
    {
        var sql = "SELECT id FROM users";
        for (var level = 0; level < depth; level++)
            sql = "SELECT id FROM users WHERE id IN (" + sql + ")";
        return sql;
    }

    private static string NestCaseScalar(int depth)
    {
        var sql = "SELECT 1";
        for (var level = 0; level < depth; level++)
            sql = "SELECT CASE WHEN 1 = 1 THEN (" + sql + ") ELSE 0 END";
        return sql;
    }

    private static string NestCte(int depth)
    {
        var sql = "SELECT id FROM users";
        for (var level = 0; level < depth; level++)
            sql = "WITH c AS (" + sql + ") SELECT id FROM c";
        return sql;
    }

    private static string NestMixedCteDerived(int depth)
    {
        var sql = "SELECT id FROM users";
        for (var level = 0; level < depth; level++)
            sql = (level & 1) == 0
                ? "WITH c AS (" + sql + ") SELECT id FROM c"
                : "SELECT d.id FROM (" + sql + ") AS d";
        return sql;
    }

    private static string NestSet(int depth)
    {
        var sql = "SELECT id FROM users WHERE id = 1";
        for (var level = 2; level <= depth; level++)
            sql = "SELECT id FROM users WHERE id = " + level + " UNION (" + sql + ")";
        return sql;
    }

    public static bool TryRun(string[] args)
    {
        if (args.Length == 0) return false;
        if (args[0] == "--depth-probe-worker")
        {
            RunWorker(args);
            return true;
        }
        if (args[0] == "--depth-probe")
        {
            RunSweep(args);
            return true;
        }
        return false;
    }

    private static void RunWorker(string[] args)
    {
        if ((args.Length != 4 && args.Length != 6) ||
            !int.TryParse(args[2], out var depth) ||
            depth is < 1 or > 4_096 ||
            !Enum.TryParse<SqlAgentToolType>(args[3], out var dialect))
        {
            throw new ArgumentException(
                "Worker args: --depth-probe-worker <shape> <depth> <dialect> [--stack-bytes N]");
        }

        int? stackBytes = null;
        if (args.Length == 6)
        {
            if (args[4] != "--stack-bytes" ||
                !int.TryParse(args[5], out var parsedStack) ||
                parsedStack is < 262_144 or > 16_777_216)
                throw new ArgumentException("Managed worker stack must be 262144..16777216 bytes.");
            stackBytes = parsedStack;
        }

        var shape = args[1];
        var query = shape switch
        {
            "redundant-grouping" =>
                "SELECT " + new string('(', depth) + "1" + new string(')', depth),
            "nested-scalar-subquery" or "nested-scalar-subquery-typed-schema" =>
                "SELECT " + string.Concat(Enumerable.Repeat("(SELECT ", depth)) +
                "1" + new string(')', depth),
            "nested-case-scalar" or "nested-case-scalar-typed-schema" =>
                NestCaseScalar(depth),
            "prefix-not" =>
                "SELECT id FROM users WHERE " +
                string.Concat(Enumerable.Repeat("NOT ", depth)) + "id = 7",
            "left-deep-and" =>
                "SELECT id FROM users WHERE " +
                string.Join(" AND ", Enumerable.Repeat("id = 7", depth)),
            "mixed-query-graph" =>
                "SELECT u.id FROM users u WHERE EXISTS (" +
                string.Concat(Enumerable.Repeat("SELECT 1 WHERE EXISTS (", depth)) +
                "SELECT u.id" + new string(')', depth + 1),
            "nested-derived-source" => NestDerived(depth),
            "nested-lateral-derived" or "nested-lateral-derived-typed-schema" =>
                NestLateralDerived(depth),
            "nested-joined-lateral" or "nested-joined-lateral-typed-schema"
                or "facts-joined-lateral" or "facts-joined-lateral-typed-schema" =>
                NestJoinedLateral(depth),
            "nested-prefixed-joined-lateral" or "nested-prefixed-joined-lateral-typed-schema" =>
                NestPrefixedJoinedLateral(depth),
            "nested-double-joined-lateral" or "nested-double-joined-lateral-typed-schema" =>
                NestDoubleJoinedLateral(depth),
            "nested-triple-joined-lateral" or "nested-triple-joined-lateral-typed-schema" =>
                NestTripleJoinedLateral(depth),
            "two-deep-sibling-lateral" or "two-deep-sibling-lateral-typed-schema" =>
                TwoDeepSiblingLateral(depth),
            "lateral-exists-comb-siblings" or "lateral-exists-comb-siblings-typed-schema" =>
                LateralExistsCombSiblings(depth),
            "nested-correlated-exists" or "nested-correlated-exists-typed-schema" =>
                NestCorrelatedExists(depth),
            "nested-correlated-in" or "nested-correlated-in-typed-schema" =>
                NestCorrelatedIn(depth),
            "nested-cte" => NestCte(depth),
            "nested-cte-typed-schema" => NestCte(depth),
            "mixed-cte-derived" or "mixed-cte-derived-typed-schema" => NestMixedCteDerived(depth),
            "nested-set" or "nested-set-typed-schema" => NestSet(depth),
            _ => throw new ArgumentException("Unknown probe shape: " + shape)
        };

        string? report = null;
        var exitCode = 0;
        void CompileOnThisThread()
        {
            var stages = new List<StageSample>();
            var subscriptions = new List<IDisposable>();
            using var all = DiagnosticListener.AllListeners.Subscribe(
                new Observer<DiagnosticListener>(source =>
                {
                    if (source.Name != "HsSqlAgent.SqlCore.Compiler") return;
                    subscriptions.Add(source.Subscribe(
                        new Observer<KeyValuePair<string, object?>>(measurement =>
                        {
                            if (measurement.Key != "StageCompleted" || measurement.Value is null)
                                return;
                            var value = measurement.Value;
                            object? Prop(string property) =>
                                value.GetType().GetProperty(property)?.GetValue(value);
                            stages.Add(new StageSample(
                                (string?)Prop("Stage") ?? "Unknown",
                                Convert.ToInt64(Prop("ElapsedMicroseconds")),
                                Convert.ToInt64(Prop("AllocatedBytes")),
                                Convert.ToInt64(Prop("WorkUnits")),
                                Convert.ToBoolean(Prop("Success"))));
                        })));
                }));
            var stopwatch = Stopwatch.StartNew();
            var beforeBytes = GC.GetAllocatedBytesForCurrentThread();
            try
            {
                var context = new SqlPlanValidationContext("depth-probe-v1");
                if (shape is "nested-cte-typed-schema" or "nested-set-typed-schema"
                    or "mixed-cte-derived-typed-schema"
                    or "nested-lateral-derived-typed-schema"
                    or "nested-joined-lateral-typed-schema"
                    or "facts-joined-lateral-typed-schema"
                    or "nested-prefixed-joined-lateral-typed-schema"
                    or "nested-double-joined-lateral-typed-schema"
                    or "nested-triple-joined-lateral-typed-schema"
                    or "two-deep-sibling-lateral-typed-schema"
                    or "lateral-exists-comb-siblings-typed-schema"
                    or "nested-correlated-exists-typed-schema"
                    or "nested-correlated-in-typed-schema"
                    or "nested-scalar-subquery-typed-schema"
                    or "nested-case-scalar-typed-schema")
                {
                    var uppercase = dialect is SqlAgentToolType.Oracle or SqlAgentToolType.Firebird;
                    var table = uppercase ? "USERS" : "users";
                    var column = uppercase ? "ID" : "id";
                    context = context.WithSchemaSnapshot(new SqlSchemaSnapshot(
                        dialect, "depth-typed-snapshot-v1",
                        new[] { SqlSchemaTable.WithTypes(
                            table, new[] { new SqlSchemaColumn(
                                column, SqlSchemaValueKind.Numeric) }) }));
                }
                var isFactsShape = shape.StartsWith("facts-", StringComparison.Ordinal);
                var compiled = isFactsShape
                    ? SqlCoreFacade.CompileQueryWithFacts(
                        query, dialect, dialect, context, new SqlExecutionPlanPolicy()).Command
                    : SqlCoreFacade.CompileQuery(
                        query, dialect, dialect, context, new SqlExecutionPlanPolicy());
                stopwatch.Stop();
                report = JsonSerializer.Serialize(new
                {
                    success = true,
                    shape,
                    dialect = dialect.ToString(),
                    depth,
                    stackBytes,
                    sqlChars = query.Length,
                    elapsedMicroseconds = Math.Round(stopwatch.Elapsed.TotalMicroseconds, 2),
                    allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - beforeBytes,
                    renderedChars = compiled.Sql.Length,
                    stageMetrics = stages,
                    diagnosticCode = (string?)null,
                    error = (string?)null
                });
            }
            catch (Exception error)
            {
                stopwatch.Stop();
                exitCode = 2;
                var diagnosticCode = error switch
                {
                    SqlParseException parseError => parseError.Diagnostic?.Code,
                    SqlCompilationException compileError => compileError.Diagnostic?.Code,
                    _ => null
                };
                report = JsonSerializer.Serialize(new
                {
                    success = false,
                    shape,
                    dialect = dialect.ToString(),
                    depth,
                    stackBytes,
                    sqlChars = query.Length,
                    elapsedMicroseconds = Math.Round(stopwatch.Elapsed.TotalMicroseconds, 2),
                    allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - beforeBytes,
                    renderedChars = 0,
                    stageMetrics = stages,
                    diagnosticCode,
                    error = error.GetType().Name + ": " +
                        error.Message[..Math.Min(250, error.Message.Length)]
                });
            }
            finally
            {
                foreach (var subscription in subscriptions) subscription.Dispose();
            }
        }

        if (stackBytes is { } size)
        {
            // The entire *process* is already isolated by RunSweep. An actual
            // StackOverflowException kills only this child, not the CI runner.
            var thread = new Thread(CompileOnThisThread, size);
            thread.Start();
            thread.Join();
        }
        else CompileOnThisThread();

        Console.WriteLine(report);
        Environment.ExitCode = exitCode;
    }

    private static void RunSweep(string[] args)
    {
        var smoke = args.Contains("--smoke", StringComparer.Ordinal);
        var depths = smoke ? new[] { 8, 32 } : new[] { 8, 32, 128, 512, 2_048 };
        var dialects = smoke
            ? new[] { SqlAgentToolType.Postgres }
            : Enum.GetValues<SqlAgentToolType>();

        // Explicit CLI slices let CI collect high-depth evidence while the
        // gate remains a fast 8/32-depth smoke. Unknown shapes and dialects
        // must fail immediately rather than silently measuring the wrong set.
        var requestedDepths = ReadOption(args, "--depths");
        if (requestedDepths is not null)
        {
            depths = requestedDepths.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(value => int.Parse(value, System.Globalization.CultureInfo.InvariantCulture))
                .ToArray();
            if (depths.Length == 0 || depths.Any(value => value is < 1 or > 4_096))
                throw new ArgumentOutOfRangeException(nameof(args), "Depths must be 1..4096.");
        }
        var requestedDialects = ReadOption(args, "--dialects");
        if (requestedDialects is not null)
        {
            dialects = requestedDialects.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(value => Enum.Parse<SqlAgentToolType>(value, ignoreCase: true))
                .ToArray();
            if (dialects.Length == 0 || dialects.Distinct().Count() != dialects.Length)
                throw new ArgumentException("Dialects must be non-empty and unique.");
        }
        var shapes = Shapes;
        var requestedShapes = ReadOption(args, "--shapes");
        if (requestedShapes is not null)
        {
            shapes = requestedShapes.Split(',', StringSplitOptions.RemoveEmptyEntries);
            if (shapes.Length == 0 || shapes.Distinct().Count() != shapes.Length
                || shapes.Any(value => !Shapes.Contains(value, StringComparer.Ordinal)
                    && !OptionalShapes.Contains(value, StringComparer.Ordinal)))
                throw new ArgumentException("Unknown or repeated depth-probe shape.");
        }
        var outputIndex = Array.IndexOf(args, "--output");
        var outputPath = outputIndex >= 0 && outputIndex + 1 < args.Length
            ? args[outputIndex + 1]
            : null;

        var stackOption = ReadOption(args, "--stack-bytes");
        int? stackBytes = null;
        if (stackOption is not null)
        {
            if (!int.TryParse(stackOption, out var stackValue) ||
                stackValue is < 262_144 or > 16_777_216)
                throw new ArgumentOutOfRangeException(nameof(args),
                    "Managed probe worker stack must be 262144..16777216 bytes.");
            stackBytes = stackValue;
        }

        var observations = new List<object>();
        var failures = 0;
        var resourceRejections = 0;
        var crashesOrTimeouts = 0;
        foreach (var dialect in dialects)
        foreach (var shape in shapes)
        foreach (var depth in depths)
        {
            using var process = new Process { StartInfo = NewWorker(shape, depth, dialect, stackBytes) };
            var wall = Stopwatch.StartNew();
            process.Start();

            // Always drain both pipes asynchronously, including after a fatal
            // StackOverflowException which may write a long stack trace.
            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            var completed = process.WaitForExit(15_000);
            if (!completed) process.Kill(entireProcessTree: true);
            process.WaitForExit();
            Task.WaitAll(standardOutput, standardError);
            wall.Stop();

            var succeeded = completed && process.ExitCode == 0;
            if (!succeeded) failures++;
            var reported = standardOutput.Result.Trim();
            // A crashed worker may emit a partial JSON payload. Treat it as
            // an observed crash, not an exception that kills the entire sweep.
            JsonDocument? parsedResult = null;
            if (reported.Length > 0)
            {
                try { parsedResult = JsonDocument.Parse(reported); }
                catch (JsonException) { /* Preserve raw output below. */ }
            }
            if (!completed || parsedResult is null) crashesOrTimeouts++;
            // A resource diagnostic is a *successful fail-closed safety
            // decision*, not evidence that SQL execution succeeded.
            var resourceRejected =
                completed && process.ExitCode == 2
                && parsedResult is not null
                && parsedResult.RootElement.TryGetProperty("diagnosticCode", out var code)
                && code.ValueKind == JsonValueKind.String
                && code.GetString() == "SQL_COMPILER_STACK_RESERVE";
            if (resourceRejected) resourceRejections++;
            using (parsedResult)
            {
            observations.Add(new
            {
                dialect = dialect.ToString(),
                shape,
                depth,
                stackBytes,
                completed,
                processExitCode = process.ExitCode,
                wallMilliseconds = Math.Round(wall.Elapsed.TotalMilliseconds, 2),
                worker = parsedResult?.RootElement.Clone(),
                malformedWorkerOutput = parsedResult is null && reported.Length > 0
                    ? reported[..Math.Min(300, reported.Length)]
                    : null,
                processError = succeeded ? null : standardError.Result[..Math.Min(300, standardError.Result.Length)]
            });
            }
        }

        var report = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            mode = smoke ? "ci-smoke" : "isolated-depth-sweep",
            runtimes = dialects.Length,
            samples = observations.Count,
            failures,
            resourceRejections,
            unexpectedFailures = failures - resourceRejections,
            crashesOrTimeouts,
            stackOverflowOrTimeoutDoesNotCrashParent = true,
            observations
        }, new JsonSerializerOptions { WriteIndented = true });

        if (outputPath is null)
            Console.WriteLine(report);
        else
        {
            var full = Path.GetFullPath(outputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, report);
        }

        // Smoke is a regression gate. Full sweeps intentionally record failure
        // thresholds without failing (or crashing) the measurement workflow.
        if (smoke && failures > 0)
            throw new InvalidOperationException(
                $"SQL depth smoke probe: {failures} of {observations.Count} child processes failed.");
        if (stackBytes is not null && (failures != resourceRejections || crashesOrTimeouts > 0))
            throw new InvalidOperationException(
                $"Constrained-stack safety regression: {failures - resourceRejections} " +
                $"unexpected failures and {crashesOrTimeouts} crashes/timeouts. " +
                "Only successful compilation or typed SQL_COMPILER_STACK_RESERVE rejection is allowed.");
    }

    private static string? ReadOption(string[] args, string flag)
    {
        var index = Array.IndexOf(args, flag);
        if (index < 0) return null;
        if (index + 1 >= args.Length)
            throw new ArgumentException("Missing value for " + flag);
        return args[index + 1];
    }

    private static ProcessStartInfo NewWorker(string shape, int depth, SqlAgentToolType dialect, int? stackBytes)
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot locate current benchmark executable.");
        var command = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(executable),
                "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            command.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        }
        command.ArgumentList.Add("--depth-probe-worker");
        command.ArgumentList.Add(shape);
        command.ArgumentList.Add(depth.ToString(System.Globalization.CultureInfo.InvariantCulture));
        command.ArgumentList.Add(dialect.ToString());
        if (stackBytes is { } amount)
        {
            command.ArgumentList.Add("--stack-bytes");
            command.ArgumentList.Add(amount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        return command;
    }
}
