using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Admin.Service.Models;

namespace HsSqlAgent.Server.Services;

public interface ITypedQueryRuntime
{
    Task<QueryExecutionResult> ExecuteAsync(
        ISqlProvider provider,
        string connectionString,
        ParsedStatement parsed,
        SecurityPolicyModel policy,
        IReadOnlySet<string>? allowedTables,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Server-side SELECT execution boundary. Callers pass a parser-native or explicitly mapped
/// <see cref="ParsedStatement"/>; compilation and execution after this boundary never depend on
/// transport DTOs or legacy strategy translators.
/// </summary>
public sealed class TypedQueryRuntime : ITypedQueryRuntime
{
    public CompiledSqlCommand Compile(
        ISqlProvider provider,
        ParsedStatement parsed,
        SecurityPolicyModel policy,
        IReadOnlySet<string>? allowedTables) =>
        Compile(provider, parsed, policy, allowedTables, targetProfile: null);

    internal CompiledSqlCommand Compile(
        ISqlProvider provider,
        ParsedStatement parsed,
        SecurityPolicyModel policy,
        IReadOnlySet<string>? allowedTables,
        SqlProviderCapabilityProfile? targetProfile)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(parsed);
        ArgumentNullException.ThrowIfNull(policy);

        return CoreSqlCompiler.CreateDefault().Compile(
            parsed,
            provider.Type,
            new SqlPlanValidationContext(
                ComputePolicyVersion(policy, allowedTables),
                allowedTables),
            new SqlExecutionPlanPolicy(policy.QueryMaxRows),
            targetProfile);
    }

    public async Task<QueryExecutionResult> ExecuteAsync(
        ISqlProvider provider,
        string connectionString,
        ParsedStatement parsed,
        SecurityPolicyModel policy,
        IReadOnlySet<string>? allowedTables,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(parsed);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        try
        {
            await using var connection = provider.Connections.Create(connectionString);
            await connection.OpenAsync(cancellationToken);
            var targetProfile = CreateVerifiedTargetProfile(provider.Type, connection);
            var command = Compile(provider, parsed, policy, allowedTables, targetProfile);
            var executor = new CompiledSqlCommandExecutor(provider.Connections);
            return await executor.ExecuteQueryAsync(
                command,
                connection,
                policy.QueryTimeoutSeconds,
                cancellationToken);
        }
        catch (Exception ex)
        {
            throw provider.Errors.Map(ex, "query");
        }
    }

    internal static SqlProviderCapabilityProfile CreateVerifiedTargetProfile(
        SqlAgentToolType provider,
        DbConnection openConnection)
    {
        ArgumentNullException.ThrowIfNull(openConnection);
        if (openConnection.State != System.Data.ConnectionState.Open)
            throw new InvalidOperationException(
                "Verified runtime capability profile requires an open database connection.");

        return new SqlProviderCapabilityProfile(
            provider,
            ServerVersion: ParseServerVersion(openConnection.ServerVersion));
    }

    private static Version? ParseServerVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        if (Version.TryParse(trimmed, out var exact)) return exact;

        var tokenLength = 0;
        while (tokenLength < trimmed.Length)
        {
            var ch = trimmed[tokenLength];
            if (!(char.IsDigit(ch) || ch == '.')) break;
            tokenLength++;
        }

        return tokenLength > 0
               && Version.TryParse(trimmed[..tokenLength].TrimEnd('.'), out var prefix)
            ? prefix
            : null;
    }

    internal static string ComputePolicyVersion(
        SecurityPolicyModel policy,
        IReadOnlySet<string>? allowedTables)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var tables = allowedTables is null
            ? string.Empty
            : string.Join(',', allowedTables.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        var material =
            $"maxRows={policy.QueryMaxRows};" +
            $"timeout={policy.QueryTimeoutSeconds};" +
            $"updatedTicks={policy.UpdatedAt?.ToUniversalTime().Ticks ?? 0L};" +
            $"tables={tables}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }
}
