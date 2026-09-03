using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using HsSqlAgent.Server.Services;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using Microsoft.Extensions.Logging;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

public sealed class SqlCompileEvidenceObserverTests
{
    [Fact]
    public void Observe_EmitsStructuredLogTraceAndLowCardinalityMetricWithoutSqlText()
    {
        const string sql = "SELECT id FROM users WHERE id = 42";
        var command = SqlCoreFacade.CompileQuery(
            sql,
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("observer-test"),
            new SqlExecutionPlanPolicy(25));
        var evidence = Assert.IsType<SqlCompileEvidence>(command.CompileEvidence);

        var measurements = new ConcurrentBag<CapturedMeasurement>();
        using var meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == HsSqlAgentMetrics.MeterName)
                    listener.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            measurements.Add(new(instrument.Name, value, tags.ToArray())));
        meterListener.Start();

        var activities = new ConcurrentBag<Activity>();
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == SqlCompileEvidenceObserver.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => activities.Add(activity)
        };
        ActivitySource.AddActivityListener(activityListener);

        using var metrics = new HsSqlAgentMetrics();
        var logger = new CapturingLogger<SqlCompileEvidenceObserver>();
        var observer = new SqlCompileEvidenceObserver(logger, metrics);

        observer.Observe(evidence);

        var compileMetric = Assert.Single(measurements, item => item.Name == "hsqlagent.sql.compiles");
        Assert.Equal("translated", Tag(compileMetric, "verdict"));
        Assert.Equal("completed", Tag(compileMetric, "boundary"));
        Assert.Equal("postgres", Tag(compileMetric, "source_provider"));
        Assert.Equal("postgres", Tag(compileMetric, "target_provider"));
        Assert.DoesNotContain(compileMetric.Tags, tag => tag.Key is "evidence_fingerprint" or "trace_id" or "sql");

        var activity = Assert.Single(activities);
        Assert.Equal("sql.compile.decision", activity.OperationName);
        Assert.Equal(evidence.EvidenceFingerprint, activity.GetTagItem("sql.compile.evidence_fingerprint"));
        Assert.Null(activity.GetTagItem("db.statement"));

        var log = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Debug, log.Level);
        Assert.Equal(evidence.EvidenceFingerprint, log.State["EvidenceFingerprint"]);
        Assert.DoesNotContain(sql, log.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(log.State.Keys, key => key is "Sql" or "Parameters" or "PlanFingerprint");
    }

    [Fact]
    public void Observe_ExceptionUsesRejectedCompileEvidence()
    {
        var error = Assert.ThrowsAny<Exception>(() => SqlCoreFacade.CompileQuery(
            "SELECT FROM",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("observer-rejection-test"),
            new SqlExecutionPlanPolicy(25)));
        var evidence = Assert.IsType<SqlCompileEvidence>(SqlCompileEvidence.TryGetFromException(error));

        using var metrics = new HsSqlAgentMetrics();
        var logger = new CapturingLogger<SqlCompileEvidenceObserver>();
        var observer = new SqlCompileEvidenceObserver(logger, metrics);

        observer.Observe(error);

        var log = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, log.Level);
        Assert.Equal(evidence.DecisionCode, log.State["DecisionCode"]);
        Assert.Equal(evidence.EvidenceFingerprint, log.State["EvidenceFingerprint"]);
    }

    private static string? Tag(CapturedMeasurement measurement, string name)
        => measurement.Tags.FirstOrDefault(tag => tag.Key == name).Value?.ToString();

    private sealed record CapturedMeasurement(
        string Name,
        long Value,
        KeyValuePair<string, object?>[] Tags);

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var values = state as IEnumerable<KeyValuePair<string, object?>>
                ?? [];
            Entries.Add(new(
                logLevel,
                formatter(state, exception),
                values.ToDictionary(pair => pair.Key, pair => pair.Value)));
        }
    }

    private sealed record LogEntry(
        LogLevel Level,
        string Message,
        IReadOnlyDictionary<string, object?> State);
}
