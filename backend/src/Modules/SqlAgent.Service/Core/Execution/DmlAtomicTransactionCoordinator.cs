using System.Collections.Immutable;
using System.Data.Common;
using System.Runtime.ExceptionServices;
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
    IDmlTransactionIsolationPolicy? transactionIsolationPolicy = null,
    IDmlSchemaSnapshotFreshnessVerifier? schemaFreshnessVerifier = null,
    IDmlTargetCommitWitness? targetCommitWitness = null,
    IDmlTargetExecutionFence? targetExecutionFence = null)
{
    private readonly IDbConnectionFactory _connectionFactory = connectionFactory;
    private readonly IDmlSchemaSnapshotFreshnessVerifier? _schemaFreshnessVerifier = schemaFreshnessVerifier;
    private readonly IDmlTargetCommitWitness? _targetCommitWitness = targetCommitWitness;
    private readonly IDmlTargetExecutionFence? _targetExecutionFence = targetExecutionFence;
    private readonly IDmlTransactionIsolationPolicy _transactionIsolationPolicy =
        transactionIsolationPolicy ?? new StrictDmlTransactionIsolationPolicy();

    public async Task<DmlCommitResult> CommitAsync(
        string connectionString,
        IReadOnlyList<ValidatedDmlPlan> plans,
        IReadOnlyList<DmlPreview> approvedPreviews,
        CancellationToken cancellationToken,
        string expectedServerVersionIdentity,
        DmlTargetCommitWitnessEvidence? witnessEvidence = null,
        bool requireExecutionFence = false)
    {
        if (requireExecutionFence && (witnessEvidence is null || _targetExecutionFence is null))
            throw new InvalidOperationException("Versioned target fencing requires both durable evidence and a configured admission writer.");
        if (witnessEvidence is not null && _targetCommitWitness is null)
            throw new InvalidOperationException("Target-enlisted DML witness was requested but is not configured.");
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(approvedPreviews);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedServerVersionIdentity);
        if (plans.Count == 0 || plans.Count != approvedPreviews.Count)
            throw new InvalidOperationException("Atomic DML transaction requires matching non-empty plan and preview sets.");

        // Shared plan rules, plus child approval binding, before touching the DB.
        for (var index = 0; index < plans.Count; index++)
        {
            DmlCoordinator.ValidatePlan(plans[index]);
            ValidateChildPreview(plans[index], approvedPreviews[index]);
        }

        var provider = plans[0].MutationCommand.TargetProvider;
        if (plans.Any(x => x.MutationCommand.TargetProvider != provider))
            throw new InvalidOperationException("Atomic DML transaction cannot span providers.");

        DmlCommitResult? acknowledgedCommit = null;
        ExceptionDispatchInfo? primaryFailure = null;
        try
        {
        await using var connection = _connectionFactory.Create(connectionString);
        await connection.OpenAsync(cancellationToken);
        RuntimeServerProfileVerifier.EnsureMatches(connection, expectedServerVersionIdentity);
        if (requireExecutionFence)
        {
            // An opt-in target fence must prove its actual schema, unique
            // keys, witness and transactional rollback on the same physical
            // DML identity before starting the approved mutation. A manually
            // run, possibly stale deployment probe is not sufficient.
            if (_targetExecutionFence is not RelationalDmlTargetExecutionFence
                || _targetCommitWitness is not RelationalDmlTargetCommitWitness)
                throw new InvalidOperationException(
                    "Production target-fenced DML requires the built-in verified relational " +
                    "fence and witness implementations; custom components need a separate " +
                    "audited readiness capability.");

            await new DmlTargetFenceReadinessProbe().VerifyAsync(
                connection, provider, cancellationToken);
        }

        await using var transaction = await connection.BeginTransactionAsync(
            _transactionIsolationPolicy.CommitIsolation(provider),
            cancellationToken);

        var totalAffected = 0;
        var returnedRows = ImmutableArray.CreateBuilder<IReadOnlyDictionary<string, object?>>();
        try
        {
            if (requireExecutionFence)
                await _targetExecutionFence!.AdmitAsync(
                    connection, transaction, provider, witnessEvidence!, cancellationToken);

            for (var index = 0; index < plans.Count; index++)
            {
                var plan = plans[index];
                var approved = approvedPreviews[index];

                // Revalidate each proof after earlier statements in the same atomic
                // transaction. A changed or unreadable schema aborts the whole batch.
                await DmlSchemaSnapshotFreshness.EnsureFreshAsync(
                    connection, transaction, plan.MutationCommand, plan.SchemaSnapshot,
                    _schemaFreshnessVerifier, cancellationToken);

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

            if (witnessEvidence is not null)
                await _targetCommitWitness!.WriteAsync(connection, transaction, provider,
                    witnessEvidence, cancellationToken);
            await DmlTransactionFailureRecovery.CommitAsync(transaction, cancellationToken);
            acknowledgedCommit = new DmlCommitResult(
                true,
                totalAffected,
                "DML transaction committed atomically after per-statement approval revalidation.")
            {
                ReturnedRows = returnedRows.ToImmutable()
            };
            return acknowledgedCommit;
        }
        catch (Exception failure)
        {
            await DmlTransactionFailureRecovery.RollbackWithoutMaskingAsync(transaction, failure);
            try
            {
                DmlTransactionFailureRecovery.ThrowIfRollbackUnconfirmed(failure);
            }
            catch (DmlRollbackOutcomeUnknownException unconfirmedRollback)
            {
                primaryFailure = ExceptionDispatchInfo.Capture(unconfirmedRollback);
                throw;
            }

            primaryFailure = ExceptionDispatchInfo.Capture(failure);
            throw;
        }
        }
        catch (Exception) when (acknowledgedCommit is not null)
        {
            // The physical target COMMIT already returned successfully.
            // Cleanup errors must not overwrite that confirmed result.
            return acknowledgedCommit;
        }
        catch (Exception cleanupFailure)
            when (primaryFailure is not null &&
                  !ReferenceEquals(cleanupFailure, primaryFailure.SourceException))
        {
            // Preserve the primary transaction failure across cleanup errors.
            primaryFailure.Throw();
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

    private static void ValidateChildPreview(ValidatedDmlPlan plan, DmlPreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        var challenge = preview.Challenge
            ?? throw new InvalidOperationException("Atomic DML statement preview has no approval challenge.");

        if (preview.Operation != plan.Operation
            || !string.Equals(preview.TableName, plan.TableName, StringComparison.Ordinal)
            || !string.Equals(challenge.PlanFingerprint, plan.PlanFingerprint, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(challenge.ApprovalPlanBinding)
            || !string.Equals(challenge.ApprovalPlanBinding,
                DmlApprovalPlanBinding.Compute(plan), StringComparison.Ordinal)
            || !string.Equals(challenge.PolicyVersion, plan.PolicyVersion, StringComparison.Ordinal)
            || challenge.AffectedRows != preview.AffectedRows
            || challenge.AffectedRows < 0
            || string.IsNullOrWhiteSpace(challenge.Nonce))
            throw new InvalidOperationException(
                "Atomic DML statement preview and approval challenge do not match its compiled plan.");

        if (plan.ApprovalMode == DmlApprovalMode.InsertValues
            && (challenge.RowSetFingerprint is not null
                || challenge.AffectedRows != plan.InsertRows.Length))
            throw new InvalidOperationException(
                "Atomic INSERT statement approval does not match the immutable approved payload.");

        if (plan.ApprovalMode != DmlApprovalMode.InsertValues
            && plan.RowIdentityAssurance == DmlRowIdentityAssurance.Strict
            && string.IsNullOrWhiteSpace(challenge.RowSetFingerprint))
            throw new InvalidOperationException(
                "Atomic row-set DML approval requires a valid row identity fingerprint.");
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
