using System.Diagnostics;
using System.Text.Json;
using Admin.Service.Data.Entites;
using Admin.Service.Interfaces;
using Admin.Service.Models;
using Common.Models;
using HsSqlAgent.Server.Services;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SqlAgent.Service.Interfaces;

namespace HsSqlAgent.Server.Tools;

public class CustomToolProxy(
    string name,
    ICustomSqlToolService customSqlToolService,
    IHttpContextAccessor httpContextAccessor,
    ISqlProviderFactory sqlProviderFactory,
    IAuditService auditService,
    IQueryValueParserService queryValueParserService,
    ISecurityPolicyRuntimeState securityPolicyRuntimeState,
    ISqlExecutionConcurrencyLimiter sqlConcurrencyLimiter,
    ITypedQueryRuntime? typedQueryRuntime = null,
    TypedDmlRuntime? typedDmlRuntime = null)
{
    private readonly string _name = name;
    private readonly ICustomSqlToolService _customSqlToolService = customSqlToolService;
    private readonly IHttpContextAccessor _httpContextAccessor = httpContextAccessor;
    private readonly ISqlProviderFactory _sqlProviderFactory = sqlProviderFactory;
    private readonly IAuditService _auditService = auditService;
    private readonly IQueryValueParserService _queryValueParserService = queryValueParserService;
    private readonly ISecurityPolicyRuntimeState _securityPolicyRuntimeState = securityPolicyRuntimeState;
    private readonly ISqlExecutionConcurrencyLimiter _sqlConcurrencyLimiter = sqlConcurrencyLimiter;
    private readonly ITypedQueryRuntime _typedQueryRuntime = typedQueryRuntime ?? new TypedQueryRuntime();
    private readonly TypedDmlRuntime _typedDmlRuntime = typedDmlRuntime ?? new TypedDmlRuntime();

    public async Task<string> Execute(
        JsonElement arguments,
        McpServer? server = null,
        CancellationToken cancellationToken = default)
        => await ExecuteCore(
            arguments,
            server is null ? null : new McpDmlApprovalClient(server),
            cancellationToken);

    internal async Task<string> Execute(
        JsonElement arguments,
        IDmlApprovalClient? approvalClient,
        CancellationToken cancellationToken = default)
        => await ExecuteCore(arguments, approvalClient, cancellationToken);

    private async Task<string> ExecuteCore(
        JsonElement arguments,
        IDmlApprovalClient? approvalClient,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        long approvalWaitDurationMs = 0;
        int? queryReturnedRows = null;
        int? dmlAffectedRows = null;
        var parameters = new Dictionary<string, object?>();
        if (arguments.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in arguments.EnumerateObject())
                parameters[prop.Name] = _queryValueParserService.UnwrapJsonElement(prop.Value);
        }

        CustomSqlTool? tool = null;
        ParsedStatement? auditQuery = null;
        ParsedStatement? auditDml = null;
        string renderedSql = string.Empty;
        try
        {
            ValidateToolAccess();
            var sqlConfig = ResolveSqlConfig();
            var dbManagementId = ResolveDbManagementId();
            if (dbManagementId is null)
            {
                var error = "Error: The authenticated key is not bound to a database.";
                await _auditService.WriteLogAsync($"mcp.{_name}.executed", _name, "failed", error);
                return error;
            }

            tool = await _customSqlToolService.GetPublishedToolByNameAsync(
                _name,
                dbManagementId.Value,
                cancellationToken);
            if (tool == null)
            {
                var error = $"Error: Published tool '{_name}' is not available for this database.";
                await _auditService.WriteLogAsync($"mcp.{_name}.executed", _name, "failed", error);
                return error;
            }

            if (string.IsNullOrWhiteSpace(sqlConfig.Provider) || string.IsNullOrWhiteSpace(sqlConfig.ConnectionString))
            {
                var error = "Error: SQL configuration (provider/connection string) is missing.";
                await _auditService.WriteLogAsync($"mcp.{_name}.executed", _name, "failed", error);
                return error;
            }

            if (!Enum.TryParse<SqlAgentToolType>(sqlConfig.Provider, true, out var dbType))
            {
                var error = $"Error: Invalid SQL provider '{sqlConfig.Provider}'.";
                await _auditService.WriteLogAsync($"mcp.{_name}.executed", _name, "failed", error);
                return error;
            }

            renderedSql = CustomToolSqlTemplate.Render(tool.SqlTemplate, tool.ParametersJson, parameters);
            var provider = _sqlProviderFactory.GetProvider(dbType);
            string result;
            var isQuery = string.Equals(tool.Type, "Query", StringComparison.OrdinalIgnoreCase);
            var isDml = string.Equals(tool.Type, "DML", StringComparison.OrdinalIgnoreCase);

            if (isQuery)
            {
                var parsedQuery = CoreSqlTextParser.ParseQuery(renderedSql, dbType);
                auditQuery = parsedQuery;

                await using (var lease = await _sqlConcurrencyLimiter.TryAcquireAsync(cancellationToken))
                {
                    if (lease is null)
                        throw new InvalidOperationException("Server busy: maximum concurrent SQL operations reached.");
                    var execution = await _typedQueryRuntime.ExecuteAsync(
                        provider,
                        sqlConfig.ConnectionString,
                        parsedQuery,
                        _securityPolicyRuntimeState.GetCurrent(),
                        ResolveTableWhitelist(),
                        cancellationToken);
                    queryReturnedRows = execution.RowCount;
                    result = JsonSerializer.Serialize(execution.Rows);
                }
            }
            else if (isDml)
            {
                var parsedDml = CoreSqlTextParser.ParseDml(renderedSql, dbType);
                auditDml = parsedDml;
                TypedDmlRuntime.EnsureSupportedStatement(parsedDml.Statement);

                var approvalContext = DmlApprovalExecutionContextResolver.FromMcp(
                    _httpContextAccessor.HttpContext,
                    dbType);
                var flow = new TypedDmlApprovalFlow(
                    _typedDmlRuntime,
                    _securityPolicyRuntimeState,
                    _sqlConcurrencyLimiter,
                    ResolveTableWhitelist);
                var dmlExecution = await flow.ExecuteAsync(
                    provider,
                    sqlConfig.ConnectionString,
                    parsedDml,
                    approvalContext,
                    approvalClient,
                    $"Custom tool `{_name}`",
                    cancellationToken);
                result = dmlExecution.Result;
                approvalWaitDurationMs += dmlExecution.ApprovalWaitDurationMs;
                dmlAffectedRows = dmlExecution.AffectedRows;

                if (!dmlExecution.Committed)
                {
                    var auditResult = result.Contains("cancelled", StringComparison.OrdinalIgnoreCase)
                        ? "cancelled"
                        : "failed";
                    await _auditService.WriteEventAsync(
                        $"mcp.{_name}.executed",
                        _name,
                        auditResult,
                        new AuditEventContext
                        {
                            ToolName = _name,
                            Operation = DmlOperationName(parsedDml),
                            DurationMs = ProcessingDuration(stopwatch, approvalWaitDurationMs),
                            AffectedRows = dmlAffectedRows,
                            ApprovalStatus = auditResult == "cancelled" ? "declined" : "not-completed",
                            Definition = DescribeDml(parsedDml)
                        },
                        result,
                        cancellationToken);
                    return result;
                }
            }
            else
            {
                result = $"Error: Unsupported tool type '{tool.Type}'.";
                await _auditService.WriteLogAsync($"mcp.{_name}.executed", _name, "failed", result);
                return result;
            }

            await _auditService.WriteEventAsync(
                $"mcp.{_name}.executed",
                _name,
                "success",
                new AuditEventContext
                {
                    ToolName = _name,
                    Operation = isQuery ? "select" : auditDml is null ? null : DmlOperationName(auditDml),
                    DurationMs = isDml
                        ? ProcessingDuration(stopwatch, approvalWaitDurationMs)
                        : stopwatch.ElapsedMilliseconds,
                    ReturnedRows = isQuery ? queryReturnedRows : null,
                    AffectedRows = isDml ? dmlAffectedRows : null,
                    ApprovalStatus = isDml ? "interactive-accepted" : null,
                    Definition = isQuery && auditQuery != null
                        ? DescribeQuery(auditQuery)
                        : auditDml == null ? null : DescribeDml(auditDml)
                },
                $"Type: {tool.Type}",
                cancellationToken);
            return result;
        }
        catch (Exception ex)
        {
            await _auditService.WriteEventAsync(
                $"mcp.{_name}.executed",
                _name,
                "failed",
                new AuditEventContext
                {
                    ToolName = _name,
                    Operation = auditQuery != null
                        ? "select"
                        : auditDml is null ? null : DmlOperationName(auditDml),
                    DurationMs = auditDml == null
                        ? stopwatch.ElapsedMilliseconds
                        : ProcessingDuration(stopwatch, approvalWaitDurationMs),
                    ReturnedRows = queryReturnedRows,
                    AffectedRows = dmlAffectedRows,
                    ErrorCategory = ex.GetType().Name,
                    Definition = auditQuery != null
                        ? DescribeQuery(auditQuery)
                        : auditDml == null ? null : DescribeDml(auditDml)
                },
                ex.Message,
                cancellationToken);
            var toolType = tool?.Type ?? "Unknown";
            var suggestedTool = string.Equals(toolType, "Query", StringComparison.OrdinalIgnoreCase)
                ? "execute_query_sql"
                : "execute_dml_sql";
            return $"Error: {ex.Message}\nPlease fix the parameters or SQL template and use '{suggestedTool}' to try again.";
        }
    }

    private static long ProcessingDuration(Stopwatch stopwatch, long approvalWaitDurationMs) =>
        Math.Max(0, stopwatch.ElapsedMilliseconds - approvalWaitDurationMs);

    private static string DescribeQuery(ParsedStatement parsed)
    {
        var containsCte = parsed.Statement switch
        {
            SelectStatement select => !select.Ctes.IsDefaultOrEmpty,
            QueryStatement query => !query.Head.Ctes.IsDefaultOrEmpty,
            _ => false
        };
        var containsSubquery = parsed.Statement switch
        {
            SelectStatement { From: DerivedTableSource } => true,
            QueryStatement { Head.From: DerivedTableSource } => true,
            _ => false
        };
        return JsonSerializer.Serialize(new
        {
            SourceDialect = parsed.SourceDialect.ToString(),
            Span = new { parsed.Statement.Span.Start, parsed.Statement.Span.End },
            ReferencedTables = Array.Empty<string>(),
            ContainsCte = containsCte,
            ContainsSubquery = containsSubquery
        });
    }

    private static string DescribeDml(ParsedStatement parsedDml)
    {
        var table = parsedDml.Statement switch
        {
            UpdateStatement update => IdentifierText(update.Target.Name),
            DeleteStatement delete => IdentifierText(delete.Target.Name),
            InsertStatement insert => IdentifierText(insert.Target.Name),
            _ => "unknown"
        };
        var fields = parsedDml.Statement switch
        {
            UpdateStatement updateStatement => updateStatement.Assignments
                .Select(assignment => IdentifierText(assignment.Column))
                .ToArray(),
            InsertStatement insertStatement => insertStatement.Columns
                .Select(IdentifierText)
                .ToArray(),
            _ => []
        };
        var hasWhere = parsedDml.Statement switch
        {
            UpdateStatement update => update.Predicate is not null,
            DeleteStatement delete => delete.Predicate is not null,
            _ => false
        };
        return JsonSerializer.Serialize(new
        {
            Operation = DmlOperationName(parsedDml),
            TableName = table,
            ValueFields = fields,
            HasWhere = hasWhere
        });
    }

    private static string DmlOperationName(ParsedStatement parsedDml) => parsedDml.Statement switch
    {
        UpdateStatement => "update",
        DeleteStatement => "delete",
        InsertStatement => "insert",
        _ => parsedDml.Statement.GetType().Name.ToLowerInvariant()
    };

    private static string IdentifierText(SqlIdentifier identifier) =>
        string.Join('.', identifier.Parts.Select(part => part.Value));

    private SqlRuntimeConfig ResolveSqlConfig()
    {
        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext != null)
        {
            var provider = httpContext.Items[McpContextItemKeys.SqlProvider]?.ToString();
            var connectionString = httpContext.Items[McpContextItemKeys.SqlConnectionString]?.ToString();
            if (!string.IsNullOrWhiteSpace(provider) && !string.IsNullOrWhiteSpace(connectionString))
                return new SqlRuntimeConfig { Provider = provider, ConnectionString = connectionString };
        }
        return new SqlRuntimeConfig();
    }

    private int? ResolveDbManagementId()
    {
        var context = _httpContextAccessor.HttpContext;
        return context?.Items.TryGetValue(McpContextItemKeys.DbManagementId, out var value) == true
               && value is int id
            ? id
            : null;
    }

    private HashSet<string>? ResolveTableWhitelist()
    {
        var context = _httpContextAccessor.HttpContext
            ?? throw new UnauthorizedAccessException("MCP table authorization context is missing.");
        if (!context.Items.TryGetValue(McpContextItemKeys.TableWhitelist, out var whitelistValue))
        {
            throw new UnauthorizedAccessException("MCP table authorization context is missing.");
        }

        var tableWhitelist = whitelistValue?.ToString();
        if (string.IsNullOrWhiteSpace(tableWhitelist)) return null;
        return tableWhitelist
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private void ValidateToolAccess()
    {
        var context = _httpContextAccessor.HttpContext
            ?? throw new UnauthorizedAccessException("MCP tool authorization context is missing.");
        if (!context.Items.TryGetValue(McpContextItemKeys.AllowedTools, out var allowedToolsValue))
        {
            throw new UnauthorizedAccessException("MCP tool authorization context is missing.");
        }

        var allowedTools = allowedToolsValue?.ToString();
        if (string.IsNullOrWhiteSpace(allowedTools)) return;
        var allowed = allowedTools
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(x => string.Equals(x, _name, StringComparison.OrdinalIgnoreCase));
        if (!allowed)
            throw new UnauthorizedAccessException($"API key does not have permission to use tool: {_name}");
    }
}

internal interface IDmlApprovalClient
{
    bool SupportsElicitation { get; }

    ValueTask<ElicitResult> ElicitAsync(
        ElicitRequestParams request,
        CancellationToken cancellationToken);
}

internal sealed class McpDmlApprovalClient(McpServer server) : IDmlApprovalClient
{
    public bool SupportsElicitation => server.ClientCapabilities?.Elicitation != null;

    public ValueTask<ElicitResult> ElicitAsync(
        ElicitRequestParams request,
        CancellationToken cancellationToken) =>
        server.ElicitAsync(request, cancellationToken);
}
