using System.Diagnostics;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.SqlParsing;
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
            Assert.NotNull(SqlCoreInspection.GetQueryFacts(
                "SELECT 1 AS result", SqlAgentToolType.Postgres));
            Assert.NotNull(CoreSqlTextParser.ParseQuery(
                "SELECT 5 AS projected", SqlAgentToolType.Postgres));
            Assert.Equal(2, CoreDmlBatchTextParser.ParseDmlBatch(
                "DELETE FROM users WHERE id = 1; DELETE FROM users WHERE id = 2",
                SqlAgentToolType.Postgres).Count);
            var legacyParsed = CoreSqlTextParser.ParseQuery(
                "SELECT 17 AS result", SqlAgentToolType.Postgres);
            var legacyCompiled = CoreSqlCompiler.CreateDefault().Compile(
                legacyParsed, SqlAgentToolType.Postgres,
                new SqlPlanValidationContext("legacy-stage-quota-v1"),
                new SqlExecutionPlanPolicy());
            Assert.NotEmpty(legacyCompiled.Sql);

            List<object> captured;
            lock (samples) captured = samples.ToList();
            // DiagnosticListener is process-wide: parallel xUnit cases can
            // publish unsuccessful stage events concurrently. Assert on the
            // successful stage sample without assuming global isolation.
            var successful = captured
                .Where(x => Equals(Get(x, "Success"), true)).ToList();
            var stages = successful.Select(x => Get(x, "Stage") as string).ToHashSet();
            foreach (var name in new[] { "Parsing", "Binding", "Normalization",
                        "Validation", "Policy", "Rendering", "Inspection",
                        "CompatibilityProjection", "BatchSplit",
                        "LegacyConversion", "Materialization", "Fingerprint" })
                Assert.Contains(name, stages);

            foreach (var data in successful)
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

    [Fact]
    public void LargeComment_IsChargedAsInputScanWorkWithoutExtraTokens()
    {
        const int contentLength = 1_048_576;
        var ownerThread = Environment.CurrentManagedThreadId;
        var samples = new List<long>();
        var subscriptions = new List<IDisposable>();
        using var listener = DiagnosticListener.AllListeners.Subscribe(
            new Observer<DiagnosticListener>(source =>
            {
                if (source.Name != "HsSqlAgent.SqlCore.Compiler") return;
                subscriptions.Add(source.Subscribe(
                    new Observer<KeyValuePair<string, object?>>(measurement =>
                    {
                        if (measurement.Key != "StageCompleted" || measurement.Value is null
                            || Environment.CurrentManagedThreadId != ownerThread)
                            return;
                        if (!Equals(Get(measurement.Value, "Stage"), "Parsing")
                            || !Equals(Get(measurement.Value, "Success"), true))
                            return;
                        samples.Add(Convert.ToInt64(Get(measurement.Value, "WorkUnits")));
                    })));
            }));
        try
        {
            var sql = "/*" + new string('x', contentLength) + "*/ SELECT 1";
            var result = SqlCoreFacade.CompileQuery(
                sql, SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
                new SqlPlanValidationContext("long-comment-work-budget-v1"),
                new SqlExecutionPlanPolicy());
            Assert.NotEmpty(result.Sql);
            Assert.Contains(samples, units => units >= contentLength);
        }
        finally
        {
            foreach (var subscription in subscriptions) subscription.Dispose();
        }
    }

    [Fact]
    public void FailingTelemetryObserver_NeverOverridesSqlValidationDiagnostic()
    {
        var ownerThread = Environment.CurrentManagedThreadId;
        var callbackInvocations = 0;
        var subscriptions = new List<IDisposable>();
        using var listener = DiagnosticListener.AllListeners.Subscribe(
            new Observer<DiagnosticListener>(source =>
            {
                if (source.Name != "HsSqlAgent.SqlCore.Compiler") return;
                subscriptions.Add(source.Subscribe(
                    new Observer<KeyValuePair<string, object?>>(measurement =>
                    {
                        if (measurement.Key == "StageCompleted"
                            && Environment.CurrentManagedThreadId == ownerThread)
                        {
                            Interlocked.Increment(ref callbackInvocations);
                            throw new InvalidOperationException("telemetry subscriber failed");
                        }
                    })));
            }));
        try
        {
            var result = SqlCoreFacade.CompileQuery(
                "SELECT 12 AS result",
                SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
                new SqlPlanValidationContext("telemetry-should-not-change-sql"),
                new SqlExecutionPlanPolicy());
            Assert.NotEmpty(result.Sql);

            var failure = Assert.Throws<SqlParseException>(() =>
                SqlCoreFacade.CompileQuery(
                    "SELECT (",
                    SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
                    new SqlPlanValidationContext("telemetry-should-not-change-error"),
                    new SqlExecutionPlanPolicy()));
            Assert.NotNull(failure.Diagnostic);
            Assert.True(callbackInvocations > 0);
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
