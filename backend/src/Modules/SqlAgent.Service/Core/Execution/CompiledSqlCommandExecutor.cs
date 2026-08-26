using System.Diagnostics;
using Dapper;
using SqlAgent.Service.Services;

namespace SqlAgent.Service.Core.Execution;

/// <summary>
/// Execution boundary for SELECT commands. It knows nothing about QueryDefinition, Core AST,
/// translation registries or SqlKata; it executes exactly the immutable command it receives.
/// </summary>
public sealed class CompiledSqlCommandExecutor(IDbConnectionFactory connectionFactory)
    : ISqlCommandExecutor
{
    static CompiledSqlCommandExecutor()
    {
        DapperTemporalTypeHandlerRegistry.EnsureRegistered();
    }

    private readonly IDbConnectionFactory _connectionFactory = connectionFactory;

    public Task<QueryExecutionResult> ExecuteQueryAsync(
        CompiledSqlCommand command,
        string connectionString,
        CancellationToken cancellationToken = default) =>
        ExecuteQueryAsync(command, connectionString, 30, cancellationToken);

    public async Task<QueryExecutionResult> ExecuteQueryAsync(
        CompiledSqlCommand command,
        string connectionString,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        if (command.Kind != SqlStatementKind.Select)
            throw new InvalidOperationException(
                $"Query executor cannot execute command kind {command.Kind}.");

        await using var connection = _connectionFactory.Create(connectionString);
        await connection.OpenAsync(cancellationToken);

        var parameters = new DynamicParameters();
        foreach (var parameter in command.Parameters)
            parameters.Add(NormalizeParameterName(parameter.Name), parameter.Value);

        var stopwatch = Stopwatch.StartNew();
        var rows = await connection.QueryAsync(
            new CommandDefinition(
                command.Sql,
                parameters,
                commandTimeout: NormalizeTimeout(commandTimeoutSeconds),
                cancellationToken: cancellationToken));
        stopwatch.Stop();

        var materialized = rows
            .Select(ToReadOnlyRow)
            .ToArray();

        return new QueryExecutionResult(
            materialized,
            materialized.Length,
            stopwatch.Elapsed,
            []);
    }

    private static int NormalizeTimeout(int timeoutSeconds) =>
        timeoutSeconds > 0 ? timeoutSeconds : 30;

    private static IReadOnlyDictionary<string, object?> ToReadOnlyRow(dynamic row)
    {
        if (row is IDictionary<string, object> dictionary)
            return dictionary.ToDictionary(
                pair => pair.Key,
                pair => (object?)pair.Value,
                StringComparer.OrdinalIgnoreCase);

        throw new InvalidOperationException(
            $"Unexpected query row representation '{row?.GetType().FullName ?? "null"}'.");
    }

    private static string NormalizeParameterName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("Compiled SQL parameter name cannot be empty.");
        var trimmed = name.Trim();
        return trimmed[0] is '@' or ':' or '$' ? trimmed[1..] : trimmed;
    }
}
