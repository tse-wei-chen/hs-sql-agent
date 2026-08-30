using HsSqlAgent.SqlCore;
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
        string sql,
        SqlAgentToolType sourceDialect,
        SecurityPolicyModel policy,
        IReadOnlySet<string>? allowedTables,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Server-side SELECT execution boundary. SQL text enters the F# compiler facade directly; callers
/// cannot construct or mutate a compatibility AST to bypass binding, validation, policy, or rendering.
/// </summary>
public sealed class TypedQueryRuntime : ITypedQueryRuntime
{
    public CompiledSqlCommand Compile(
        ISqlProvider provider,
        string sql,
        SqlAgentToolType sourceDialect,
        SecurityPolicyModel policy,
        IReadOnlySet<string>? allowedTables) =>
        Compile(provider, sql, sourceDialect, policy, allowedTables, targetProfile: null);

    internal CompiledSqlCommand Compile(
        ISqlProvider provider,
        string sql,
        SqlAgentToolType sourceDialect,
        SecurityPolicyModel policy,
        IReadOnlySet<string>? allowedTables,
        SqlProviderCapabilityProfile? targetProfile)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        ArgumentNullException.ThrowIfNull(policy);

        var validationContext = new SqlPlanValidationContext(
            ComputePolicyVersion(policy, allowedTables),
            allowedTables);
        var executionPolicy = new SqlExecutionPlanPolicy(policy.QueryMaxRows);

        return targetProfile is null
            ? SqlCoreFacade.CompileQuery(
                sql,
                sourceDialect,
                provider.Type,
                validationContext,
                executionPolicy)
            : SqlCoreFacade.CompileQuery(
                sql,
                sourceDialect,
                provider.Type,
                validationContext,
                executionPolicy,
                targetProfile);
    }

    public async Task<QueryExecutionResult> ExecuteAsync(
        ISqlProvider provider,
        string connectionString,
        string sql,
        SqlAgentToolType sourceDialect,
        SecurityPolicyModel policy,
        IReadOnlySet<string>? allowedTables,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        try
        {
            await using var connection = provider.Connections.Create(connectionString);
            await connection.OpenAsync(cancellationToken);
            var verifiedProfile = RuntimeServerProfileVerifier.Capture(provider.Type, connection);
            var command = Compile(
                provider,
                sql,
                sourceDialect,
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
