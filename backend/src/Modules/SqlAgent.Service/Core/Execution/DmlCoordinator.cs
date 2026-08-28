using System.Collections.Immutable;
using System.Data.Common;
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
    IDmlPreviewTransactionFactory? previewTransactionFactory = null) : IDmlCoordinator
{
    private const int PreviewRowLimit = 20;
    private readonly IDbConnectionFactory _connectionFactory = connectionFactory;
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

        var matchCommand = RequireMatchCommand(plan);
        await using var connection = _connectionFactory.Create(connectionString);
        await connection.OpenAsync(cancellationToken);
        RuntimeServerProfileVerifier.EnsureMatches(connection, expectedServerVersionIdentity);
        await using var transaction = await _previewTransactionFactory.BeginAsync(
            connection,
            matchCommand.TargetProvider,
            _transactionIsolationPolicy.PreviewIsolation(matchCommand.TargetProvider),
            cancellationToken);

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

        var rowSetFingerprint = ComputeRowSetFingerprint(plan, rows);
        var challenge = CreateChallenge(
            plan,
            rowSetFingerprint,
            rows.Count,
            approvalContextFingerprint);
        await _challengeStore.RegisterAsync(challenge, cancellationToken);

        return new DmlPreview(
            plan.Operation,
            plan.TableName,
            rows.Count,
            rows.Take(PreviewRowLimit).ToImmutableArray(),
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

    public async Task<DmlCommitResult> CommitAsync(
        string connectionString,
        ValidatedDmlPlan plan,
        DmlApprovalChallenge approvedChallenge,
        string approvalContextFingerprint,
        CancellationToken cancellationToken,
        string expectedServerVersionIdentity)
    {
        ValidatePlan(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(approvalContextFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedServerVersionIdentity);
        ValidateChallenge(plan, approvedChallenge, approvalContextFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        if (!await _challengeStore.TryConsumeAsync(approvedChallenge, cancellationToken))
        {
            throw new InvalidOperationException(
                "DML approval challenge is unknown, modified, expired, or has already been consumed.");
        }

        await using var connection = _connectionFactory.Create(connectionString);
        await connection.OpenAsync(cancellationToken);
        RuntimeServerProfileVerifier.EnsureMatches(connection, expectedServerVersionIdentity);
        await using var transaction = await connection.BeginTransactionAsync(
            _transactionIsolationPolicy.CommitIsolation(plan.MutationCommand.TargetProvider),
            cancellationToken);

        try
        {
            if (plan.ApprovalMode == DmlApprovalMode.InsertValues)
            {
                return await CommitInsertValuesAsync(
                    connection,
                    transaction,
                    plan,
                    approvedChallenge,
                    cancellationToken);
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

            if (currentRows.Count != approvedChallenge.AffectedRows
                || !string.Equals(
                    currentFingerprint,
                    approvedChallenge.RowSetFingerprint,
                    StringComparison.Ordinal))
            {
                await transaction.RollbackAsync(cancellationToken);
                return new DmlCommitResult(
                    false,
                    0,
                    "DML execution cancelled: the matched row set changed after approval.");
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

            await transaction.CommitAsync(cancellationToken);
            return new DmlCommitResult(
                true,
                execution.AffectedRows,
                "DML operation committed after approval and row-set revalidation.")
            {
                ReturnedRows = execution.ReturnedRows
            };
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
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

    private static async Task<DmlCommitResult> CommitInsertValuesAsync(
        DbConnection connection,
        DbTransaction transaction,
        ValidatedDmlPlan plan,
        DmlApprovalChallenge approvedChallenge,
        CancellationToken cancellationToken)
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

        await transaction.CommitAsync(cancellationToken);
        return new DmlCommitResult(
            true,
            execution.AffectedRows,
            "INSERT VALUES committed after exact-plan approval validation.")
        {
            ReturnedRows = execution.ReturnedRows
        };
    }

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
            Guid.NewGuid().ToString("N"));
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

    private static void ValidatePlan(ValidatedDmlPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(plan.TableName);
        ArgumentException.ThrowIfNullOrWhiteSpace(plan.PolicyVersion);

        if (plan.MaxAffectedRows < 0)
            throw new InvalidOperationException("DML maximum affected rows cannot be negative.");
        if (plan.MutationCommand.Kind is not (
            SqlStatementKind.Insert or SqlStatementKind.Update or SqlStatementKind.Delete))
        {
            throw new InvalidOperationException(
                $"DML mutation command has invalid kind {plan.MutationCommand.Kind}.");
        }

        var expectedKind = plan.Operation switch
        {
            HsSqlAgent.SqlCore.Enums.DmlOperation.Insert => SqlStatementKind.Insert,
            HsSqlAgent.SqlCore.Enums.DmlOperation.Update => SqlStatementKind.Update,
            HsSqlAgent.SqlCore.Enums.DmlOperation.Delete => SqlStatementKind.Delete,
            _ => throw new InvalidOperationException($"Unsupported DML operation {plan.Operation}.")
        };
        if (plan.MutationCommand.Kind != expectedKind)
            throw new InvalidOperationException("DML operation does not match its compiled mutation command kind.");

        var expectedFingerprint = DmlFingerprintService.ComputePlanFingerprint(
            plan.MutationCommand,
            plan.PolicyVersion);
        if (!string.Equals(expectedFingerprint, plan.PlanFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("Validated DML plan fingerprint does not match its mutation command.");

        switch (plan.ApprovalMode)
        {
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

            case DmlApprovalMode.RowSetMutation:
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
