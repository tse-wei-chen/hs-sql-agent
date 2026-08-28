using System.Security.Cryptography;
using System.Text;
using Admin.Service.Models;
using SqlAgent.Service.Core.Execution;

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
            var verifiedProfile = RuntimeServerProfileVerifier.Capture(provider.Type, connection);
            var command = Compile(
                provider,
                parsed,
                policy,
                allowedTables,
                verifiedProfile.TargetProfile);
            var executor = new CompiledSqlCommandExecutor();
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
        System.Data.Common.DbConnection openConnection) =>
        RuntimeServerProfileVerifier.Capture(provider, openConnection).TargetProfile;

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