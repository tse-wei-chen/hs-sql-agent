using System.Data.Common;
using System.Text;
using Admin.Service.Interfaces;
using Admin.Service.Models;
using Common.Interfaces;
using HsSqlAgent.Server.Authorization;
using HsSqlAgent.Server.Models;
using HsSqlAgent.Server.Services;
using HsSqlAgent.SqlCore.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SqlAgent.Service.Factories;

namespace HsSqlAgent.Server.Controllers;

[ApiController]
[Route("api/runtime/security/sql-explain")]
public sealed class SqlExplainController(
    IAuditService auditService,
    ISqlCompileEvidenceObserver? compileEvidenceObserver = null) : ControllerBase
{
    [HttpPost]
    [HasPermission("/runtime/security", "view")]
    public async Task<IActionResult> Explain(
        [FromBody] SqlExplainRequest request,
        [FromServices] IDbManagementService dbManagementService,
        [FromServices] IMcpAccessKeyService keyService,
        [FromServices] ISecurityPolicyRuntimeState securityPolicyRuntimeState,
        [FromServices] ISqlProviderFactory providerFactory,
        [FromServices] ISqlConnectionStringFactory connectionStringFactory,
        [FromServices] ICryptoService cryptoService,
        [FromServices] IOptions<McpKeySettings> mcpKeySettings,
        CancellationToken cancellationToken)
    {
        if (request is null) return BadRequest(new { error = "Request is required." });
        if (request.DbManagementId <= 0) return BadRequest(new { error = "A target database is required." });
        if (string.IsNullOrWhiteSpace(request.Sql)) return BadRequest(new { error = "SQL is required." });

        var statementType = SqlExplainCompiler.NormalizeStatementType(request.StatementType);
        if (statementType is not ("Query" or "DML"))
            return BadRequest(new { error = "StatementType must be either 'Query' or 'DML'." });

        if (await dbManagementService.GetDbByIdAsync(request.DbManagementId, true, cancellationToken)
            is not DbManagementPwdVM database)
            return NotFound(new { error = $"Database with ID {request.DbManagementId} was not found." });
        if (!Enum.TryParse<SqlAgentToolType>(database.SqlProvider, true, out var targetProvider))
            return BadRequest(new { error = $"Invalid SQL provider '{database.SqlProvider}'." });

        var sourceDialect = targetProvider;
        if (!string.IsNullOrWhiteSpace(request.SourceDialect)
            && !Enum.TryParse(request.SourceDialect, true, out sourceDialect))
            return BadRequest(new { error = $"Invalid source dialect '{request.SourceDialect}'." });

        var requiredTool = statementType == "DML" ? "execute_dml_sql" : "execute_query_sql";
        var scopeResult = await ResolveScopeAsync(
            request,
            requiredTool,
            keyService,
            cancellationToken);
        if (scopeResult.Error is not null) return BadRequest(new { error = scopeResult.Error });

        var policy = securityPolicyRuntimeState.GetCurrent();
        var scope = scopeResult.Scope!;
        if (!scope.KeyActive)
        {
            var inactive = SqlExplainCompiler.Denied(
                request,
                sourceDialect,
                targetProvider,
                policy,
                scope,
                "authorization.key_inactive",
                "The selected MCP key is inactive or expired.");
            await WriteAuditAsync(request, inactive, cancellationToken);
            return Ok(inactive);
        }
        if (!scope.ToolAllowed)
        {
            var denied = SqlExplainCompiler.Denied(
                request,
                sourceDialect,
                targetProvider,
                policy,
                scope,
                "authorization.tool_denied",
                $"The selected MCP key does not allow '{requiredTool}'.");
            await WriteAuditAsync(request, denied, cancellationToken);
            return Ok(denied);
        }

        var provider = providerFactory.GetProvider(targetProvider);
        var connectionString = BuildConnectionString(
            database,
            targetProvider,
            connectionStringFactory,
            cryptoService,
            mcpKeySettings);

        try
        {
            await using var connection = provider.Connections.Create(connectionString);
            await connection.OpenAsync(cancellationToken);
            var targetProfile = TypedQueryRuntime.CreateVerifiedTargetProfile(targetProvider, connection);
            var result = new SqlExplainCompiler(compileEvidenceObserver).Explain(
                provider,
                request,
                sourceDialect,
                policy,
                scopeResult.AllowedTables,
                targetProfile,
                scope);
            await WriteAuditAsync(request, result, cancellationToken);
            return Ok(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var failure = new SqlExplainResponse(
                false,
                "compile-only",
                false,
                statementType,
                sourceDialect.ToString(),
                targetProvider.ToString(),
                scope,
                SqlExplainModelMapper.Policy(policy),
                [],
                new SqlExplainFailure(
                    "runtime_profile.unavailable",
                    "The target database runtime profile could not be verified. No SQL was executed.",
                    "RuntimeProfile",
                    exception is DbException or TimeoutException,
                    [],
                    null));
            await WriteAuditAsync(request, failure, cancellationToken, exception.GetType().Name);
            return Ok(failure);
        }
    }

    private async Task WriteAuditAsync(
        SqlExplainRequest request,
        SqlExplainResponse response,
        CancellationToken cancellationToken,
        string? detail = null)
    {
        await auditService.WriteLogAsync(
            "security.sql.explained",
            request.DbManagementId.ToString(),
            response.Success ? "success" : "rejected",
            detail ?? $"Type: {response.StatementType}; Source: {response.SourceDialect}; Target: {response.TargetProvider}; Executed: false; Decision: {response.Failure?.Code ?? "translated"}",
            cancellationToken);
    }

    private static async Task<ScopeResolution> ResolveScopeAsync(
        SqlExplainRequest request,
        string requiredTool,
        IMcpAccessKeyService keyService,
        CancellationToken cancellationToken)
    {
        if (!request.AccessKeyId.HasValue)
        {
            return new ScopeResolution(
                new SqlExplainScope(
                    null,
                    null,
                    true,
                    requiredTool,
                    true,
                    "Global policy only",
                    "All tables",
                    []),
                null,
                null);
        }

        var key = (await keyService.ListKeysAsync(cancellationToken))
            .FirstOrDefault(candidate => candidate.Id == request.AccessKeyId.Value);
        if (key is null)
            return new ScopeResolution(null, null, $"MCP key {request.AccessKeyId.Value} was not found.");
        if (key.DbManagementId != request.DbManagementId)
            return new ScopeResolution(null, null, "The selected MCP key belongs to a different database.");

        var allowedTools = SplitCsv(key.AllowedTools);
        var unrestrictedTools = allowedTools.Count == 0;
        var allowedTables = SplitCsv(key.TableWhitelist);
        IReadOnlySet<string>? tableSet = allowedTables.Count == 0
            ? null
            : allowedTables.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var active = key.IsActive && !key.IsExpired;
        var toolAllowed = unrestrictedTools
                          || allowedTools.Contains(requiredTool, StringComparer.OrdinalIgnoreCase);

        return new ScopeResolution(
            new SqlExplainScope(
                key.Id,
                key.Name,
                active,
                requiredTool,
                toolAllowed,
                unrestrictedTools ? "Unrestricted tools" : $"{allowedTools.Count} selected tools",
                tableSet is null ? "All tables" : $"{tableSet.Count} restricted tables",
                tableSet?.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray() ?? []),
            tableSet,
            null);
    }

    private static IReadOnlyList<string> SplitCsv(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

    private static string BuildConnectionString(
        DbManagementPwdVM database,
        SqlAgentToolType dbType,
        ISqlConnectionStringFactory connectionStringFactory,
        ICryptoService cryptoService,
        IOptions<McpKeySettings> mcpKeySettings)
    {
        var hmacSecret = Encoding.UTF8.GetBytes(mcpKeySettings.Value.HmacSecretKey);
        var password = cryptoService.DecryptText(database.PasswordHash, hmacSecret);
        return connectionStringFactory.BuildConnectionString(
            dbType,
            new BuildDbConnectionModelBase
            {
                Host = database.Host,
                Port = database.Port,
                Username = database.Username,
                Password = password,
                Database = database.Database,
                ExtraSettings = database.ExtraSettings
            });
    }

    private sealed record ScopeResolution(
        SqlExplainScope? Scope,
        IReadOnlySet<string>? AllowedTables,
        string? Error);
}
