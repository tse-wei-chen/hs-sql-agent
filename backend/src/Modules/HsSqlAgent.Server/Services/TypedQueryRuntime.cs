using System.Security.Cryptography;
using System.Text;
using Admin.Service.Models;
using SqlAgent.Service.Core.Compilation;
using SqlAgent.Service.Core.Execution;
using SqlAgent.Service.Core.Mapping;
using SqlAgent.Service.Core.Pipeline;
using SqlAgent.Service.Core.Providers;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Models;

namespace HsSqlAgent.Server.Services;

public interface ITypedQueryRuntime
{
    Task<QueryExecutionResult> ExecuteAsync(
        ISqlProvider provider,
        string connectionString,
        QueryDefinition definition,
        SqlAgentToolType sourceDialect,
        SecurityPolicyModel policy,
        IReadOnlySet<string>? allowedTables,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Server-side SELECT execution boundary. Callers provide an explicit provider, source dialect,
/// current security policy and table authorization. The transport DTO is mapped to an independent
/// Core ParsedStatement before entering the compiler pipeline; execution receives only the final
/// immutable command and has no dependency on legacy strategies.
/// </summary>
public sealed class TypedQueryRuntime : ITypedQueryRuntime
{
    public CompiledSqlCommand Compile(
        ISqlProvider provider,
        QueryDefinition definition,
        SqlAgentToolType sourceDialect,
        SecurityPolicyModel policy,
        IReadOnlySet<string>? allowedTables)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(policy);

        var parsed = new ParsedStatement(
            QueryDefinitionCoreMapper.Map(definition),
            sourceDialect);

        return CoreSqlCompiler.CreateDefault().Compile(
            parsed,
            provider.Type,
            new SqlPlanValidationContext(
                ComputePolicyVersion(policy, allowedTables),
                allowedTables),
            new SqlExecutionPlanPolicy(policy.QueryMaxRows));
    }

    public async Task<QueryExecutionResult> ExecuteAsync(
        ISqlProvider provider,
        string connectionString,
        QueryDefinition definition,
        SqlAgentToolType sourceDialect,
        SecurityPolicyModel policy,
        IReadOnlySet<string>? allowedTables,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var command = Compile(
            provider,
            definition,
            sourceDialect,
            policy,
            allowedTables);
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
