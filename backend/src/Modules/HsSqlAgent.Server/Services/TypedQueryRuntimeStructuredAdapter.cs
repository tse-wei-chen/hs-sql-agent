using Admin.Service.Models;
using SqlAgent.Service.Core.Mapping;
using SqlAgent.Service.Core.Pipeline;
using SqlAgent.Service.Core.Providers;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Models;

namespace HsSqlAgent.Server.Services;

/// <summary>
/// Temporary strangler adapter for structured DTO callers. The execution contract itself remains
/// parser-native; raw SQL production callers are migrated off this adapter in the next stage.
/// </summary>
internal static class TypedQueryRuntimeStructuredAdapter
{
    [Obsolete("Map or parse to ParsedStatement before entering ITypedQueryRuntime.")]
    public static Task<QueryExecutionResult> ExecuteAsync(
        this ITypedQueryRuntime runtime,
        ISqlProvider provider,
        string connectionString,
        QueryDefinition definition,
        SqlAgentToolType sourceDialect,
        SecurityPolicyModel policy,
        IReadOnlySet<string>? allowedTables,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(definition);
        return runtime.ExecuteAsync(
            provider,
            connectionString,
            new ParsedStatement(QueryDefinitionCoreMapper.Map(definition), sourceDialect),
            policy,
            allowedTables,
            cancellationToken);
    }
}
