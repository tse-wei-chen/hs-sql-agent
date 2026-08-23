using System.Collections.Immutable;
using System.Data.Common;
using Dapper;
using SqlAgent.Service.Core.Compilation;

namespace SqlAgent.Service.Core.Execution;

/// <summary>
/// Typed DML approval coordinator. Preview is read-only. Commit consumes a one-time approval,
/// opens a transaction, re-queries the matched row identity set, compares the approved challenge,
/// then executes exactly the compiled mutation command. Count equality alone is insufficient in
/// Strict mode.
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

    public async Task<DmlPreview> PreviewAsync(
        string connectionString,
        ValidatedDmlPlan plan,
        CancellationToken cancellationToken = default)
    {
        ValidatePlan(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        await using var connection = _connectionFactory.Create(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await _previewTransactionFactory.BeginAsync(
            connection,
            plan.MatchQueryCommand.TargetProvider,
            _transactionIsolationPolicy.PreviewIsolation(plan.MatchQueryCommand.TargetProvider),
            cancellationToken);

        var rows = await QueryRowsAsync(
            connection,
            transaction,
            plan.MatchQueryCommand,
            cancellationToken);

        await transaction.RollbackAsync(cancellationToken);

        if (plan.MaxAffectedRows > 0 && rows.Count > plan.MaxAffectedRows)
        {
            throw new UnauthorizedAccessException(
                $"Security policy denied DML: affectedRows={rows.Count} exceeds maximum {plan.MaxAffectedRows}.");
        }

        var rowSetFingerprint = ComputeRowSetFingerprint(plan, rows);
        var now = _timeProvider.GetUtcNow();
        var ttl = plan.ApprovalTtl > TimeSpan.Zero
            ? plan.ApprovalTtl
            : TimeSpan.FromMinutes(5);
        var challenge = new DmlApprovalChallenge(
            plan.PlanFingerprint,
            rowSetFingerprint,
            rows.Count,
            plan.PolicyVersion,
            now,
            now.Add(ttl),
            Guid.NewGuid().ToString("N"));

        await _challengeStore.RegisterAsync(challenge, cancellationToken);

        return new DmlPreview(
            plan.Operation,
            plan.TableName,
            rows.Count,
            rows.Take(PreviewRowLimit).ToImmutableArray(),
            challenge);
    }

    public async Task<DmlCommitResult> CommitAsync(
        string connectionString,
        ValidatedDmlPlan plan,
        DmlApprovalChallenge approvedChallenge,
        CancellationToken cancellationToken = default)
    {
        ValidatePlan(plan);
        ValidateChallenge(plan, approvedChallenge);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        if (!await _challengeStore.TryConsumeAsync(approvedChallenge, cancellationToken))
        {
            throw new InvalidOperationException(
                "DML approval challenge is unknown, modified, expired, or has already been consumed.");
        }

        await using var connection = _connectionFactory.Create(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            _transactionIsolationPolicy.CommitIsolation(plan.MutationCommand.TargetProvider),
            cancellationToken);

        try
        {
            var currentRows = await QueryRowsAsync(
                connection,
                transaction,
                plan.MatchQueryCommand,
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

            var affected = await ExecuteAsync(
                connection,
                transaction,
                plan.MutationCommand,
                cancellationToken);

            if (affected != approvedChallenge.AffectedRows)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new DmlCommitResult(
                    false,
                    0,
                    $"DML execution cancelled: affected row count changed after revalidation " +
                    $"(approved={approvedChallenge.AffectedRows}, executed={affected}).");
            }

            await transaction.CommitAsync(cancellationToken);
            return new DmlCommitResult(
                true,
                affected,
                "DML operation committed after approval and row-set revalidation.");
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private void ValidateChallenge(
        ValidatedDmlPlan plan,
        DmlApprovalChallenge challenge)
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
        if (plan.MaxAffectedRows > 0 && challenge.AffectedRows > plan.MaxAffectedRows)
            throw new InvalidOperationException("DML approval exceeds the validated maximum affected row count.");
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
        if (plan.MatchQueryCommand.Kind != SqlStatementKind.Select)
            throw new InvalidOperationException("DML match command must be a SELECT command.");
        if (plan.MutationCommand.TargetProvider != plan.MatchQueryCommand.TargetProvider)
            throw new InvalidOperationException("DML mutation and match commands target different providers.");

        var expectedFingerprint = DmlFingerprintService.ComputePlanFingerprint(
            plan.MutationCommand,
            plan.PolicyVersion);
        if (!string.Equals(expectedFingerprint, plan.PlanFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("Validated DML plan fingerprint does not match its mutation command.");

        if (plan.RowIdentityAssurance == DmlRowIdentityAssurance.Strict
            && plan.RowIdentityColumns.IsDefaultOrEmpty)
        {
            throw new InvalidOperationException(
                "Strict DML approval requires one or more row identity columns.");
        }
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

    private static async Task<int> ExecuteAsync(
        DbConnection connection,
        DbTransaction transaction,
        CompiledSqlCommand command,
        CancellationToken cancellationToken)
    {
        return await connection.ExecuteAsync(
            new CommandDefinition(
                command.Sql,
                BuildParameters(command),
                transaction,
                cancellationToken: cancellationToken));
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
}
