using System.Security.Cryptography;
using System.Text;
using Admin.Service.Models;
using SqlAgent.Service.Core.Compilation;
using SqlAgent.Service.Core.Execution;
using SqlAgent.Service.Core.Pipeline;
using SqlAgent.Service.Core.Providers;

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
        IReadOnlySet<string>? allowedTables)
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
            new SqlExecutionPlanPolicy(policy.QueryMaxRows));
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

        var command = Compile(provider, parsed, policy, allowedTables);
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
