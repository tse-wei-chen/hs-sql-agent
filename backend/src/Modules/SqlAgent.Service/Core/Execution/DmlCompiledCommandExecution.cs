using System.Collections.Immutable;
using System.Data.Common;
using Dapper;

namespace SqlAgent.Service.Core.Execution;

/// <summary>
/// One shared implementation of compiled DML parameter binding, row identity
/// fingerprinting, Query rows and mutation execution. Both approved single
/// statements and atomic batches consume these exact primitives; approval,
/// schema freshness, transaction and commit semantics remain separate.
/// </summary>
internal static class DmlCompiledCommandExecution
{
    internal static string? ComputeRowSetFingerprint(
        ValidatedDmlPlan plan,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        if (plan.RowIdentityAssurance == DmlRowIdentityAssurance.CountOnly)
            return null;

        var keys = rows.Select(row =>
            (IReadOnlyList<object?>)plan.RowIdentityColumns
                .Select(column => GetRequiredValue(row, column))
                .ToArray());
        return DmlFingerprintService.ComputeUnorderedRowSetFingerprint(keys);
    }

    internal static object? GetRequiredValue(
        IReadOnlyDictionary<string, object?> row,
        string column)
    {
        foreach (var pair in row)
        {
            if (string.Equals(pair.Key, column, StringComparison.OrdinalIgnoreCase))
                return pair.Value;
        }
        throw new InvalidOperationException(
            $"DML match query did not return required row identity column '{column}'.");
    }

    internal static async Task<List<IReadOnlyDictionary<string, object?>>> QueryRowsAsync(
        DbConnection connection,
        DbTransaction transaction,
        CompiledSqlCommand command,
        CancellationToken cancellationToken)
    {
        var parameters = BuildParameters(command);
        var result = await connection.QueryAsync(
            new CommandDefinition(
                command.Sql,
                parameters,
                transaction,
                cancellationToken: cancellationToken));

        return result.Select(ToReadOnlyRow).ToList();
    }

    internal static async Task<MutationExecutionResult> ExecuteMutationAsync(
        DbConnection connection,
        DbTransaction transaction,
        CompiledSqlCommand command,
        CancellationToken cancellationToken)
    {
        if (!command.ReturnsRows)
        {
            var affected = await connection.ExecuteAsync(
                new CommandDefinition(
                    command.Sql,
                    BuildParameters(command),
                    transaction,
                    cancellationToken: cancellationToken));
            return new MutationExecutionResult(
                affected,
                ImmutableArray<IReadOnlyDictionary<string, object?>>.Empty);
        }

        var rows = await QueryRowsAsync(
            connection,
            transaction,
            command,
            cancellationToken);
        return new MutationExecutionResult(
            rows.Count,
            rows.ToImmutableArray());
    }

    internal static DynamicParameters BuildParameters(CompiledSqlCommand command)
    {
        var parameters = new DynamicParameters();
        foreach (var parameter in command.Parameters)
            parameters.Add(NormalizeParameterName(parameter.Name), parameter.Value);
        return parameters;
    }

    internal static IReadOnlyDictionary<string, object?> ToReadOnlyRow(dynamic row)
    {
        if (row is IDictionary<string, object> dictionary)
            return dictionary.ToDictionary(
                pair => pair.Key,
                pair => (object?)pair.Value,
                StringComparer.OrdinalIgnoreCase);
        throw new InvalidOperationException(
            $"Unexpected query row representation '{row?.GetType().FullName ?? "null"}'.");
    }

    internal static string NormalizeParameterName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("Compiled SQL parameter name cannot be empty.");
        var trimmed = name.Trim();
        return trimmed[0] is '@' or ':' or '$' ? trimmed[1..] : trimmed;
    }

    internal sealed record MutationExecutionResult(
        int AffectedRows,
        ImmutableArray<IReadOnlyDictionary<string, object?>> ReturnedRows);
}
