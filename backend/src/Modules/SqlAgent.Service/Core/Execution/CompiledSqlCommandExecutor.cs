using System.Data;
using System.Data.Common;
using System.Diagnostics;
using Dapper;
using SqlAgent.Service.Services;

namespace SqlAgent.Service.Core.Execution;

/// <summary>
/// Execution boundary for SELECT commands. It knows nothing about QueryDefinition, Core AST,
/// translation registries or SqlKata; it executes exactly the immutable command it receives on an
/// already-open connection whose runtime profile has been verified by the caller.
/// </summary>
public sealed class CompiledSqlCommandExecutor : ISqlCommandExecutor
{
    static CompiledSqlCommandExecutor()
    {
        DapperTemporalTypeHandlerRegistry.EnsureRegistered();
    }

    public async Task<QueryExecutionResult> ExecuteQueryAsync(
        CompiledSqlCommand command,
        DbConnection openConnection,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(openConnection);

        if (command.Kind != SqlStatementKind.Select)
            throw new InvalidOperationException(
                $"Query executor cannot execute command kind {command.Kind}.");
        if (openConnection.State != ConnectionState.Open)
            throw new InvalidOperationException(
                "Query executor requires an already-open database connection.");

        var parameters = new DynamicParameters();
        foreach (var parameter in command.Parameters)
            parameters.Add(NormalizeParameterName(parameter.Name), parameter.Value);

        var stopwatch = Stopwatch.StartNew();
        var rows = await openConnection.QueryAsync(
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