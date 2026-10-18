using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Admin.Service.Data;
using Admin.Service.Models;
using Common.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SqlAgent.Service.Interfaces;

namespace HsSqlAgent.Server.Services;

/// <summary>
/// Fresh Admin Store authorization proof for synchronous, human-approved DML.
/// Never trust a five-minute key-validation cache or an unchanged HttpContext
/// after awaiting an external MCP approval.
/// </summary>
internal interface IMcpDmlCommitAuthorizationVerifier
{
    Task<McpDmlCommitAuthorizationSnapshot> CaptureAsync(
        McpSqlExecutionScope scope,
        string originalConnectionString,
        int? customToolId,
        int? customToolRevisionId,
        CancellationToken cancellationToken);
}

internal sealed record McpDmlCommitAuthorizationSnapshot(
    string Fingerprint,
    IReadOnlySet<string>? AllowedTables)
{
    internal void EnsureUnchanged(McpDmlCommitAuthorizationSnapshot latest)
    {
        if (!string.Equals(Fingerprint, latest.Fingerprint, StringComparison.Ordinal))
            throw new UnauthorizedAccessException(
                "MCP access key, tool, target connection or policy changed while DML approval was pending.");
    }
}

internal sealed class McpDmlCommitAuthorizationVerifier(
    IAdminContext adminContext,
    IDbSetterService dbSetter,
    ICryptoService crypto,
    IOptions<McpKeySettings> options) : IMcpDmlCommitAuthorizationVerifier
{
    private readonly byte[] _secret = Encoding.UTF8.GetBytes(options.Value.HmacSecretKey);

    public async Task<McpDmlCommitAuthorizationSnapshot> CaptureAsync(
        McpSqlExecutionScope scope,
        string originalConnectionString,
        int? customToolId,
        int? customToolRevisionId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(originalConnectionString);
        scope.EnsureDmlColumnPolicySupported();

        // AsNoTracking forces a new database read in the same request/scope.
        var key = await adminContext.McpAccessKeys.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == scope.AccessKeyId, cancellationToken)
            ?? throw Denied("MCP access key no longer exists.");
        if (!key.IsActive || key.RevokedAt.HasValue
            || (key.ExpiresAt.HasValue && key.ExpiresAt <= DateTime.UtcNow))
            throw Denied("MCP access key is revoked or expired.");
        if (key.DbManagementId != scope.DbManagementId || key.AllowedQueryColumns is not null)
            throw Denied("MCP access key database or column authorization changed.");
        if (!string.IsNullOrWhiteSpace(key.AllowedTools)
            && !key.AllowedTools.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Contains(scope.ToolName, StringComparer.OrdinalIgnoreCase))
            throw Denied("MCP access key no longer permits this DML tool.");

        var tables = string.IsNullOrWhiteSpace(key.TableWhitelist)
            ? null
            : ImmutableHashSet.CreateRange(
                StringComparer.OrdinalIgnoreCase,
                key.TableWhitelist.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        if (!SameTables(scope.AllowedTables, tables))
            throw Denied("MCP table authorization differs from the authenticated request.");

        var db = await adminContext.DbManagement.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == scope.DbManagementId, cancellationToken)
            ?? throw Denied("MCP target database has been removed.");
        if (!Enum.TryParse<SqlAgentToolType>(db.SqlProvider, true, out var provider)
            || provider != scope.Provider
            || !string.Equals(db.Database?.Trim(), scope.DatabaseName, StringComparison.Ordinal))
            throw Denied("MCP target database provider or identity has changed.");

        if (customToolId.HasValue != customToolRevisionId.HasValue)
            throw Denied("Custom DML tool revision is not bound.");
        if (customToolId.HasValue)
        {
            var tool = await adminContext.CustomSqlTools.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == customToolId.Value, cancellationToken)
                ?? throw Denied("Custom DML tool was removed.");
            if (tool.DbManagementId != scope.DbManagementId
                || !string.Equals(tool.Name, scope.ToolName, StringComparison.Ordinal)
                || !string.Equals(tool.Status, "Published", StringComparison.OrdinalIgnoreCase)
                || tool.PublishedRevisionId != customToolRevisionId)
                throw Denied("Custom DML tool was unpublished or changed revision.");
        }

        // Rebuild with the same service/credentials as McpAccessKeyAuthMiddleware:
        // an approved SQL statement must never commit against an old DB connection
        // configuration after the target has been changed.
        var password = crypto.DecryptText(db.PasswordHash ?? string.Empty, _secret);
        var connection = await dbSetter.BuildDbConnectionAsync(new BuildDbConnectionModel
        {
            Provider = db.SqlProvider,
            Host = db.Host,
            Port = db.Port,
            Database = db.Database,
            Username = db.Username,
            Password = password,
            ExtraSettings = db.ExtraSettings
        }, cancellationToken);
        if (!string.Equals(connection, originalConnectionString, StringComparison.Ordinal))
            throw Denied("MCP target database connection configuration changed.");

        // Includes all authorization/connection fields, even changes which have
        // equivalent effective permissions. Re-issuing an approval is safer.
        var material = JsonSerializer.Serialize(new
        {
            KeyId = key.Id, key.KeyHash, key.IsActive, key.RevokedAt, key.ExpiresAt,
            key.DbManagementId, key.AllowedTools, key.TableWhitelist, key.AllowedQueryColumns,
            DatabaseId = db.Id, db.SqlProvider, db.Database, db.Host, db.Port, db.Username,
            db.PasswordHash, db.ExtraSettings, db.UpdatedAt,
            customToolId, customToolRevisionId,
            ToolName = scope.ToolName
        });
        var fingerprint = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(material)));
        return new McpDmlCommitAuthorizationSnapshot(fingerprint, tables);
    }

    private static bool SameTables(IReadOnlySet<string>? first, IReadOnlySet<string>? second) =>
        first is null ? second is null
        : second is not null && first.Count == second.Count && first.All(second.Contains);

    private static UnauthorizedAccessException Denied(string message) => new(message);
}
