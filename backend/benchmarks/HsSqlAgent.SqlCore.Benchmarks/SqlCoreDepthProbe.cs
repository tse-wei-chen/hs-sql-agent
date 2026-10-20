using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Pipeline;
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
        if (args.Length != 4 || !int.TryParse(args[2], out var depth) ||
            depth is < 1 or > 4_096 ||
            !Enum.TryParse<SqlAgentToolType>(args[3], out var dialect))
        {
            throw new ArgumentException("Worker args: --depth-probe-worker <shape> <depth> <dialect>");
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

        var stopwatch = Stopwatch.StartNew();
        var beforeBytes = GC.GetAllocatedBytesForCurrentThread();
        try
        {
            var command = SqlCoreFacade.CompileQuery(
                query, dialect, dialect,
                new SqlPlanValidationContext("depth-probe-v1"),
                new SqlExecutionPlanPolicy());
            stopwatch.Stop();
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                success = true,
                shape,
                dialect = dialect.ToString(),
                depth,
                sqlChars = query.Length,
                elapsedMicroseconds = Math.Round(stopwatch.Elapsed.TotalMicroseconds, 2),
                allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - beforeBytes,
                renderedChars = command.Sql.Length,
                error = (string?)null
            }));
        }
        catch (Exception error)
        {
            stopwatch.Stop();
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                success = false,
                shape,
                dialect = dialect.ToString(),
                depth,
                sqlChars = query.Length,
                elapsedMicroseconds = Math.Round(stopwatch.Elapsed.TotalMicroseconds, 2),
                allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - beforeBytes,
                renderedChars = 0,
                error = error.GetType().Name + ": " + error.Message[..Math.Min(250, error.Message.Length)]
            }));
            Environment.ExitCode = 2;
        }
    }

    private static void RunSweep(string[] args)
    {
        var smoke = args.Contains("--smoke", StringComparer.Ordinal);
        var depths = smoke ? new[] { 8, 32 } : new[] { 8, 32, 128, 512, 2_048 };
        var dialects = smoke
            ? new[] { SqlAgentToolType.Postgres }
            : Enum.GetValues<SqlAgentToolType>();
        var outputIndex = Array.IndexOf(args, "--output");
        var outputPath = outputIndex >= 0 && outputIndex + 1 < args.Length
            ? args[outputIndex + 1]
            : null;

        var observations = new List<object>();
        var failures = 0;
        foreach (var dialect in dialects)
        foreach (var shape in Shapes)
        foreach (var depth in depths)
        {
            using var process = new Process { StartInfo = NewWorker(shape, depth, dialect) };
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
            observations.Add(new
            {
                dialect = dialect.ToString(),
                shape,
                depth,
                completed,
                processExitCode = process.ExitCode,
                wallMilliseconds = Math.Round(wall.Elapsed.TotalMilliseconds, 2),
                worker = reported.Length > 0 ? reported : null,
                processError = succeeded ? null : standardError.Result[..Math.Min(300, standardError.Result.Length)]
            });
        }

        var report = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            mode = smoke ? "ci-smoke" : "isolated-depth-sweep",
            runtimes = dialects.Length,
            samples = observations.Count,
            failures,
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
    }

    private static ProcessStartInfo NewWorker(string shape, int depth, SqlAgentToolType dialect)
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
        return command;
    }
}
