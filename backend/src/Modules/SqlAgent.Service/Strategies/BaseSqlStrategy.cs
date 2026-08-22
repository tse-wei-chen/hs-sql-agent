using System.Data.Common;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using SqlAgent.Service.Core.Compilation;
using SqlAgent.Service.Core.Execution;
using SqlAgent.Service.Core.Pipeline;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Interfaces;
using SqlAgent.Service.Models;
using SqlAgent.Service.Services;
using SqlAgent.Service.SqlTranslation.Context;
using SqlAgent.Service.SqlTranslation.Diagnostics;
using SqlAgent.Service.Strategies.Adapters;

namespace SqlAgent.Service.Strategies;

/// <summary>
/// Transitional provider strategy base. SQL parsing, translation, policy rewriting and lowering
/// belong to the Core pipeline; strategy subclasses retain only provider connection/metadata and
/// error-formatting responsibilities while callers migrate to ISqlProvider.
/// </summary>
public abstract class BaseSqlStrategy(
    IQueryValueParserService valueParser,
    IConfiguration configuration) : ISqlStrategy
{
    static BaseSqlStrategy()
    {
        DapperTemporalTypeHandlerRegistry.EnsureRegistered();
    }

    // Kept in the constructor contract until provider registrations stop constructing strategies
    // directly. Translation no longer consumes either dependency from this base class.
    private readonly IQueryValueParserService _valueParser = valueParser;
    protected readonly IConfiguration _configuration = configuration;

    public abstract SqlAgentToolType DbType { get; }
    public abstract string BuildConnectionString(BuildDbConnectionModelBase model);
    public abstract DbConnection CreateConnection(string? connectionString);

    public abstract Task<List<string>> GetSchemasAsync(
        string connectionString,
        CancellationToken cancellationToken = default);

    public abstract Task<List<string>> GetTablesAsync(
        string connectionString,
        string schemaName,
        CancellationToken cancellationToken = default);

    public abstract Task<List<ColumnInfo>> GetColumnsAsync(
        string connectionString,
        string schemaName,
        string tableName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Compatibility compiler for tests and remaining callers during the strangler migration.
    /// It delegates to the canonical Core pipeline and contains no legacy recursive translator or
    /// ambient translation state.
    /// </summary>
    [Obsolete("Use CoreSqlCompiler or ITypedQueryRuntime. This compatibility API will be removed.")]
    public string CompileQuerySql(QueryDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return CompileCore(definition, new SqlExecutionPolicy()).Sql;
    }

    /// <summary>
    /// Compatibility translation surface. Diagnostic passthrough policy belonged to the removed
    /// legacy translator; Core compilation is fail-closed and currently exposes no warning-mode
    /// translation contract.
    /// </summary>
    [Obsolete("Use CoreSqlCompiler with explicit source and target dialects.")]
    public SqlTranslationResult CompileQueryTranslation(
        QueryDefinition definition,
        SqlAgentToolType sourceDialect,
        UnknownFunctionPolicy unknownFunctionPolicy = UnknownFunctionPolicy.Throw)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (unknownFunctionPolicy != UnknownFunctionPolicy.Throw)
        {
            throw new NotSupportedException(
                "Legacy warning/passthrough translation policy was removed. Core translation is fail-closed.");
        }

        var command = CoreSqlCompiler.CreateDefault().Compile(
            definition,
            sourceDialect,
            DbType,
            new SqlPlanValidationContext("legacy-strategy-compat"),
            new SqlExecutionPlanPolicy());
        return new SqlTranslationResult(command.Sql, []);
    }

    [Obsolete("Use ITypedQueryRuntime. This compatibility API delegates to the Core pipeline.")]
    public Task<string> ExecuteQueryAsync(
        QueryDefinition definition,
        string? connectionString = null,
        CancellationToken cancellationToken = default) =>
        ExecuteQueryAsync(
            definition,
            connectionString,
            new SqlExecutionPolicy { QueryTimeoutSeconds = 30 },
            cancellationToken);

    [Obsolete("Use ITypedQueryRuntime. This compatibility API delegates to the Core pipeline.")]
    public async Task<string> ExecuteQueryAsync(
        QueryDefinition definition,
        string? connectionString,
        SqlExecutionPolicy policy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var command = CompileCore(definition, policy);
        var provider = new LegacySqlProviderAdapter(this);
        var executor = new CompiledSqlCommandExecutor(provider.Connections);

        try
        {
            var execution = await executor.ExecuteQueryAsync(
                command,
                connectionString,
                policy.QueryTimeoutSeconds,
                cancellationToken);
            return JsonSerializer.Serialize(execution.Rows);
        }
        catch (Exception ex)
        {
            throw new Exception(BuildExecutionErrorMessage(ex, "Query"), ex);
        }
    }

    [Obsolete("Legacy string-token DML approval was removed. Use TypedDmlRuntime/TypedDmlApprovalFlow.")]
    public Task<string> ExecuteDmlAsync(
        string? connectionString = null,
        DmlDefinition? dml = null,
        CancellationToken cancellationToken = default) =>
        ExecuteDmlAsync(
            connectionString,
            dml,
            new SqlExecutionPolicy { QueryTimeoutSeconds = 30 },
            cancellationToken);

    [Obsolete("Legacy string-token DML approval was removed. Use TypedDmlRuntime/TypedDmlApprovalFlow.")]
    public Task<string> ExecuteDmlAsync(
        string? connectionString,
        DmlDefinition? dml,
        SqlExecutionPolicy policy,
        CancellationToken cancellationToken = default) =>
        Task.FromException<string>(new NotSupportedException(
            "Legacy string-token DML execution has been removed. " +
            "Use TypedDmlRuntime with typed preview/challenge/commit and revalidation."));

    protected abstract string BuildExecutionErrorMessage(Exception ex, string type);

    private CompiledSqlCommand CompileCore(
        QueryDefinition definition,
        SqlExecutionPolicy policy) =>
        CoreSqlCompiler.CreateDefault().Compile(
            definition,
            definition.SourceDialect ?? DbType,
            DbType,
            new SqlPlanValidationContext("legacy-strategy-compat"),
            new SqlExecutionPlanPolicy(policy.QueryMaxRows));
}
