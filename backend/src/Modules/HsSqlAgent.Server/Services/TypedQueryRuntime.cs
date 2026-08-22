using System.Security.Cryptography;
using System.Text;
using Admin.Service.Models;
using SqlAgent.Service.Core.Compilation;
using SqlAgent.Service.Core.Execution;
using SqlAgent.Service.Core.Pipeline;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Models;
using SqlAgent.Service.Strategies;
using SqlAgent.Service.Strategies.Adapters;

namespace HsSqlAgent.Server.Services;

public interface ITypedQueryRuntime
{
    Task<QueryExecutionResult> ExecuteAsync(
        ISqlStrategy strategy,
        string connectionString,
        QueryDefinition definition,
        SqlAgentToolType sourceDialect,
        SecurityPolicyModel policy,
        IReadOnlySet<string>? allowedTables,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Server-side strangler boundary for SELECT execution. Callers provide an explicit source
/// dialect plus the current security policy and table authorization. The runtime compiles through
/// the Core pipeline and executes only the resulting immutable command.
/// </summary>
public sealed class TypedQueryRuntime : ITypedQueryRuntime
{
    public CompiledSqlCommand Compile(
        ISqlStrategy strategy,
        QueryDefinition definition,
        SqlAgentToolType sourceDialect,
        SecurityPolicyModel policy,
        IReadOnlySet<string>? allowedTables)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(policy);

        var provider = new LegacySqlProviderAdapter(strategy);
        if (provider.Type != strategy.DbType)
            throw new InvalidOperationException("Query provider adapter type does not match the selected strategy.");

        return CoreSqlCompiler.CreateDefault().Compile(
            definition,
            sourceDialect,
            provider.Type,
            new SqlPlanValidationContext(
                ComputePolicyVersion(policy, allowedTables),
                allowedTables),
            new SqlExecutionPlanPolicy(policy.QueryMaxRows));
    }

    public async Task<QueryExecutionResult> ExecuteAsync(
        ISqlStrategy strategy,
        string connectionString,
        QueryDefinition definition,
        SqlAgentToolType sourceDialect,
        SecurityPolicyModel policy,
        IReadOnlySet<string>? allowedTables,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var command = Compile(
            strategy,
            definition,
            sourceDialect,
            policy,
            allowedTables);
        var provider = new LegacySqlProviderAdapter(strategy);
        var executor = new CompiledSqlCommandExecutor(provider.Connections);
        try
        {
            return await executor.ExecuteQueryAsync(
                command,
                connectionString,
                policy.QueryTimeoutSeconds,
                cancellationToken);
        }
        catch (Exception ex)
        {
            throw provider.Errors.Map(ex, "query");
        }
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
