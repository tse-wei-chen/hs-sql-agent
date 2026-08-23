using System.Data.Common;
using System.Text.Json;
using SqlAgent.Service.Core.Compilation;
using SqlAgent.Service.Core.Execution;
using SqlAgent.Service.Core.Mapping;
using SqlAgent.Service.Core.Pipeline;
using SqlAgent.Service.Core.Providers;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Models;
using SqlAgent.Service.Strategies;

namespace SqlAgent.Test.Strategies;

/// <summary>
/// Provider integration harness that exercises the canonical Core query pipeline against the
/// provider implementation directly. The historical strategy contract remains only as a test
/// registration detail; no strategy-to-provider adapter participates in compilation or execution.
/// </summary>
public sealed class CoreStrategyTestHarness<TStrategy>
    where TStrategy : ISqlStrategy
{
    private readonly TStrategy _strategy;
    private readonly ISqlProvider _provider;
    private readonly CoreSqlCompiler _compiler = CoreSqlCompiler.CreateDefault();
    private readonly CompiledSqlCommandExecutor _executor;

    public CoreStrategyTestHarness(TStrategy strategy)
    {
        _strategy = strategy ?? throw new ArgumentNullException(nameof(strategy));
        _provider = strategy as ISqlProvider
            ?? throw new InvalidOperationException(
                $"Provider integration fixture {strategy.GetType().FullName} must implement {nameof(ISqlProvider)} directly.");
        _executor = new CompiledSqlCommandExecutor(_provider.Connections);
    }

    public SqlAgentToolType DbType => _provider.Type;

    public string BuildConnectionString(BuildDbConnectionModelBase model) =>
        _strategy.BuildConnectionString(model);

    public DbConnection CreateConnection(string connectionString) =>
        _strategy.CreateConnection(connectionString);

    public Task<List<string>> GetSchemasAsync(
        string connectionString,
        CancellationToken cancellationToken = default) =>
        _strategy.GetSchemasAsync(connectionString, cancellationToken);

    public Task<List<string>> GetTablesAsync(
        string connectionString,
        string schemaName,
        CancellationToken cancellationToken = default) =>
        _strategy.GetTablesAsync(connectionString, schemaName, cancellationToken);

    public Task<List<ColumnInfo>> GetColumnsAsync(
        string connectionString,
        string schemaName,
        string tableName,
        CancellationToken cancellationToken = default) =>
        _strategy.GetColumnsAsync(connectionString, schemaName, tableName, cancellationToken);

    public Task<string> ExecuteQueryAsync(
        QueryDefinition definition,
        string? connectionString = null,
        CancellationToken cancellationToken = default) =>
        ExecuteQueryAsync(
            definition,
            connectionString,
            new SqlExecutionPolicy { QueryTimeoutSeconds = 30 },
            cancellationToken);

    public async Task<string> ExecuteQueryAsync(
        QueryDefinition definition,
        string? connectionString,
        SqlExecutionPolicy policy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        // Structured DTOs remain a test-input convenience only. Map them explicitly before the
        // compiler boundary so provider integration tests exercise the same typed contract as
        // production raw-SQL paths.
        var parsed = new ParsedStatement(
            QueryDefinitionCoreMapper.Map(definition),
            definition.SourceDialect ?? DbType);
        var command = _compiler.Compile(
            parsed,
            DbType,
            new SqlPlanValidationContext("provider-integration-test"),
            new SqlExecutionPlanPolicy(policy.QueryMaxRows));

        try
        {
            var execution = await _executor.ExecuteQueryAsync(
                command,
                connectionString,
                policy.QueryTimeoutSeconds,
                cancellationToken);
            return JsonSerializer.Serialize(execution.Rows);
        }
        catch (Exception ex)
        {
            throw _provider.Errors.Map(ex, "query");
        }
    }
}
