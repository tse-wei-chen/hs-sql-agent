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
        Preview or execute exactly one explicitly allowed, PostgreSQL-only schema DDL statement.
        With previewOnly=true, inspect live metadata and return a schema diff without changing data.
        With previewOnly=false, execution REQUIRES separately configured DDL approval, fresh physical
        database/schema evidence and a verified PostgreSQL transaction. DML approval does not grant
        DDL permission. The tool is disabled and hidden by default. Unsupported or destructive
        DDL, dynamic SQL and other database providers are always rejected.
    ")]
    public async Task<McpDdlToolResult> ExecuteDdlSql(
        [Description("One strictly additive PostgreSQL DDL statement with explicit schema.table names.")]
        string sql,
        [Description("True to inspect and preview only, without DDL approval or mutation.")]
        bool previewOnly = true,
        CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew();
        DdlRuntimeResult? outcome = null;
        try
        {
            if (_postgresDdlRuntime is not { Enabled: true })
                throw new UnauthorizedAccessException("DDL is not enabled by the Host.");
            var actor = RequireDdlGrant();
            var config = await ResolveSqlConfigAsync();
            if (!CheckProviderAndConnectionString(config, out var providerType)
                || providerType != SqlAgentToolType.Postgres)
                throw new UnauthorizedAccessException("DDL is restricted to PostgreSQL.");
            var provider = _sqlProviderFactory.GetProvider(providerType);
            await using var lease = await _sqlConcurrencyLimiter.TryAcquireAsync(cancellationToken);
            if (lease is null)
                throw new InvalidOperationException("DDL concurrency limit exceeded.");
            outcome = await _postgresDdlRuntime.RunAsync(
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
                        ? target.SchemaName + "." + target.TableName : "ddl",
                    outcome?.Status.ToString().ToLowerInvariant() ?? "rejected",
                    new AuditEventContext
                    {
                        ToolName = McpBuiltInTools.ExecuteDdlSql,
                        Operation = outcome?.Preview?.StaticPreview.Plan.Changes[0].Operation.ToString()
                            ?? "denied",
                        DurationMs = watch.ElapsedMilliseconds,
                        Definition = outcome?.Preview?.HumanReadableDiff,
                        ErrorCategory = outcome?.ErrorCode
                    },
                    outcome?.Message ?? "Denied at DDL Host access boundary.",
                    CancellationToken.None);
            }
            catch (Exception)
            {
                // OutcomeUnknown is deliberately not rewritten or retried.
            }
        }
    }
}
