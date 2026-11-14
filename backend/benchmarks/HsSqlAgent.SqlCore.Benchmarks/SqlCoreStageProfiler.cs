using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;

// Optional G0 R0 observational profiler. The shared Program.cs benchmark
// owns its SQL corpus and compilations; this only observes existing Core
// StageCompleted events, never instruments or changes production compiler code.
internal static class SqlCoreStageProfiler
{
    private static readonly string[] RequiredStages =
        ["Parsing", "Binding", "Normalization", "Validation", "Policy", "Rendering"];

    public static void Run(BenchmarkCase[] cases, BenchmarkOptions options)
    {
        using var observer = new CompilerStageObserver();
        var rows = new List<object>();
        foreach (var provider in Enum.GetValues<SqlAgentToolType>())
        {
            var context = new SqlPlanValidationContext("sqlcore-benchmark-v1");
            foreach (var benchmarkCase in cases)
            {
                var expected = benchmarkCase.Compile(provider, context);
                for (var i = 0; i < options.Warmup; i++)
                    BenchmarkCase.AssertEquivalent(
                        expected, benchmarkCase.Compile(provider, context), provider, benchmarkCase.Name);

                var events = new List<StageSample>();
                for (var i = 0; i < options.Iterations; i++)
                {
                    observer.Begin();
                    try
                    {
                        var actual = benchmarkCase.Compile(provider, context);
                        BenchmarkCase.AssertEquivalent(expected, actual, provider, benchmarkCase.Name);
                    }
                    finally
                    {
                        events.AddRange(observer.End());
                    }
                }

                var groups = events.GroupBy(stage => stage.Name, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
                foreach (var stage in RequiredStages)
                {
                    if (!groups.TryGetValue(stage, out var samples) ||
                        samples.Length != options.Iterations || samples.Any(sample => !sample.Success))
                        throw new InvalidOperationException(
                            $"Incomplete or failed compiler stage '{stage}' at {provider}/{benchmarkCase.Name}.");
                }
                if (groups.Values.SelectMany(group => group).Any(sample => !sample.Success))
                    throw new InvalidOperationException(
                        $"Compiler telemetry reported a failure at {provider}/{benchmarkCase.Name}.");

                var stageRows = groups.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => new
                    {
                        Name = pair.Key,
                        Samples = pair.Value.Length,
                        MedianMicroseconds = Median(pair.Value.Select(sample => sample.ElapsedMicroseconds)),
                        MedianAllocatedBytes = Median(pair.Value.Select(sample => sample.AllocatedBytes)),
                        MedianWorkUnits = Median(pair.Value.Select(sample => sample.WorkUnits)),
                        AllSucceeded = pair.Value.All(sample => sample.Success)
                    }).ToArray();

                rows.Add(new
                {
                    Provider = provider.ToString(),
                    Case = benchmarkCase.Name,
                    OutputDigest = BenchmarkCase.OutputDigest(expected),
                    Stages = stageRows
                });
            }
        }
        var report = new
        {
            schemaVersion = 1,
            startedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            framework = RuntimeInformation.FrameworkDescription,
            os = RuntimeInformation.OSDescription,
            processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            iterations = options.Iterations,
            warmup = options.Warmup,
            results = rows
        };
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        if (options.Output is { } path)
        {
            var output = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.WriteAllText(output, json);
        }
        else
            Console.WriteLine(json);
    }

    private static double Median(IEnumerable<long> values)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0) throw new InvalidOperationException("No compiler-stage samples.");
        var middle = sorted.Length / 2;
        return (sorted.Length & 1) != 0 ? sorted[middle]
            : (sorted[middle - 1] / 2.0 + sorted[middle] / 2.0);
    }

    private sealed record StageSample(
        string Name, long ElapsedMicroseconds, long AllocatedBytes, long WorkUnits, bool Success);

    private sealed class CompilerStageObserver : IDisposable
    {
        private readonly int ownerThread = Environment.CurrentManagedThreadId;
        private readonly List<IDisposable> subscriptions = [];
        private readonly IDisposable all;
        private readonly List<StageSample> samples = [];
        private bool recording;

        public CompilerStageObserver()
        {
            all = DiagnosticListener.AllListeners.Subscribe(new Observer<DiagnosticListener>(listener =>
            {
                if (listener.Name != "HsSqlAgent.SqlCore.Compiler") return;
                subscriptions.Add(listener.Subscribe(
                    new Observer<KeyValuePair<string, object?>>(evt =>
                    {
                        if (!recording || Environment.CurrentManagedThreadId != ownerThread ||
                            evt.Key != "StageCompleted" || evt.Value is null) return;
                        var payload = evt.Value;
                        object? Read(string name) => payload.GetType().GetProperty(name)?.GetValue(payload);
                        samples.Add(new StageSample(
                            Convert.ToString(Read("Stage"), CultureInfo.InvariantCulture) ?? "Unknown",
                            Convert.ToInt64(Read("ElapsedMicroseconds"), CultureInfo.InvariantCulture),
                            Convert.ToInt64(Read("AllocatedBytes"), CultureInfo.InvariantCulture),
                            Convert.ToInt64(Read("WorkUnits"), CultureInfo.InvariantCulture),
                            Convert.ToBoolean(Read("Success"), CultureInfo.InvariantCulture)));
                    })));
            }));
        }

        public void Begin()
        {
            samples.Clear();
            recording = true;
        }

        public StageSample[] End()
        {
            recording = false;
            return samples.ToArray();
        }

        public void Dispose()
        {
            all.Dispose();
            foreach (var subscription in subscriptions)
                subscription.Dispose();
        }
    }

    private sealed class Observer<T>(Action<T> onNext) : IObserver<T>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) => throw error;
        public void OnNext(T value) => onNext(value);
    }
}
