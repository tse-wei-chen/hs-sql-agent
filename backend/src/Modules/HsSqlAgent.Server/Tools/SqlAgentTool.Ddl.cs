using System.ComponentModel;
using System.Diagnostics;
using Admin.Service.Models;
using Common.Models;
using HsSqlAgent.Server.Models;
using HsSqlAgent.Server.Services;
using ModelContextProtocol.Server;

namespace HsSqlAgent.Server.Tools;

public partial class SqlAgentTool
{
    /// <summary>
    /// Different from ordinary ValidateToolAccess: a missing/empty AllowedTools
    /// list cannot grant a high-risk DDL permission. Both this strict check
    /// and the separate Host feature gate are mandatory.
    /// </summary>
    private DdlActorContext RequireDdlGrant()
    {
        var context = _httpContextAccessor.HttpContext
            ?? throw new UnauthorizedAccessException("DDL requires an authenticated MCP context.");
        if (!context.Items.TryGetValue(McpContextItemKeys.AllowedTools, out var raw)
            || string.IsNullOrWhiteSpace(raw?.ToString())
            || !raw.ToString()!.Split(',',
                    StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Contains(McpBuiltInTools.ExecuteDdlSql, StringComparer.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("DDL requires an explicit execute_ddl_sql access-key grant.");
        if (context.Items[McpContextItemKeys.AccessKeyId] is not int accessKeyId
            || context.Items[McpContextItemKeys.DbManagementId] is not int databaseId
            || string.IsNullOrWhiteSpace(context.Items[McpContextItemKeys.DatabaseName]?.ToString()))
            throw new UnauthorizedAccessException("DDL requires an authenticated access key and target database identity.");
        return new DdlActorContext(
            "mcp-key:" + accessKeyId,
            "db-management:" + databaseId,
            context.Items[McpContextItemKeys.DatabaseName]!.ToString()!.Trim());
    }

    [McpServerTool(UseStructuredContent = true, ReadOnly = false), Description(@"
        Preview or execute exactly one explicitly allowed, PostgreSQL or SQLite schema DDL statement.
        With previewOnly=true, inspect live metadata and return a schema diff without changing data.
        With previewOnly=false, execution REQUIRES separately configured DDL approval, fresh physical
        database/schema evidence and a provider-specific verified transaction. DML approval does not grant
        DDL permission. The tool is disabled and hidden by default. Unsupported or destructive
        DDL, dynamic SQL and other database providers are always rejected.
    ")]
    public async Task<McpDdlToolResult> ExecuteDdlSql(
        [Description("One strictly additive PostgreSQL or SQLite DDL statement with explicit schema.table names.")]
        string sql,
        [Description("True to inspect and preview only, without DDL approval or mutation.")]
        bool previewOnly = true,
        CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew();
        DdlRuntimeResult? outcome = null;
        SqliteDdlRuntimeResult? sqliteOutcome = null;
        try
        {
            var actor = RequireDdlGrant();
            var config = await ResolveSqlConfigAsync();
            if (!CheckProviderAndConnectionString(config, out var providerType))
                throw new UnauthorizedAccessException("DDL provider or connection is unverified.");
            var configured = providerType switch
            {
                SqlAgentToolType.Postgres => _postgresDdlRuntime is { Enabled: true },
                SqlAgentToolType.Sqlite => _sqliteDdlRuntime is { Enabled: true },
                _ => false
            };
            if (!configured)
                throw new UnauthorizedAccessException("DDL must be explicitly enabled for the target provider.");
            // A durable request audit must succeed BEFORE any possible
            // schema mutation, even if the outcome audit later fails.
            await _auditService.WriteEventAsync(
                "mcp.ddl.requested",
                actor.TargetIdentity,
                "requested",
                new AuditEventContext
                {
                    ToolName = McpBuiltInTools.ExecuteDdlSql,
                    Operation = previewOnly ? "preview" : "execute",
                    DurationMs = watch.ElapsedMilliseconds
                },
                "Independent DDL permission, approval and live schema proof required.",
                cancellationToken);
            var provider = _sqlProviderFactory.GetProvider(providerType);
            await using var lease = await _sqlConcurrencyLimiter.TryAcquireAsync(cancellationToken);
            if (lease is null)
                throw new InvalidOperationException("DDL concurrency limit exceeded.");
            if (providerType == SqlAgentToolType.Sqlite)
            {
                sqliteOutcome = await _sqliteDdlRuntime!.RunAsync(
                    provider, config.ConnectionString, sql, actor,
                    previewOnly, _ddlApprovalProvider, cancellationToken);
                return McpDdlToolResult.FromSqliteRuntime(sqliteOutcome);
            }
            outcome = await _postgresDdlRuntime!.RunAsync(
                provider, config.ConnectionString, sql, actor,
                previewOnly, _ddlApprovalProvider, cancellationToken);
            return McpDdlToolResult.FromRuntime(outcome);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new McpDdlToolResult("rejected", false, null, null, null, null,
                null, null, "DDL request cancelled; no execution was authorized.",
                new McpToolError("ddl.cancelled", "DDL request cancelled.", "Approval"));
        }
        catch (UnauthorizedAccessException ex)
        {
            return new McpDdlToolResult("rejected", false, null, null, null, null,
                null, null, ex.Message, new McpToolError("ddl.permission_denied", ex.Message, "Authorization"));
        }
        catch (Exception)
        {
            return new McpDdlToolResult("failed", false, null, null, null, null,
                null, null, "DDL Host failed before starting execution.",
                new McpToolError("ddl.host_failed", "DDL Host failed before starting execution.", "Host"));
        }
        finally
        {
            // Persist an audit attempt even for rejected, pending or unknown
            // outcomes. Failure to emit audit must not convert a lost commit
            // response into a false success/failure or trigger a retry.
            try
            {
                await _auditService.WriteEventAsync(
                    "mcp.ddl.attempted",
                    outcome?.Preview?.Before is { } target
                        ? target.SchemaName + "." + target.TableName
                        : sqliteOutcome?.Preview?.Before is { } sqlite
                            ? sqlite.SchemaName + "." + sqlite.TableName : "ddl",
                    (outcome?.Status ?? sqliteOutcome?.Status)?.ToString().ToLowerInvariant() ?? "rejected",
                    new AuditEventContext
                    {
                        ToolName = McpBuiltInTools.ExecuteDdlSql,
                        Operation = outcome?.Preview?.StaticPreview.Plan.Changes[0].Operation.ToString()
                            ?? sqliteOutcome?.Preview?.StaticPreview.Plan.Changes[0].Operation.ToString()
                            ?? "denied",
                        DurationMs = watch.ElapsedMilliseconds,
                        Definition = outcome?.Preview?.HumanReadableDiff ?? sqliteOutcome?.Preview?.HumanReadableDiff,
                        ErrorCategory = outcome?.ErrorCode ?? sqliteOutcome?.ErrorCode
                    },
                    outcome?.Message ?? sqliteOutcome?.Message ?? "Denied at DDL Host access boundary.",
                    CancellationToken.None);
            }
            catch (Exception)
            {
                // OutcomeUnknown is deliberately not rewritten or retried.
            }
        }
    }
}
