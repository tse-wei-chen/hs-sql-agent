using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Models;
using HsSqlAgent.SqlCore.SqlParsing;

/// <summary>
/// Opt-in, crash-isolated regression gate for the 256 KiB ForProvider stack
/// and the DML compilation path. Never execute SQL against a database.
/// Fatal StackOverflowException terminates only a child process, not CI.
/// </summary>
internal static class SqlCoreCapabilityStackProbe
{
    private const int StackBytes = 262_144;
    private static readonly string[] Scenarios =
        ["matrix-none", "matrix-old", "matrix-current",
         "dml-selective", "dml-unbounded"];

    private sealed record MatrixSnapshot(int Count, string Digest);

    public static bool TryRun(string[] args)
    {
        if (args.Length == 0) return false;
        if (args[0] == "--capability-stack-worker")
        {
            RunWorker(args);
            return true;
        }
        if (args[0] == "--capability-stack-probe")
        {
            RunSweep(args);
            return true;
        }
        return false;
    }

    private static SqlProviderCapabilityProfile? Profile(
        SqlAgentToolType provider, string scenario)
    {
        if (scenario == "matrix-none") return null;
        var version = scenario switch
        {
            "matrix-old" => provider switch
            {
                SqlAgentToolType.Postgres => new Version(9, 2),
                SqlAgentToolType.MySQL => new Version(5, 7),
                SqlAgentToolType.Sqlite => new Version(3, 23),
                SqlAgentToolType.MsSqlServer => new Version(13, 0),
                SqlAgentToolType.Oracle => new Version(11, 2),
                SqlAgentToolType.Firebird => new Version(3, 0),
                _ => throw new ArgumentOutOfRangeException(nameof(provider))
            },
            "matrix-current" => provider switch
            {
                SqlAgentToolType.Postgres => new Version(16, 0),
                SqlAgentToolType.MySQL => new Version(8, 4),
                SqlAgentToolType.Sqlite => new Version(3, 45),
                SqlAgentToolType.MsSqlServer => new Version(17, 0),
                SqlAgentToolType.Oracle => new Version(26, 0),
                SqlAgentToolType.Firebird => new Version(5, 0),
                _ => throw new ArgumentOutOfRangeException(nameof(provider))
            },
            _ => throw new ArgumentException("Unknown matrix profile scenario.")
        };
        return new SqlProviderCapabilityProfile(provider, version);
    }

    private static MatrixSnapshot Snapshot(
        SqlAgentToolType provider, SqlProviderCapabilityProfile? profile)
    {
        var matrix = SqlCapabilityMatrix.ForProvider(provider, profile);
        var caps = matrix.Capabilities.ToArray();
        if (caps.Length < 50 ||
            caps.Select(cap => cap.Id).Distinct(StringComparer.Ordinal).Count() != caps.Length ||
            caps.Any(cap => string.IsNullOrWhiteSpace(cap.Id)
                || string.IsNullOrWhiteSpace(cap.Category)
                || string.IsNullOrWhiteSpace(cap.Detail)))
        {
            throw new InvalidOperationException(
                "Capability matrix entries became empty, duplicated or incomplete.");
        }

        // All capabilities, in declared order: ID, category, status, exact
        // detail text. Compare across native stack sizes without normalizing.
        var canonical = JsonSerializer.Serialize(caps.Select(cap => new
        {
            cap.Id, cap.Category,
            Status = cap.Status.ToString(),
            cap.Detail
        }));
        return new MatrixSnapshot(caps.Length,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))));
    }

    private static string CompileDeepDml(SqlAgentToolType provider, bool selective)
    {
        const int depth = 2048;
        var predicate = string.Join(" AND ", Enumerable.Repeat("1 = 1", depth));
        if (selective) predicate += " AND id = 7";

        try
        {
            var command = SqlCoreFacade.CompileDml(
                "DELETE FROM users WHERE " + predicate, provider, provider,
                new SqlPlanValidationContext("capability-stack-probe-v1"));
            if (!selective || string.IsNullOrWhiteSpace(command.Sql))
                throw new InvalidOperationException(
                    "Unbounded DELETE was authorized or the bounded result is empty.");
            return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(command.Sql)));
        }
        catch (UnauthorizedAccessException error) when (!selective)
        {
            var diagnostic = error.Data["HsSqlAgent.SqlCore.Diagnostic"] as SqlDiagnostic;
            if (diagnostic?.Code != "SQL_POLICY_DELETE_UNBOUNDED_PREDICATE")
                throw new InvalidOperationException(
                    "Unbounded DELETE lost its Policy diagnostic.", error);
            return diagnostic.Code;
        }
    }

    private static void RunWorker(string[] args)
    {
        if (args.Length != 3 ||
            !Enum.TryParse<SqlAgentToolType>(args[1], out var provider) ||
            !Enum.IsDefined(provider) ||
            !Scenarios.Contains(args[2], StringComparer.Ordinal))
            throw new ArgumentException(
                "Worker: --capability-stack-worker <provider> <scenario>");

        var scenario = args[2];
        var stopwatch = Stopwatch.StartNew();
        var reference = scenario.StartsWith("matrix-", StringComparison.Ordinal)
            ? Snapshot(provider, Profile(provider, scenario))
            : null;

        string? digest = null;
        int? capabilityCount = null;
        Exception? captured = null;
        var worker = new Thread(() =>
        {
            try
            {
                if (reference is not null)
                {
                    // Repeat inside a 256 KiB CLR stack to exercise JIT and
                    // identical matrix materialization under stack pressure.
                    for (var i = 0; i < 3; i++)
                    {
                        var smallStack = Snapshot(provider, Profile(provider, scenario));
                        if (smallStack != reference)
                            throw new InvalidOperationException(
                                "Matrix ordering, status or detail changed on constrained stack.");
                        digest = smallStack.Digest;
                        capabilityCount = smallStack.Count;
                    }
                }
                else
                {
                    digest = CompileDeepDml(provider, scenario == "dml-selective");
                }
            }
            catch (Exception error)
            {
                captured = error;
            }
        }, StackBytes);
        worker.Start();
        worker.Join();
        stopwatch.Stop();

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            provider = provider.ToString(),
            scenario,
            stackBytes = StackBytes,
            success = captured is null,
            capabilityCount,
            digest,
            elapsedMilliseconds = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 2),
            error = captured is null ? null :
                captured.GetType().Name + ": " + captured.Message
        }));
        if (captured is not null) Environment.ExitCode = 2;
    }

    private static void RunSweep(string[] args)
    {
        if (args.Length != 1 && (args.Length != 3 || args[1] != "--output"))
            throw new ArgumentException(
                "Usage: --capability-stack-probe [--output <file>]");

        var records = new List<object>();
        var failures = 0;
        var crashesOrTimeouts = 0;
        foreach (var provider in Enum.GetValues<SqlAgentToolType>())
        foreach (var scenario in Scenarios)
        {
            using var process = new Process
            {
                StartInfo = NewWorker(provider, scenario)
            };
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            var completed = process.WaitForExit(20_000);
            if (!completed) process.Kill(entireProcessTree: true);
            process.WaitForExit();
            Task.WaitAll(stdout, stderr);
            JsonDocument? doc = null;
            try
            {
                doc = JsonDocument.Parse(stdout.Result);
            }
            catch (JsonException)
            {
                // Fatal StackOverflowException can exit with no JSON.
            }

            var passed = completed && process.ExitCode == 0 && doc is not null &&
                doc.RootElement.TryGetProperty("success", out var success) &&
                success.GetBoolean();
            if (!passed) failures++;
            if (!completed || doc is null) crashesOrTimeouts++;
            using (doc)
            {
                records.Add(new
                {
                    provider = provider.ToString(), scenario,
                    processExitCode = process.ExitCode,
                    completed,
                    success = passed,
                    worker = doc?.RootElement.Clone(),
                    stderr = passed ? null :
                        stderr.Result[..Math.Min(400, stderr.Result.Length)]
                });
            }
        }

        var output = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            stackBytes = StackBytes,
            samples = records.Count,
            failures,
            crashesOrTimeouts,
            records
        }, new JsonSerializerOptions { WriteIndented = true });
        if (args.Length == 3)
        {
            var path = Path.GetFullPath(args[2]);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, output);
        }
        else
            Console.WriteLine(output);

        Console.WriteLine(
            $"Capability 256 KiB stack gate: {records.Count} cases, " +
            $"{failures} failures, {crashesOrTimeouts} crashes/timeouts.");
        if (failures != 0 || records.Count != Enum.GetValues<SqlAgentToolType>().Length *
            Scenarios.Length)
            throw new InvalidOperationException(
                "Constrained-stack capability or DML proof regression; inspect child report.");
    }

    private static ProcessStartInfo NewWorker(
        SqlAgentToolType provider, string scenario)
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Missing executable path.");
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(executable),
                "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--capability-stack-worker");
        start.ArgumentList.Add(provider.ToString());
        start.ArgumentList.Add(scenario);
        return start;
    }
}
