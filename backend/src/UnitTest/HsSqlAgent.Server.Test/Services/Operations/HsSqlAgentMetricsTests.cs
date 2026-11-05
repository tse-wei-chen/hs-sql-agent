using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Admin.Service.Models;
using HsSqlAgent.Server.Services;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

public class HsSqlAgentMetricsTests
{
    [Fact]
    public void RecordsLowCardinalitySqlRateLimitAndHealthMetrics()
    {
        var measurements = new ConcurrentBag<CapturedMeasurement>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == HsSqlAgentMetrics.MeterName)
                    meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            measurements.Add(new(instrument.Name, value, tags.ToArray())));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            measurements.Add(new(instrument.Name, value, tags.ToArray())));
        listener.Start();
        using var metrics = new HsSqlAgentMetrics();

        metrics.Record(
            "mcp.customer_supplied_tool_name.executed",
            "success",
            new AuditEventContext
            {
                ToolName = "customer_supplied_tool_name",
                Operation = "select",
                DurationMs = 125,
                ReturnedRows = 3
            });
        metrics.RecordRateLimitRejection("key");
        metrics.RecordSqlCompile("Rejected", "TargetCapability", "SQL_TARGET_FEATURE_UNSUPPORTED", "Postgres", "MySQL");
        metrics.RecordDbHealth(4812, "Postgres", "healthy", 17);
        listener.RecordObservableInstruments();

        Assert.Contains(measurements, item => item.Name == "hsqlagent.sql.executions" && item.LongValue == 1);
        Assert.Contains(measurements, item => item.Name == "hsqlagent.sql.execution.duration" && item.DoubleValue == 0.125);
        Assert.Contains(measurements, item => item.Name == "hsqlagent.rate_limit.rejections" && Tag(item, "layer")?.ToString() == "key");
        Assert.Contains(measurements, item =>
            item.Name == "hsqlagent.sql.compiles"
            && Tag(item, "verdict")?.ToString() == "rejected"
            && Tag(item, "boundary")?.ToString() == "targetcapability"
            && Tag(item, "decision_code")?.ToString() == "sql_target_feature_unsupported"
            && Tag(item, "source_provider")?.ToString() == "postgres"
            && Tag(item, "target_provider")?.ToString() == "mysql");
        Assert.Contains(measurements, item => item.Name == "hsqlagent.db.health.databases" && Tag(item, "provider")?.ToString() == "postgres" && Tag(item, "status")?.ToString() == "healthy");
        Assert.All(measurements, item =>
        {
            Assert.DoesNotContain(item.Tags, tag => tag.Key is "tool_name" or "database_id" or "access_key_id" or "evidence_fingerprint" or "trace_id");
            Assert.DoesNotContain(item.Tags, tag => Equals(tag.Value, "customer_supplied_tool_name") || Equals(tag.Value, 4812));
        });
    }

    private static object? Tag(CapturedMeasurement measurement, string name)
        => measurement.Tags.FirstOrDefault(tag => tag.Key == name).Value;

    private sealed record CapturedMeasurement(
        string Name,
        object Value,
        KeyValuePair<string, object?>[] Tags)
    {
        public long? LongValue => Value is long value ? value : null;
        public double? DoubleValue => Value is double value ? value : null;
    }
}
