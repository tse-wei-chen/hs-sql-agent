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
            "nested-scalar-subquery" =>
                "SELECT " + string.Concat(Enumerable.Repeat("(SELECT ", depth)) +
                "1" + new string(')', depth),
            "prefix-not" =>
                "SELECT id FROM users WHERE " +
                string.Concat(Enumerable.Repeat("NOT ", depth)) + "id = 7",
            "left-deep-and" =>
                "SELECT id FROM users WHERE " +
                string.Join(" AND ", Enumerable.Repeat("id = 7", depth)),
            _ => throw new ArgumentException("Unknown probe shape: " + shape)
        };

        string? report = null;
        var exitCode = 0;
        void CompileOnThisThread()
        {
            var stopwatch = Stopwatch.StartNew();
            var beforeBytes = GC.GetAllocatedBytesForCurrentThread();
            try
            {
                var compiled = SqlCoreFacade.CompileQuery(
                    query, dialect, dialect,
                    new SqlPlanValidationContext("depth-probe-v1"),
                    new SqlExecutionPlanPolicy());
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
                    diagnosticCode,
                    error = error.GetType().Name + ": " +
                        error.Message[..Math.Min(250, error.Message.Length)]
                });
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
                || shapes.Any(value => !Shapes.Contains(value, StringComparer.Ordinal)))
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
            var parsedResult = reported.Length > 0
                ? JsonDocument.Parse(reported)
                : null;
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
