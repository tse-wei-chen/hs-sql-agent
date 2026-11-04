using System.Collections.Immutable;
using System.Data;
using System.Data.Common;
using System.Runtime.ExceptionServices;
using Dapper;

namespace SqlAgent.Service.Core.Execution;

/// <summary>
/// Typed DML approval coordinator. UPDATE/DELETE preview is read-only and commit re-queries the
/// matched row identity set before executing the exact compiled mutation. INSERT VALUES has no
/// pre-existing row set, so preview exposes the immutable payload and commit is bound to the exact
/// compiled command fingerprint plus approved payload row count. Every challenge is one-time.
/// </summary>
public sealed class DmlCoordinator(
    IDbConnectionFactory connectionFactory,
    TimeProvider? timeProvider = null,
    IDmlApprovalChallengeStore? challengeStore = null,
    IDmlTransactionIsolationPolicy? transactionIsolationPolicy = null,
    IDmlPreviewTransactionFactory? previewTransactionFactory = null,
    IDmlSchemaSnapshotFreshnessVerifier? schemaFreshnessVerifier = null,
    IDmlTargetCommitWitness? targetCommitWitness = null,
    IDmlTargetExecutionFence? targetExecutionFence = null) : IDmlCoordinator
{
    private const int PreviewRowLimit = 20;
    private readonly IDbConnectionFactory _connectionFactory = connectionFactory;
    private readonly IDmlSchemaSnapshotFreshnessVerifier? _schemaFreshnessVerifier = schemaFreshnessVerifier;
    private readonly IDmlTargetCommitWitness? _targetCommitWitness = targetCommitWitness;
    private readonly IDmlTargetExecutionFence? _targetExecutionFence = targetExecutionFence;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly IDmlApprovalChallengeStore _challengeStore =
        challengeStore ?? new InMemoryDmlApprovalChallengeStore(timeProvider);
    private readonly IDmlTransactionIsolationPolicy _transactionIsolationPolicy =
        transactionIsolationPolicy ?? new StrictDmlTransactionIsolationPolicy();
    private readonly IDmlPreviewTransactionFactory _previewTransactionFactory =
        previewTransactionFactory ?? new ProviderDmlPreviewTransactionFactory();

    [Obsolete("Verified runtime server-version identity is required for DML preview.", error: true)]
    public Task<DmlPreview> PreviewAsync(
        string connectionString,
        ValidatedDmlPlan plan,
        string approvalContextFingerprint,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(
            "Verified runtime server-version identity is required for DML preview.");

    public async Task<DmlPreview> PreviewAsync(
        string connectionString,
        ValidatedDmlPlan plan,
        string approvalContextFingerprint,
        CancellationToken cancellationToken,
        string expectedServerVersionIdentity)
    {
        ValidatePlan(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(approvalContextFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedServerVersionIdentity);

        if (plan.ApprovalMode == DmlApprovalMode.InsertValues)
            return await PreviewInsertValuesAsync(plan, approvalContextFingerprint, cancellationToken);

        if (plan.ApprovalMode is DmlApprovalMode.InsertSelect or DmlApprovalMode.InsertConflictDoNothing or DmlApprovalMode.InsertConflictDoUpdate or DmlApprovalMode.SqlServerMergeMatchedDelete or DmlApprovalMode.SqlServerMergeMatchedUpdate
            && _schemaFreshnessVerifier is not NativeDmlSchemaSnapshotService)
            throw new InvalidOperationException(
                "Native source/target DML approval requires built-in live schema verification before preview.");

        var matchCommand = RequireMatchCommand(plan);
        await using var connection = _connectionFactory.Create(connectionString);
        await connection.OpenAsync(cancellationToken);
        RuntimeServerProfileVerifier.EnsureMatches(connection, expectedServerVersionIdentity);
        await using var transaction = await _previewTransactionFactory.BeginAsync(
            connection,
            matchCommand.TargetProvider,
            plan.ApprovalMode == DmlApprovalMode.InsertSelect
                && matchCommand.TargetProvider is SqlAgentToolType.MySQL or SqlAgentToolType.MsSqlServer
                ? IsolationLevel.Serializable
                : _transactionIsolationPolicy.PreviewIsolation(matchCommand.TargetProvider),
            cancellationToken);

        if (plan.ApprovalMode is DmlApprovalMode.InsertSelect or DmlApprovalMode.InsertConflictDoNothing or DmlApprovalMode.InsertConflictDoUpdate or DmlApprovalMode.SqlServerMergeMatchedDelete or DmlApprovalMode.SqlServerMergeMatchedUpdate)
            await DmlSchemaSnapshotFreshness.EnsureFreshAsync(
                connection, transaction, plan.MutationCommand, plan.SchemaSnapshot,
                _schemaFreshnessVerifier, cancellationToken);

        var rows = await QueryRowsAsync(
            connection,
            transaction,
            matchCommand,
            cancellationToken);

        await transaction.RollbackAsync(cancellationToken);

        if (plan.MaxAffectedRows > 0 && rows.Count > plan.MaxAffectedRows)
        {
            throw new UnauthorizedAccessException(
                $"Security policy denied DML: affectedRows={rows.Count} exceeds maximum {plan.MaxAffectedRows}.");
        }

        if (plan.ApprovalMode is DmlApprovalMode.InsertConflictDoNothing or DmlApprovalMode.InsertConflictDoUpdate && rows.Count > 1)
            throw new InvalidOperationException(
                "Native conflict skip target UNIQUE key resolved to multiple physical rows; preview denied.");

        // A present target PK means a guaranteed DO NOTHING with zero affected
        // rows; an absent PK means exactly one proposed insert. The query result
        // is still fingerprinted in both cases, including the empty set.
        var affectedRows = plan.ApprovalMode switch
        {
            DmlApprovalMode.InsertConflictDoNothing => 1 - rows.Count,
            DmlApprovalMode.InsertConflictDoUpdate => 1,
            _ => rows.Count
        };
        var rowSetFingerprint = ComputeRowSetFingerprint(plan, rows);
        var challenge = CreateChallenge(
            plan,
            rowSetFingerprint,
            affectedRows,
            approvalContextFingerprint);
        await _challengeStore.RegisterAsync(challenge, cancellationToken);

        return new DmlPreview(
            plan.Operation,
            plan.TableName,
            affectedRows,
            (plan.ApprovalMode == DmlApprovalMode.SqlServerMergeMatchedUpdate
                ? SqlServerMergeMatchedUpdatePreviewRows(plan, rows)
                : plan.ApprovalMode == DmlApprovalMode.InsertConflictDoUpdate
                    ? ConflictUpdatePreviewRows(plan, rows)
                    : plan.ApprovalMode == DmlApprovalMode.InsertConflictDoNothing
                    ? plan.InsertRows.Take(affectedRows)
                        .Select(row => (IReadOnlyDictionary<string, object?>)row)
                    : rows.Take(PreviewRowLimit))
                .ToImmutableArray(),
            challenge);
    }

    [Obsolete("Verified runtime server-version identity is required for DML commit.", error: true)]
    public Task<DmlCommitResult> CommitAsync(
        string connectionString,
        ValidatedDmlPlan plan,
        DmlApprovalChallenge approvedChallenge,
        string approvalContextFingerprint,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(
            "Verified runtime server-version identity is required for DML commit.");

    public Task<DmlCommitResult> CommitAsync(
        string connectionString,
        ValidatedDmlPlan plan,
        DmlApprovalChallenge approvedChallenge,
        string approvalContextFingerprint,
        CancellationToken cancellationToken,
        string expectedServerVersionIdentity) =>
        CommitAsync(connectionString, plan, approvedChallenge, approvalContextFingerprint,
            cancellationToken, expectedServerVersionIdentity, witnessEvidence: null);

    public async Task<DmlCommitResult> CommitAsync(
        string connectionString,
        ValidatedDmlPlan plan,
        DmlApprovalChallenge approvedChallenge,
        string approvalContextFingerprint,
        CancellationToken cancellationToken,
        string expectedServerVersionIdentity,
        DmlTargetCommitWitnessEvidence? witnessEvidence,
        bool requireExecutionFence = false)
    {
        if (requireExecutionFence && (witnessEvidence is null || _targetExecutionFence is null))
            throw new InvalidOperationException("Versioned target fencing requires both durable evidence and a configured admission writer.");
        if (witnessEvidence is not null && _targetCommitWitness is null)
            throw new InvalidOperationException("Target-enlisted DML witness was requested but is not configured.");
        ValidatePlan(plan);
        if (plan.ApprovalMode is DmlApprovalMode.InsertSelect or DmlApprovalMode.InsertConflictDoNothing or DmlApprovalMode.InsertConflictDoUpdate or DmlApprovalMode.SqlServerMergeMatchedDelete or DmlApprovalMode.SqlServerMergeMatchedUpdate
            && _schemaFreshnessVerifier is not NativeDmlSchemaSnapshotService)
            throw new InvalidOperationException(
                "Native DML source/target approval cannot commit without built-in schema freshness proof.");
        ArgumentException.ThrowIfNullOrWhiteSpace(approvalContextFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedServerVersionIdentity);
        ValidateChallenge(plan, approvedChallenge, approvalContextFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        if (!await _challengeStore.TryConsumeAsync(approvedChallenge, cancellationToken))
        {
            throw new InvalidOperationException(
                "DML approval challenge is unknown, modified, expired, or has already been consumed.");
        }

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
                connection, plan.MutationCommand.TargetProvider, cancellationToken);
        }

        // The native SQLite provider starts a SERIALIZABLE transaction with
        // BEGIN IMMEDIATE (not a deferred read snapshot). This acquires the
        // writer reservation before the source-row recheck. Verify that
        // provider behavior in a real SQLite concurrency test; SqlAgent.Service
        // must remain driver-free.
        var commitIsolation = plan.ApprovalMode is DmlApprovalMode.InsertSelect or DmlApprovalMode.SqlServerMergeMatchedDelete or DmlApprovalMode.SqlServerMergeMatchedUpdate
            ? IsolationLevel.Serializable
            : _transactionIsolationPolicy.CommitIsolation(plan.MutationCommand.TargetProvider);
        await using var transaction = await connection.BeginTransactionAsync(
            commitIsolation, cancellationToken);

        try
        {
            // Catalog verification must finish before durable admission.
            // Oracle may invalidate an earlier uncommitted target admission
            // while reading dictionary metadata. Revocation still races the
            // INSERT below, before any business DML is permitted.
            await DmlSchemaSnapshotFreshness.EnsureFreshAsync(
                connection, transaction, plan.MutationCommand, plan.SchemaSnapshot,
                _schemaFreshnessVerifier, cancellationToken);

            // An approval-time unique index is not a transaction-held proof.
            // Acquire SQL Server table locks and re-attest on THIS write
            // connection before even reading the approved row set.
            EnsureLiveSqlServerCteKeyCurrent(
                plan, connection, transaction, cancellationToken);

            if (requireExecutionFence)
            {
                await _targetExecutionFence!.AdmitAsync(connection, transaction,
                    plan.MutationCommand.TargetProvider, witnessEvidence!, cancellationToken);
                await RelationalDmlTargetExecutionFence.EnsureAdmittedInTransactionAsync(
                    connection, transaction, plan.MutationCommand.TargetProvider,
                    witnessEvidence!, cancellationToken);
            }

            if (plan.ApprovalMode == DmlApprovalMode.InsertValues)
            {
                var insertResult = await CommitInsertValuesAsync(
                    connection,
                    transaction,
                    plan,
                    approvedChallenge,
                    cancellationToken,
                    witnessEvidence,
                    requireExecutionFence);
                if (insertResult.Committed)
                    acknowledgedCommit = insertResult;
                return insertResult;
            }

            var matchCommand = RequireMatchCommand(plan);
            var currentRows = await QueryRowsAsync(
                connection,
                transaction,
                matchCommand,
                cancellationToken);

            if (plan.MaxAffectedRows > 0 && currentRows.Count > plan.MaxAffectedRows)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new DmlCommitResult(
                    false,
                    0,
                    $"DML execution cancelled: current affected row count {currentRows.Count} exceeds maximum {plan.MaxAffectedRows}.");
            }

            var currentFingerprint = ComputeRowSetFingerprint(plan, currentRows);

            if (plan.ApprovalMode is DmlApprovalMode.InsertConflictDoNothing or DmlApprovalMode.InsertConflictDoUpdate
                && currentRows.Count > 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new DmlCommitResult(false, 0,
                    "Native conflict skip cancelled: target key is no longer uniquely identifiable.");
            }
            var currentAffectedRows = plan.ApprovalMode switch
            {
                DmlApprovalMode.InsertConflictDoNothing => 1 - currentRows.Count,
                DmlApprovalMode.InsertConflictDoUpdate => 1,
                _ => currentRows.Count
            };
            if (currentAffectedRows != approvedChallenge.AffectedRows
                || !string.Equals(
                    currentFingerprint,
                    approvedChallenge.RowSetFingerprint,
                    StringComparison.Ordinal))
            {
                await transaction.RollbackAsync(cancellationToken);
                return new DmlCommitResult(
                    false,
                    0,
                    plan.ApprovalMode == DmlApprovalMode.InsertSelect
                        ? "INSERT SELECT execution cancelled: the source row values or count changed after approval."
                        : plan.ApprovalMode == DmlApprovalMode.InsertConflictDoNothing
                            ? "Native DO NOTHING cancelled: the target UNIQUE key conflict changed after approval."
                            : plan.ApprovalMode == DmlApprovalMode.InsertConflictDoUpdate
                                ? "PostgreSQL conflict UPDATE cancelled: the target row or conflict state changed after approval."
                                : "DML execution cancelled: the matched row set changed after approval.");
            }

            var execution = await ExecuteMutationAsync(
                connection,
                transaction,
                plan.MutationCommand,
                cancellationToken);

            if (execution.AffectedRows != approvedChallenge.AffectedRows)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new DmlCommitResult(
                    false,
                    0,
                    $"DML execution cancelled: affected row count changed after revalidation " +
                    $"(approved={approvedChallenge.AffectedRows}, executed={execution.AffectedRows}).");
            }

            await WriteWitnessAsync(connection, transaction, plan.MutationCommand.TargetProvider,
                witnessEvidence, cancellationToken);
            await DmlTransactionFailureRecovery.CommitAsync(transaction, cancellationToken);
            if (requireExecutionFence)
                await DmlTargetCommitEvidenceVerifier.EnsureConfirmedAsync(
                    connection, plan.MutationCommand.TargetProvider, witnessEvidence!,
                    _targetExecutionFence!, _targetCommitWitness!, cancellationToken);
            acknowledgedCommit = new DmlCommitResult(
                true,
                execution.AffectedRows,
                "DML operation committed after approval and row-set revalidation.")
            {
                ReturnedRows = execution.ReturnedRows
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
            // Acknowledged target COMMIT is irreversible: a later transaction
            // or connection DisposeAsync failure cannot turn it into a
            // Failed approval or cause an operator to repeat the mutation.
            return acknowledgedCommit;
        }
        catch (Exception cleanupFailure)
            when (primaryFailure is not null &&
                  !ReferenceEquals(cleanupFailure, primaryFailure.SourceException))
        {
            // An unrelated provider DisposeAsync failure cannot hide the
            // primary SQL error or typed uncertain transaction outcome.
            primaryFailure.Throw();
            throw;
        }
    }

    private static IEnumerable<IReadOnlyDictionary<string, object?>> SqlServerMergeMatchedUpdatePreviewRows(
        ValidatedDmlPlan plan,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> before)
    {
        var proof = plan.SqlServerMergeMatchedUpdateProof
            ?? throw new InvalidOperationException("SQL Server MERGE UPDATE payload proof is missing.");
        if (before.Count == 0) yield break;
        if (before.Count != 1)
            throw new InvalidOperationException("SQL Server MERGE target primary key was not unique.");
        var row = before[0];
        var result = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["__action"] = "UPDATE",
            [proof.UniqueKeyColumn] = proof.KeyValue
        };
        for (var index = 0; index < proof.UpdatedColumns.Length; index++)
        {
            var column = proof.UpdatedColumns[index];
            if (!row.TryGetValue(column, out var previous))
                throw new InvalidOperationException(
                    "SQL Server MERGE preview cannot read every physical before-value.");
            result.Add("__before_" + column, previous);
            result.Add("__after_" + column, proof.ProposedValues[index]);
        }
        yield return result;
    }

    private static IEnumerable<IReadOnlyDictionary<string, object?>> ConflictUpdatePreviewRows(
        ValidatedDmlPlan plan,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> before)
    {
        // The approved payload is the only source of proposed values. Old
        // values come exclusively from the transactionally schema-verified
        // physical SELECT, never an untrusted caller's preview DTO.
        var key = plan.RowIdentityColumns[0];
        if (before.Count > 1
            || !plan.InsertRows[0].TryGetValue(key, out var keyValue))
            throw new InvalidOperationException(
                "PostgreSQL conflict update preview has an invalid physical key/value binding.");
        var preview = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["__action"] = before.Count == 1 ? "UPDATE" : "INSERT",
            [key] = keyValue
        };
        foreach (var column in plan.RowIdentityColumns.Skip(1))
        {
            if (!plan.InsertRows[0].TryGetValue(column, out var proposed))
                throw new InvalidOperationException(
                    "PostgreSQL conflict update preview is missing an approved proposed value.");
            object? oldValue = null;
            if (before.Count == 1 && !before[0].TryGetValue(column, out oldValue))
                throw new InvalidOperationException(
                    "PostgreSQL conflict update preview could not read every physical before-value.");
            preview.Add("__before_" + column, oldValue);
            preview.Add("__after_" + column, proposed);
        }
        yield return preview;
    }

    private async Task<DmlPreview> PreviewInsertValuesAsync(
        ValidatedDmlPlan plan,
        string approvalContextFingerprint,
        CancellationToken cancellationToken)
    {
        var affectedRows = plan.InsertRows.Length;
        if (plan.MaxAffectedRows > 0 && affectedRows > plan.MaxAffectedRows)
        {
            throw new UnauthorizedAccessException(
                $"Security policy denied INSERT: rowCount={affectedRows} exceeds maximum {plan.MaxAffectedRows}.");
        }

        var challenge = CreateChallenge(
            plan,
            rowSetFingerprint: null,
            affectedRows,
            approvalContextFingerprint);
        await _challengeStore.RegisterAsync(challenge, cancellationToken);

        return new DmlPreview(
            plan.Operation,
            plan.TableName,
            affectedRows,
            plan.InsertRows
                .Take(PreviewRowLimit)
                .Select(row => (IReadOnlyDictionary<string, object?>)row)
                .ToImmutableArray(),
            challenge);
    }

    private async Task<DmlCommitResult> CommitInsertValuesAsync(
        DbConnection connection,
        DbTransaction transaction,
        ValidatedDmlPlan plan,
        DmlApprovalChallenge approvedChallenge,
        CancellationToken cancellationToken,
        DmlTargetCommitWitnessEvidence? witnessEvidence,
        bool requireExecutionFence)
    {
        var execution = await ExecuteMutationAsync(
            connection,
            transaction,
            plan.MutationCommand,
            cancellationToken);

        if (execution.AffectedRows != approvedChallenge.AffectedRows)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new DmlCommitResult(
                false,
                0,
                $"INSERT execution cancelled: approved payload row count changed " +
                $"(approved={approvedChallenge.AffectedRows}, executed={execution.AffectedRows}).");
        }

        await WriteWitnessAsync(connection, transaction, plan.MutationCommand.TargetProvider,
            witnessEvidence, cancellationToken);
        await DmlTransactionFailureRecovery.CommitAsync(transaction, cancellationToken);
        if (requireExecutionFence)
            await DmlTargetCommitEvidenceVerifier.EnsureConfirmedAsync(
                connection, plan.MutationCommand.TargetProvider, witnessEvidence!,
                _targetExecutionFence!, _targetCommitWitness!, cancellationToken);
        return new DmlCommitResult(
            true,
            execution.AffectedRows,
            "INSERT VALUES committed after exact-plan approval validation.")
        {
            ReturnedRows = execution.ReturnedRows
        };
    }

    private Task WriteWitnessAsync(
        DbConnection connection, DbTransaction transaction, SqlAgentToolType provider,
        DmlTargetCommitWitnessEvidence? evidence, CancellationToken cancellationToken) =>
        evidence is null ? Task.CompletedTask
        : _targetCommitWitness!.WriteAsync(connection, transaction, provider, evidence, cancellationToken);

    private DmlApprovalChallenge CreateChallenge(
        ValidatedDmlPlan plan,
        string? rowSetFingerprint,
        int affectedRows,
        string approvalContextFingerprint)
    {
        var now = _timeProvider.GetUtcNow();
        var ttl = plan.ApprovalTtl > TimeSpan.Zero
            ? plan.ApprovalTtl
            : TimeSpan.FromMinutes(5);
        return new DmlApprovalChallenge(
            plan.PlanFingerprint,
            rowSetFingerprint,
            affectedRows,
            plan.PolicyVersion,
            approvalContextFingerprint,
            now,
            now.Add(ttl),
            Guid.NewGuid().ToString("N"))
        {
            ApprovalPlanBinding = DmlApprovalPlanBinding.Compute(plan)
        };
    }

    private void ValidateChallenge(
        ValidatedDmlPlan plan,
        DmlApprovalChallenge challenge,
        string approvalContextFingerprint)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        var now = _timeProvider.GetUtcNow();
        if (challenge.ExpiresAt <= now)
            throw new InvalidOperationException("DML approval challenge has expired.");
        if (challenge.IssuedAt > now)
            throw new InvalidOperationException("DML approval challenge has an invalid issue time.");
        if (string.IsNullOrWhiteSpace(challenge.Nonce))
            throw new InvalidOperationException("DML approval challenge nonce is missing.");
        if (!string.Equals(plan.PolicyVersion, challenge.PolicyVersion, StringComparison.Ordinal))
            throw new InvalidOperationException("DML policy changed after approval.");
        if (!string.Equals(plan.PlanFingerprint, challenge.PlanFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("DML plan changed after approval.");
        if (string.IsNullOrWhiteSpace(challenge.ApprovalPlanBinding)
            || !string.Equals(challenge.ApprovalPlanBinding,
                DmlApprovalPlanBinding.Compute(plan), StringComparison.Ordinal))
            throw new InvalidOperationException(
                "DML approval plan binding changed after preview; request a new approval.");
        if (!string.Equals(
                challenge.ApprovalContextFingerprint,
                approvalContextFingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "DML approval execution context changed after preview; request a new preview before committing.");
        }
        if (plan.MaxAffectedRows > 0 && challenge.AffectedRows > plan.MaxAffectedRows)
            throw new InvalidOperationException("DML approval exceeds the validated maximum affected row count.");

        if (plan.ApprovalMode == DmlApprovalMode.SqlServerMergeMatchedDelete
            && challenge.AffectedRows is not (0 or 1))
            throw new InvalidOperationException(
                "SQL Server MERGE MATCHED DELETE approval must predict zero or one affected row.");
        if (plan.ApprovalMode is DmlApprovalMode.SqlServerMergeMatchedDelete or DmlApprovalMode.SqlServerMergeMatchedUpdate
            && challenge.AffectedRows is not (0 or 1))
            throw new InvalidOperationException(
                "SQL Server MERGE approval must predict zero or one affected row.");

        if (plan.ApprovalMode == DmlApprovalMode.InsertConflictDoNothing
            && challenge.AffectedRows is not (0 or 1))
            throw new InvalidOperationException(
                "SQLite conflict skip approval must predict exactly zero or one affected row.");

        if (plan.ApprovalMode == DmlApprovalMode.InsertValues)
        {
            if (challenge.RowSetFingerprint is not null)
                throw new InvalidOperationException("INSERT VALUES approval must not contain a row-set fingerprint.");
            if (challenge.AffectedRows != plan.InsertRows.Length)
                throw new InvalidOperationException("INSERT VALUES approved row count does not match the immutable payload.");
            return;
        }

        if (plan.RowIdentityAssurance == DmlRowIdentityAssurance.Strict
            && string.IsNullOrWhiteSpace(challenge.RowSetFingerprint))
        {
            throw new InvalidOperationException(
                "Strict DML approval requires a row-set fingerprint.");
        }
    }

    // Single and atomic DML must share the exact same fail-closed plan rules.
    internal static void ValidatePlan(ValidatedDmlPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(plan.TableName);
        ArgumentException.ThrowIfNullOrWhiteSpace(plan.PolicyVersion);

        if (plan.MaxAffectedRows < 0)
            throw new InvalidOperationException("DML maximum affected rows cannot be negative.");
        if (plan.MutationCommand.Kind is not (
            SqlStatementKind.Insert or SqlStatementKind.Update or SqlStatementKind.Delete or SqlStatementKind.Merge))
        {
            throw new InvalidOperationException(
                $"DML mutation command has invalid kind {plan.MutationCommand.Kind}.");
        }

        var expectedKind = plan.ApprovalMode is DmlApprovalMode.SqlServerMergeMatchedDelete or DmlApprovalMode.SqlServerMergeMatchedUpdate
            ? SqlStatementKind.Merge
            : plan.Operation switch
        {
            HsSqlAgent.SqlCore.Enums.DmlOperation.Insert => SqlStatementKind.Insert,
            HsSqlAgent.SqlCore.Enums.DmlOperation.Update => SqlStatementKind.Update,
            HsSqlAgent.SqlCore.Enums.DmlOperation.Delete => SqlStatementKind.Delete,
            _ => throw new InvalidOperationException($"Unsupported DML operation {plan.Operation}.")
        };
        if (plan.MutationCommand.Kind != expectedKind)
            throw new InvalidOperationException("DML operation does not match its compiled mutation command kind.");

        if (plan.SqlServerCteUniqueKeyAssurance is { } proof)
        {
            if (plan.MutationCommand.TargetProvider != SqlAgentToolType.MsSqlServer
                || plan.Operation is not (DmlOperation.Update or DmlOperation.Delete)
                || plan.ApprovalMode != DmlApprovalMode.RowSetMutation
                || plan.RowIdentityAssurance != DmlRowIdentityAssurance.Strict
                || plan.MaxAffectedRows <= 0
                || !string.Equals(plan.TableName, proof.TargetTable, StringComparison.Ordinal)
                || (plan.SchemaSnapshot is not null
                    && (plan.SchemaSnapshot.Provider != SqlAgentToolType.MsSqlServer
                        || !string.Equals(plan.SchemaSnapshot.Sha256, proof.SchemaSha256,
                            StringComparison.Ordinal))))
            {
                throw new InvalidOperationException(
                    "SQL Server live CTE key proof is not bound to a strict, bounded, " +
                    "same-provider UPDATE/DELETE plan and its exact physical target/snapshot.");
            }
        }

        var fingerprintPolicy = plan.SqlServerCteUniqueKeyAssurance?.TargetKeyColumn is not null
            ? plan.PolicyVersion + "|live-sqlserver-cte-exact-integer=" +
              plan.SqlServerCteUniqueKeyAssurance.ProofIdentity
            : plan.PolicyVersion;
        var expectedFingerprint = DmlFingerprintService.ComputePlanFingerprint(
            plan.MutationCommand, fingerprintPolicy);
        if (plan.SqlServerCteUniqueKeyAssurance?.TargetKeyColumn is not null
            && !string.Equals(expectedFingerprint, plan.MutationCommand.PlanFingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "SQL Server live CTE proof is not bound to the compiled command fingerprint.");
        }
        if (!string.Equals(expectedFingerprint, plan.PlanFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("Validated DML plan fingerprint does not match its mutation command.");

        if (plan.SqlServerMergeMatchedUpdateProof is not null
            && plan.ApprovalMode != DmlApprovalMode.SqlServerMergeMatchedUpdate)
            throw new InvalidOperationException(
                "SQL Server MERGE UPDATE proof cannot authorize another approval mode.");
        if (plan.SqlServerMergeMatchedDeleteProof is not null
            && plan.ApprovalMode != DmlApprovalMode.SqlServerMergeMatchedDelete)
            throw new InvalidOperationException(
                "SQL Server MERGE live proof cannot authorize another DML operation.");
        if (plan.NativeConflictDoNothingProof is not null
            && plan.ApprovalMode != DmlApprovalMode.InsertConflictDoNothing)
            throw new InvalidOperationException(
                "Native conflict skip proof cannot authorize a different DML approval mode.");
        if (plan.NativeConflictDoUpdateProof is not null
            && (plan.ApprovalMode != DmlApprovalMode.InsertConflictDoUpdate
                || plan.NativeConflictDoNothingProof is not null))
            throw new InvalidOperationException(
                "PostgreSQL conflict UPDATE proof cannot authorize another mode or carry a DO NOTHING proof.");

        switch (plan.ApprovalMode)
        {
            case DmlApprovalMode.SqlServerMergeMatchedDelete:
            case DmlApprovalMode.SqlServerMergeMatchedUpdate:
                ValidateSqlServerMergePlan(plan);
                return;

            case DmlApprovalMode.InsertConflictDoNothing:
                var keyProof = plan.NativeConflictDoNothingProof;
                if (plan.Operation != DmlOperation.Insert
                    || plan.MutationCommand.TargetProvider is not (SqlAgentToolType.Sqlite or SqlAgentToolType.Postgres)
                    || plan.RowIdentityAssurance != DmlRowIdentityAssurance.Strict
                    || plan.MaxAffectedRows != 1
                    || plan.InsertRows.Length != 1
                    || plan.RowIdentityColumns.Length != 1
                    || plan.MatchQueryCommand is not { Kind: SqlStatementKind.Select,
                        TargetProvider: SqlAgentToolType.Sqlite or SqlAgentToolType.Postgres } pkMatch
                    || pkMatch.TargetProvider != plan.MutationCommand.TargetProvider
                    || plan.SchemaSnapshot is null
                    || plan.SchemaSnapshot.Provider != plan.MutationCommand.TargetProvider
                    || plan.SchemaSnapshot.Tables.Count != 1
                    || !(plan.MutationCommand.TargetProvider == SqlAgentToolType.Sqlite
                        ? plan.SchemaSnapshot.Version.StartsWith(
                            "native-dml-schema-v2:", StringComparison.Ordinal)
                        : plan.SchemaSnapshot.Version.StartsWith(
                            "native-postgres-insert-source-v1:", StringComparison.Ordinal))
                    || keyProof is null
                    || !string.Equals(keyProof.PhysicalTarget, plan.TableName,
                        plan.MutationCommand.TargetProvider == SqlAgentToolType.Postgres
                            ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(plan.SchemaSnapshot.Tables[0].Name,
                        plan.TableName, plan.MutationCommand.TargetProvider == SqlAgentToolType.Postgres
                            ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(keyProof.SchemaSha256, plan.SchemaSnapshot.Sha256,
                        StringComparison.Ordinal)
                    || !string.Equals(keyProof.UniqueKeyColumn,
                        plan.RowIdentityColumns[0], StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(keyProof.MutationFingerprint,
                        plan.PlanFingerprint, StringComparison.Ordinal)
                    || !string.Equals(keyProof.TargetQueryFingerprint,
                        DmlFingerprintService.ComputePlanFingerprint(pkMatch, plan.PolicyVersion),
                        StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "Native conflict skip requires a sealed sole enforced UNIQUE key proof, " +
                        "exact match SELECT, immutable single payload and physical schema freshness.");
                return;

            case DmlApprovalMode.InsertConflictDoUpdate:
                var updateProof = plan.NativeConflictDoUpdateProof;
                if (plan.Operation != DmlOperation.Insert
                    || plan.MutationCommand.TargetProvider != SqlAgentToolType.Postgres
                    || plan.RowIdentityAssurance != DmlRowIdentityAssurance.Strict
                    || plan.MaxAffectedRows != 1
                    || plan.InsertRows.Length != 1
                    || plan.RowIdentityColumns.Length is < 2 or > 5
                    || plan.MatchQueryCommand is not { Kind: SqlStatementKind.Select,
                        TargetProvider: SqlAgentToolType.Postgres } updateMatch
                    || plan.SchemaSnapshot is null
                    || plan.SchemaSnapshot.Provider != SqlAgentToolType.Postgres
                    || plan.SchemaSnapshot.Tables.Count != 1
                    || !plan.SchemaSnapshot.Version.StartsWith(
                        "native-postgres-insert-source-v1:", StringComparison.Ordinal)
                    || updateProof is null
                    || !string.Equals(updateProof.PhysicalTarget, plan.TableName, StringComparison.Ordinal)
                    || !string.Equals(plan.SchemaSnapshot.Tables[0].Name,
                        plan.TableName, StringComparison.Ordinal)
                    || !string.Equals(updateProof.SchemaSha256, plan.SchemaSnapshot.Sha256,
                        StringComparison.Ordinal)
                    || !string.Equals(updateProof.UniqueKeyColumn,
                        plan.RowIdentityColumns[0], StringComparison.Ordinal)
                    || updateProof.UpdatedColumns.IsDefaultOrEmpty
                    || updateProof.UpdatedColumns.Length != plan.RowIdentityColumns.Length - 1
                    || !updateProof.UpdatedColumns.SequenceEqual(
                        plan.RowIdentityColumns.Skip(1), StringComparer.Ordinal)
                    || updateProof.UpdatedColumns.Distinct(StringComparer.Ordinal).Count()
                        != updateProof.UpdatedColumns.Length
                    || plan.InsertRows[0].Count != plan.RowIdentityColumns.Length
                    || plan.RowIdentityColumns.Any(column => !plan.InsertRows[0].ContainsKey(column))
                    || !string.Equals(updateProof.MutationFingerprint,
                        plan.PlanFingerprint, StringComparison.Ordinal)
                    || !string.Equals(updateProof.TargetQueryFingerprint,
                        DmlFingerprintService.ComputePlanFingerprint(updateMatch, plan.PolicyVersion),
                        StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "PostgreSQL conflict UPDATE requires a sealed one-row before/after proof with exact physical key, columns and schema.");
                return;

            case DmlApprovalMode.InsertValues:
                if (plan.Operation != HsSqlAgent.SqlCore.Enums.DmlOperation.Insert)
                    throw new InvalidOperationException("INSERT VALUES approval mode requires an INSERT operation.");
                if (plan.MatchQueryCommand is not null)
                    throw new InvalidOperationException("INSERT VALUES approval must not carry a row-set match command.");
                if (!plan.RowIdentityColumns.IsDefaultOrEmpty)
                    throw new InvalidOperationException("INSERT VALUES approval must not carry pre-existing row identity columns.");
                if (plan.RowIdentityAssurance != DmlRowIdentityAssurance.CountOnly)
                    throw new InvalidOperationException("INSERT VALUES approval uses exact payload validation, not strict row identity.");
                if (plan.InsertRows.IsDefaultOrEmpty)
                    throw new InvalidOperationException("INSERT VALUES approval requires one or more immutable preview rows.");
                if (plan.MaxAffectedRows > 0 && plan.InsertRows.Length > plan.MaxAffectedRows)
                    throw new InvalidOperationException("INSERT VALUES payload exceeds the validated maximum affected row count.");
                return;

            case DmlApprovalMode.InsertSelect:
                var physicalComparison = plan.MutationCommand.TargetProvider is SqlAgentToolType.Postgres or SqlAgentToolType.MySQL
                    ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                if (plan.Operation != DmlOperation.Insert
                    || plan.MutationCommand.TargetProvider is not (SqlAgentToolType.Sqlite or SqlAgentToolType.Postgres or SqlAgentToolType.MySQL or SqlAgentToolType.MsSqlServer)
                    || plan.RowIdentityAssurance != DmlRowIdentityAssurance.Strict
                    || plan.MaxAffectedRows <= 0 || plan.MaxAffectedRows == int.MaxValue
                    || plan.MatchQueryCommand is not { Kind: SqlStatementKind.Select,
                        TargetProvider: SqlAgentToolType.Sqlite or SqlAgentToolType.Postgres or SqlAgentToolType.MySQL or SqlAgentToolType.MsSqlServer } match
                    || match.TargetProvider != plan.MutationCommand.TargetProvider
                    || plan.SchemaSnapshot is null
                    || plan.SchemaSnapshot.Provider != plan.MutationCommand.TargetProvider
                    || plan.SchemaSnapshot.Tables.Count is < 1 or > 2
                    || !plan.InsertRows.IsDefaultOrEmpty
                    || plan.RowIdentityColumns.IsDefaultOrEmpty
                    || plan.NativeInsertSelectProof is not { } sourceProof
                    || !string.Equals(sourceProof.PhysicalTarget, plan.TableName, StringComparison.Ordinal)
                    || !string.Equals(plan.SchemaSnapshot.Tables[0].Name, plan.TableName,
                        physicalComparison)
                    || (string.Equals(sourceProof.PhysicalSource, plan.TableName,
                            physicalComparison)
                        ? plan.SchemaSnapshot.Tables.Count != 1
                          || (plan.MutationCommand.TargetProvider == SqlAgentToolType.MsSqlServer
                              && !plan.SchemaSnapshot.Version.StartsWith(
                                  "native-sqlserver2022-insert-source-v1:", StringComparison.Ordinal))
                          || (plan.MutationCommand.TargetProvider == SqlAgentToolType.Postgres
                              && !plan.SchemaSnapshot.Version.StartsWith(
                                  "native-postgres-insert-source-v1:", StringComparison.Ordinal))
                          || (plan.MutationCommand.TargetProvider == SqlAgentToolType.MySQL
                              && !plan.SchemaSnapshot.Version.StartsWith(
                                  "native-mysql84-innodb-source-v2:", StringComparison.Ordinal))
                        : plan.SchemaSnapshot.Tables.Count != 2
                          || (plan.MutationCommand.TargetProvider == SqlAgentToolType.MsSqlServer
                              && !plan.SchemaSnapshot.Version.StartsWith(
                                  "native-sqlserver2022-dual-source-v1:", StringComparison.Ordinal))
                          || (plan.MutationCommand.TargetProvider == SqlAgentToolType.MySQL
                              && !plan.SchemaSnapshot.Version.StartsWith(
                                  "native-mysql84-dual-source-v1:", StringComparison.Ordinal))
                          || !string.Equals(plan.SchemaSnapshot.Tables[1].Name,
                              sourceProof.PhysicalSource, physicalComparison))
                    || !string.Equals(sourceProof.SchemaSha256, plan.SchemaSnapshot.Sha256,
                        StringComparison.Ordinal)
                    || !string.Equals(sourceProof.MutationFingerprint, plan.PlanFingerprint,
                        StringComparison.Ordinal)
                    || !string.Equals(sourceProof.SourceQueryFingerprint,
                        DmlFingerprintService.ComputePlanFingerprint(match, plan.PolicyVersion),
                        StringComparison.Ordinal)
                    || !sourceProof.ProjectedColumns.SequenceEqual(
                        plan.RowIdentityColumns, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        "Native INSERT SELECT requires a sealed, bounded, separately attested physical source-row proof.");
                return;

            case DmlApprovalMode.RowSetMutation:
                if (plan.NativeInsertSelectProof is not null)
                    throw new InvalidOperationException(
                        "SQLite INSERT SELECT proof cannot be reused for row-set UPDATE/DELETE.");
                if (plan.Operation == HsSqlAgent.SqlCore.Enums.DmlOperation.Insert)
                    throw new InvalidOperationException("INSERT cannot use row-set mutation approval mode.");
                if (plan.MatchQueryCommand is null)
                    throw new InvalidOperationException("Row-set DML approval requires a SELECT match command.");
                if (plan.MatchQueryCommand.Kind != SqlStatementKind.Select)
                    throw new InvalidOperationException("DML match command must be a SELECT command.");
                if (plan.MutationCommand.TargetProvider != plan.MatchQueryCommand.TargetProvider)
                    throw new InvalidOperationException("DML mutation and match commands target different providers.");
                if (!plan.InsertRows.IsDefaultOrEmpty)
                    throw new InvalidOperationException("Row-set DML approval must not carry INSERT preview rows.");
                if (plan.RowIdentityAssurance == DmlRowIdentityAssurance.Strict
                    && plan.RowIdentityColumns.IsDefaultOrEmpty)
                {
                    throw new InvalidOperationException(
                        "Strict DML approval requires one or more row identity columns.");
                }
                return;

            default:
                throw new InvalidOperationException($"Unsupported DML approval mode {plan.ApprovalMode}.");
        }
    }

    private static void ValidateSqlServerMergePlan(ValidatedDmlPlan plan)
    {
        var updateMode = plan.ApprovalMode == DmlApprovalMode.SqlServerMergeMatchedUpdate;
        var deletion = plan.SqlServerMergeMatchedDeleteProof;
        var update = plan.SqlServerMergeMatchedUpdateProof;
        var target = updateMode ? update?.PhysicalTarget : deletion?.PhysicalTarget;
        var key = updateMode ? update?.UniqueKeyColumn : deletion?.UniqueKeyColumn;
        var mutation = updateMode ? update?.MutationFingerprint : deletion?.MutationFingerprint;
        var match = updateMode ? update?.TargetQueryFingerprint : deletion?.TargetQueryFingerprint;
        var schema = updateMode ? update?.SchemaSha256 : deletion?.SchemaSha256;
        if (plan.Operation != (updateMode ? DmlOperation.Update : DmlOperation.Delete)
            || plan.MutationCommand.Kind != SqlStatementKind.Merge
            || plan.MutationCommand.TargetProvider != SqlAgentToolType.MsSqlServer
            || plan.RowIdentityAssurance != DmlRowIdentityAssurance.Strict
            || plan.MaxAffectedRows != 1
            || !plan.InsertRows.IsDefaultOrEmpty
            || plan.SqlServerCteUniqueKeyAssurance is not null
            || plan.NativeInsertSelectProof is not null
            || plan.NativeConflictDoNothingProof is not null
            || plan.NativeConflictDoUpdateProof is not null
            || (updateMode
                ? deletion is not null || update is null
                : update is not null || deletion is null)
            || plan.MatchQueryCommand is not { Kind: SqlStatementKind.Select,
                TargetProvider: SqlAgentToolType.MsSqlServer } pkMatch
            || plan.SchemaSnapshot is null
            || plan.SchemaSnapshot.Provider != SqlAgentToolType.MsSqlServer
            || plan.SchemaSnapshot.Tables.Count != 1
            || !plan.SchemaSnapshot.Version.StartsWith(
                "native-sqlserver2022-insert-source-v1:", StringComparison.Ordinal)
            || !string.Equals(target, plan.TableName, StringComparison.Ordinal)
            || !string.Equals(plan.SchemaSnapshot.Tables[0].Name,
                plan.TableName, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(schema, plan.SchemaSnapshot.Sha256, StringComparison.Ordinal)
            || plan.RowIdentityColumns.IsDefaultOrEmpty
            || !string.Equals(key, plan.RowIdentityColumns[0], StringComparison.Ordinal)
            || !string.Equals(mutation, plan.PlanFingerprint, StringComparison.Ordinal)
            || !string.Equals(match,
                DmlFingerprintService.ComputePlanFingerprint(pkMatch, plan.PolicyVersion),
                StringComparison.Ordinal)
            || (!updateMode && plan.RowIdentityColumns.Length != 1)
            || (updateMode && (update!.UpdatedColumns.Length is < 1 or > 3
                || update.ProposedValues.Length != update.UpdatedColumns.Length
                || plan.RowIdentityColumns.Length != update.UpdatedColumns.Length + 1
                || !update.UpdatedColumns.SequenceEqual(
                    plan.RowIdentityColumns.Skip(1), StringComparer.Ordinal)
                || update.UpdatedColumns.Distinct(StringComparer.Ordinal).Count()
                    != update.UpdatedColumns.Length
                || update.UpdatedColumns.Any(column =>
                    string.Equals(column, key, StringComparison.Ordinal)
                    || column.StartsWith("__", StringComparison.Ordinal)))))
            throw new InvalidOperationException(
                "SQL Server MERGE approval requires one exact physical INT PRIMARY key, " +
                "a sealed bounded old/new INT payload and a complete native schema/row-set proof.");
    }

    // Shared by single and atomic commits. No detached metadata connection,
    // optimistic preview digest or caller-owned Catalog can discharge this
    // proof. IsCurrent pins both tables until the active transaction ends.
    internal static void EnsureLiveSqlServerCteKeyCurrent(
        ValidatedDmlPlan plan,
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        if (plan.SqlServerCteUniqueKeyAssurance is not { } proof)
            return;

        cancellationToken.ThrowIfCancellationRequested();
        if (!proof.IsCurrent(connection, transaction))
        {
            throw new InvalidOperationException(
                "SQL Server CTE source unique-key metadata changed or cannot be " +
                "reverified in the actual mutation transaction; write denied.");
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static CompiledSqlCommand RequireMatchCommand(ValidatedDmlPlan plan) =>
        plan.MatchQueryCommand
        ?? throw new InvalidOperationException("Row-set DML approval requires a match query command.");

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

    private static object? GetRequiredValue(
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

    private static async Task<List<IReadOnlyDictionary<string, object?>>> QueryRowsAsync(
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

    private static async Task<MutationExecutionResult> ExecuteMutationAsync(
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
