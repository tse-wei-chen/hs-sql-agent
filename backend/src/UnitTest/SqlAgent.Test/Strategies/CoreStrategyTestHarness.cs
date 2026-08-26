using System.Data.Common;
using System.Text.Json;
using SqlAgent.Service.Core.Execution;

namespace SqlAgent.Test.Strategies;

/// <summary>
/// Provider integration harness that exercises the canonical Core query pipeline against the
/// provider implementation directly. ISqlStrategy now inherits ISqlProvider, so the historical
/// registration type carries the Core runtime contract at compile time with no cast or adapter.
/// </summary>
public sealed class CoreStrategyTestHarness<TStrategy>
    where TStrategy : ISqlStrategy
{
    private readonly TStrategy _provider;
    private readonly CoreSqlCompiler _compiler = CoreSqlCompiler.CreateDefault();
    private readonly CompiledSqlCommandExecutor _executor;

    public CoreStrategyTestHarness(TStrategy provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _executor = new CompiledSqlCommandExecutor(_provider.Connections);
    }

    public SqlAgentToolType DbType => _provider.Type;

    public string BuildConnectionString(BuildDbConnectionModelBase model) =>
        _provider.BuildConnectionString(model);

    public DbConnection CreateConnection(string connectionString) =>
        _provider.CreateConnection(connectionString);

    public Task<List<string>> GetSchemasAsync(
        string connectionString,
        CancellationToken cancellationToken = default) =>
        _provider.GetSchemasAsync(connectionString, cancellationToken);

    public Task<List<string>> GetTablesAsync(
        string connectionString,
        string schemaName,
        CancellationToken cancellationToken = default) =>
        _provider.GetTablesAsync(connectionString, schemaName, cancellationToken);

    public Task<List<ColumnInfo>> GetColumnsAsync(
        string connectionString,
        string schemaName,
        string tableName,
        CancellationToken cancellationToken = default) =>
        _provider.GetColumnsAsync(connectionString, schemaName, tableName, cancellationToken);

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
