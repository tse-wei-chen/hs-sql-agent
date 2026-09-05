using System.Collections.Immutable;
using System.Data.Common;
using Dapper;

namespace SqlAgent.Service.Core.Execution;

/// <summary>
/// Internal commit primitive for an already-approved ordered DML plan set. The public/server-facing
/// approval boundary is TypedDmlRuntime; this coordinator must not become a public challenge bypass.
/// UPDATE/DELETE row identities are re-queried immediately before each mutation, after earlier
/// mutations in the same transaction. If an earlier mutation changes a later approved row set, the
/// whole transaction is rolled back rather than silently widening or changing the approved impact.
/// </summary>
internal sealed class DmlAtomicTransactionCoordinator(
    IDbConnectionFactory connectionFactory,
    IDmlTransactionIsolationPolicy? transactionIsolationPolicy = null)
{
    private readonly IDbConnectionFactory _connectionFactory = connectionFactory;
    private readonly IDmlTransactionIsolationPolicy _transactionIsolationPolicy =
        transactionIsolationPolicy ?? new StrictDmlTransactionIsolationPolicy();

    public async Task<DmlCommitResult> CommitAsync(
        string connectionString,
        IReadOnlyList<ValidatedDmlPlan> plans,
        IReadOnlyList<DmlPreview> approvedPreviews,
        CancellationToken cancellationToken,
        string expectedServerVersionIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(approvedPreviews);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedServerVersionIdentity);
        if (plans.Count == 0 || plans.Count != approvedPreviews.Count)
            throw new InvalidOperationException("Atomic DML transaction requires matching non-empty plan and preview sets.");

        foreach (var plan in plans)
            ValidatePlan(plan);

        var provider = plans[0].MutationCommand.TargetProvider;
        if (plans.Any(x => x.MutationCommand.TargetProvider != provider))
            throw new InvalidOperationException("Atomic DML transaction cannot span providers.");

        await using var connection = _connectionFactory.Create(connectionString);
        await connection.OpenAsync(cancellationToken);
        RuntimeServerProfileVerifier.EnsureMatches(connection, expectedServerVersionIdentity);
        await using var transaction = await connection.BeginTransactionAsync(
            _transactionIsolationPolicy.CommitIsolation(provider),
            cancellationToken);

        var totalAffected = 0;
        var returnedRows = ImmutableArray.CreateBuilder<IReadOnlyDictionary<string, object?>>();
        try
        {
            for (var index = 0; index < plans.Count; index++)
            {
                var plan = plans[index];
                var approved = approvedPreviews[index];

                if (approved.AffectedRows < 0)
                    throw new InvalidOperationException("Approved DML affected row count cannot be negative.");

                if (plan.ApprovalMode == DmlApprovalMode.InsertValues)
                {
                    if (approved.AffectedRows != plan.InsertRows.Length)
                        return await RollbackAsync(transaction, index,
                            "approved INSERT payload row count no longer matches the immutable plan", cancellationToken);
                }
                else
                {
                    var matchCommand = plan.MatchQueryCommand
                        ?? throw new InvalidOperationException("Row-set DML approval requires a match query command.");
                    var currentRows = await QueryRowsAsync(connection, transaction, matchCommand, cancellationToken);
                    if (plan.MaxAffectedRows > 0 && currentRows.Count > plan.MaxAffectedRows)
                        return await RollbackAsync(transaction, index,
                            $"current affected row count {currentRows.Count} exceeds maximum {plan.MaxAffectedRows}", cancellationToken);

                    var currentFingerprint = ComputeRowSetFingerprint(plan, currentRows);
                    if (currentRows.Count != approved.AffectedRows
                        || !string.Equals(currentFingerprint, approved.Challenge.RowSetFingerprint, StringComparison.Ordinal))
                    {
                        return await RollbackAsync(transaction, index,
                            "matched row set changed after approval", cancellationToken);
                    }
                }

                var execution = await ExecuteMutationAsync(
                    connection,
                    transaction,
                    plan.MutationCommand,
                    cancellationToken);
                if (execution.AffectedRows != approved.AffectedRows)
                    return await RollbackAsync(transaction, index,
                        $"affected row count changed after revalidation (approved={approved.AffectedRows}, executed={execution.AffectedRows})",
                        cancellationToken);

                totalAffected = checked(totalAffected + execution.AffectedRows);
                returnedRows.AddRange(execution.ReturnedRows);
            }

            await transaction.CommitAsync(cancellationToken);
            return new DmlCommitResult(
                true,
                totalAffected,
                "DML transaction committed atomically after per-statement approval revalidation.")
            {
                ReturnedRows = returnedRows.ToImmutable()
            };
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static async Task<DmlCommitResult> RollbackAsync(
        DbTransaction transaction,
        int zeroBasedIndex,
        string reason,
        CancellationToken cancellationToken)
    {
        await transaction.RollbackAsync(cancellationToken);
        return new DmlCommitResult(
            false,
            0,
            $"DML transaction cancelled at statement {zeroBasedIndex + 1}: {reason}. Entire transaction rolled back.");
    }

    private static void ValidatePlan(ValidatedDmlPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(plan.TableName);
        ArgumentException.ThrowIfNullOrWhiteSpace(plan.PolicyVersion);
        if (plan.MutationCommand.Kind is not (
            SqlStatementKind.Insert or SqlStatementKind.Update or SqlStatementKind.Delete))
            throw new InvalidOperationException($"Invalid DML mutation command kind {plan.MutationCommand.Kind}.");

        var expectedFingerprint = DmlFingerprintService.ComputePlanFingerprint(
            plan.MutationCommand,
            plan.PolicyVersion);
        if (!string.Equals(expectedFingerprint, plan.PlanFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("Validated DML plan fingerprint does not match its mutation command.");

        if (plan.ApprovalMode == DmlApprovalMode.InsertValues)
        {
            if (plan.InsertRows.IsDefaultOrEmpty)
                throw new InvalidOperationException("INSERT VALUES approval requires immutable preview rows.");
            return;
        }

        if (plan.MatchQueryCommand is null || plan.MatchQueryCommand.Kind != SqlStatementKind.Select)
            throw new InvalidOperationException("Row-set DML approval requires a SELECT match command.");
        if (plan.RowIdentityAssurance == DmlRowIdentityAssurance.Strict && plan.RowIdentityColumns.IsDefaultOrEmpty)
            throw new InvalidOperationException("Strict DML approval requires row identity columns.");
    }

    private static string? ComputeRowSetFingerprint(
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

    private static object? GetRequiredValue(IReadOnlyDictionary<string, object?> row, string column)
    {
        foreach (var pair in row)
            if (string.Equals(pair.Key, column, StringComparison.OrdinalIgnoreCase))
                return pair.Value;
        throw new InvalidOperationException(
            $"DML match query did not return required row identity column '{column}'.");
    }

    private static async Task<List<IReadOnlyDictionary<string, object?>>> QueryRowsAsync(
        DbConnection connection,
        DbTransaction transaction,
        CompiledSqlCommand command,
        CancellationToken cancellationToken)
    {
        var result = await connection.QueryAsync(new CommandDefinition(
            command.Sql,
            BuildParameters(command),
            transaction,
            cancellationToken: cancellationToken));
        return result.Select(ToReadOnlyRow).ToList();
    }

    private static async Task<MutationExecutionResult> ExecuteMutationAsync(
        DbConnection connection,
        DbTransaction transaction,
        CompiledSqlCommand command,
        CancellationToken cancellationToken)
    {
        if (!command.ReturnsRows)
        {
            var affected = await connection.ExecuteAsync(new CommandDefinition(
                command.Sql,
                BuildParameters(command),
                transaction,
                cancellationToken: cancellationToken));
            return new MutationExecutionResult(
                affected,
                ImmutableArray<IReadOnlyDictionary<string, object?>>.Empty);
        }

        var rows = await QueryRowsAsync(connection, transaction, command, cancellationToken);
        return new MutationExecutionResult(rows.Count, rows.ToImmutableArray());
    }

    private static DynamicParameters BuildParameters(CompiledSqlCommand command)
    {
        var parameters = new DynamicParameters();
        foreach (var parameter in command.Parameters)
            parameters.Add(NormalizeParameterName(parameter.Name), parameter.Value);
        return parameters;
    }

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

    private sealed record MutationExecutionResult(
        int AffectedRows,
        ImmutableArray<IReadOnlyDictionary<string, object?>> ReturnedRows);
}
