using System.Diagnostics;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreCompilerResourceTelemetryTests
{
    [Fact]
    public void Compilation_ReportsStageWorkAndAllocationWithoutUserSql()
    {
        var samples = new List<object>();
        var subscriptions = new List<IDisposable>();
        using var listener = DiagnosticListener.AllListeners.Subscribe(
            new Observer<DiagnosticListener>(source =>
            {
                if (source.Name != "HsSqlAgent.SqlCore.Compiler") return;
                subscriptions.Add(source.Subscribe(
                    new Observer<KeyValuePair<string, object?>>(measurement =>
                    {
                        if (measurement.Key == "StageCompleted" && measurement.Value is not null)
                        {
                            lock (samples) samples.Add(measurement.Value);
                        }
                    })));
            }));
        try
        {
            var command = SqlCoreFacade.CompileQuery(
                "SELECT 42 AS safe_output",
                SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
                new SqlPlanValidationContext("resource-telemetry-v1"),
                new SqlExecutionPlanPolicy());
            Assert.NotEmpty(command.Sql);

            List<object> captured;
            lock (samples) captured = samples.ToList();
            var stages = captured.Select(x => Get(x, "Stage") as string).ToHashSet();
            foreach (var name in new[] { "Parsing", "Binding", "Normalization",
                        "Validation", "Policy", "Rendering" })
                Assert.Contains(name, stages);

            foreach (var data in captured)
            {
                Assert.True(Convert.ToInt64(Get(data, "ElapsedMicroseconds")) >= 0);
                Assert.True(Convert.ToInt64(Get(data, "AllocatedBytes")) >= 0);
                Assert.True(Convert.ToInt64(Get(data, "WorkUnits")) >= 0);
                Assert.Equal(true, Get(data, "Success"));
                var props = data.GetType().GetProperties()
                    .Select(p => p.Name).OrderBy(n => n).ToArray();
                Assert.Equal(new[] { "AllocatedBytes", "ElapsedMicroseconds",
                    "Stage", "Success", "WorkUnits" }, props);
            }
        }
        finally
        {
            foreach (var subscription in subscriptions) subscription.Dispose();
        }
    }

    private static object? Get(object value, string property) =>
        value.GetType().GetProperty(property)?.GetValue(value);

    private sealed class Observer<T>(Action<T> onNext) : IObserver<T>
    {
        public void OnNext(T value) => onNext(value);
        public void OnCompleted() { }
        public void OnError(Exception error) { }
    }
}
