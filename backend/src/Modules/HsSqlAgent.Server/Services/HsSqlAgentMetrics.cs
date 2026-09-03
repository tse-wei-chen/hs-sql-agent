using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Admin.Service.Interfaces;
using Admin.Service.Models;

namespace HsSqlAgent.Server.Services;

public interface IHsSqlAgentMetrics
{
    void McpRequestStarted();
    void McpRequestCompleted(int statusCode, TimeSpan duration);
    void RecordRateLimitRejection(string layer);
    void RecordSqlCompile(string verdict, string boundary, string decisionCode, string sourceProvider, string targetProvider);
    void RecordDbHealth(int databaseId, string provider, string status, long latencyMs);
}

public sealed class HsSqlAgentMetrics : IHsSqlAgentMetrics, IAuditMetricSink, IDisposable
{
    public const string MeterName = "HsSqlAgent.Server";

    private readonly Meter _meter = new(MeterName, "1.0.0");
    private readonly Counter<long> _mcpRequests;
    private readonly Histogram<double> _mcpRequestDuration;
    private readonly UpDownCounter<long> _activeMcpRequests;
    private readonly Counter<long> _sqlExecutions;
    private readonly Histogram<double> _sqlExecutionDuration;
    private readonly Histogram<long> _returnedRows;
    private readonly Histogram<long> _affectedRows;
    private readonly Counter<long> _rateLimitRejections;
    private readonly Counter<long> _sqlCompiles;
    private readonly Counter<long> _dmlApprovals;
    private readonly ConcurrentDictionary<int, DbHealthMeasurement> _databaseHealth = new();

    public HsSqlAgentMetrics()
    {
        _mcpRequests = _meter.CreateCounter<long>("hsqlagent.mcp.requests", "{request}", "Completed MCP HTTP requests.");
        _mcpRequestDuration = _meter.CreateHistogram<double>("hsqlagent.mcp.request.duration", "s", "MCP HTTP request duration.");
        _activeMcpRequests = _meter.CreateUpDownCounter<long>("hsqlagent.mcp.requests.active", "{request}", "Active MCP HTTP requests.");
        _sqlExecutions = _meter.CreateCounter<long>("hsqlagent.sql.executions", "{execution}", "SQL executions by operation and result.");
        _sqlExecutionDuration = _meter.CreateHistogram<double>("hsqlagent.sql.execution.duration", "s", "SQL execution duration.");
        _returnedRows = _meter.CreateHistogram<long>("hsqlagent.sql.rows.returned", "{row}", "Rows returned by query executions.");
        _affectedRows = _meter.CreateHistogram<long>("hsqlagent.sql.rows.affected", "{row}", "Rows affected by DML executions.");
        _rateLimitRejections = _meter.CreateCounter<long>("hsqlagent.rate_limit.rejections", "{rejection}", "Rejected requests by rate-limit layer.");
        _sqlCompiles = _meter.CreateCounter<long>("hsqlagent.sql.compiles", "{compile}", "SQL compiler decisions by verdict, boundary, diagnostic code and provider pair.");
        _dmlApprovals = _meter.CreateCounter<long>("hsqlagent.dml.approvals", "{approval}", "DML approval outcomes.");
        _meter.CreateObservableGauge(
            "hsqlagent.db.health.databases",
            ObserveDatabaseHealth,
            "{database}",
            "Configured databases grouped by provider and health state.");
        _meter.CreateObservableGauge(
            "hsqlagent.db.health.probe.duration",
            ObserveDatabaseProbeDuration,
            "ms",
            "Latest database health-probe duration, aggregated by provider.");
    }

    public void McpRequestStarted() => _activeMcpRequests.Add(1);

    public void McpRequestCompleted(int statusCode, TimeSpan duration)
    {
        _activeMcpRequests.Add(-1);
        var result = statusCode < 400 ? "success" : "failure";
        var tags = new TagList { { "result", result }, { "status_code", statusCode } };
        _mcpRequests.Add(1, tags);
        _mcpRequestDuration.Record(duration.TotalSeconds, tags);
    }

    public void RecordRateLimitRejection(string layer)
        => _rateLimitRejections.Add(1, new TagList { { "layer", Normalize(layer) } });

    public void RecordSqlCompile(
        string verdict,
        string boundary,
        string decisionCode,
        string sourceProvider,
        string targetProvider)
        => _sqlCompiles.Add(1, new TagList
        {
            { "verdict", Normalize(verdict) },
            { "boundary", Normalize(boundary) },
            { "decision_code", Normalize(decisionCode) },
            { "source_provider", Normalize(sourceProvider) },
            { "target_provider", Normalize(targetProvider) }
        });

    public void RecordDbHealth(int databaseId, string provider, string status, long latencyMs)
        => _databaseHealth[databaseId] = new(Normalize(provider), Normalize(status), Math.Max(0, latencyMs));

    public void Record(string action, string result, AuditEventContext eventContext)
    {
        if (!action.StartsWith("mcp.", StringComparison.Ordinal) ||
            !action.EndsWith(".executed", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(eventContext.Operation))
            return;

        var operation = Normalize(eventContext.Operation);
        var normalizedResult = Normalize(result);
        var toolKind = eventContext.ToolName is "execute_query_sql" or "execute_dml_sql" ? "builtin" : "custom";
        var tags = new TagList
        {
            { "operation", operation },
            { "result", normalizedResult },
            { "tool_kind", toolKind }
        };
        if (!string.IsNullOrWhiteSpace(eventContext.ErrorCategory))
            tags.Add("error_category", Normalize(eventContext.ErrorCategory));

        _sqlExecutions.Add(1, tags);
        if (eventContext.DurationMs is { } durationMs)
            _sqlExecutionDuration.Record(Math.Max(0, durationMs) / 1000d, tags);
        if (eventContext.ReturnedRows is { } returnedRows)
            _returnedRows.Record(Math.Max(0, returnedRows), new TagList { { "tool_kind", toolKind } });
        if (eventContext.AffectedRows is { } affectedRows)
            _affectedRows.Record(Math.Max(0, affectedRows), new TagList { { "operation", operation }, { "tool_kind", toolKind } });
        if (!string.IsNullOrWhiteSpace(eventContext.ApprovalStatus))
            _dmlApprovals.Add(1, new TagList { { "outcome", NormalizeApproval(eventContext.ApprovalStatus) }, { "tool_kind", toolKind } });
    }

    public void Dispose() => _meter.Dispose();

    private IEnumerable<Measurement<long>> ObserveDatabaseHealth()
        => _databaseHealth.Values
            .GroupBy(x => new { x.Provider, x.Status })
            .Select(group => new Measurement<long>(group.LongCount(), new TagList
            {
                { "provider", group.Key.Provider },
                { "status", group.Key.Status }
            }));

    private IEnumerable<Measurement<double>> ObserveDatabaseProbeDuration()
        => _databaseHealth.Values
            .GroupBy(x => x.Provider)
            .Select(group => new Measurement<double>(group.Average(x => x.LatencyMs), new TagList
            {
                { "provider", group.Key }
            }));

    private static string Normalize(string value)
        => value.Trim().ToLowerInvariant().Replace(' ', '_');

    private static string NormalizeApproval(string value)
        => value switch
        {
            "interactive-accepted" => "accepted",
            "declined" => "declined",
            _ => Normalize(value)
        };

    private readonly record struct DbHealthMeasurement(string Provider, string Status, long LatencyMs);
}
