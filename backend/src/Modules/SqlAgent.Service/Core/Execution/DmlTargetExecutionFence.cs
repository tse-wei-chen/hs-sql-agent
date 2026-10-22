using System.Data;
using System.Data.Common;
using System.Runtime.ExceptionServices;

namespace SqlAgent.Service.Core.Execution;

/// <summary>
/// Persistent, target-enlisted admission barrier. The first admitted writer
/// and any later revoker compete for the same UNIQUE request ID in the target
/// database. The admission INSERT must occur before application SQL,
/// after provider-native catalog verification that could interfere with
/// an existing uncommitted Oracle admission.
/// </summary>
public interface IDmlTargetExecutionFence
{
    Task AdmitAsync(
        DbConnection connection,
        DbTransaction transaction,
        SqlAgentToolType provider,
        DmlTargetCommitWitnessEvidence evidence,
        CancellationToken cancellationToken);

    /// <summary>
    /// Independently fences an approval. Returns only after the target
    /// transaction containing a permanent revocation has acknowledged COMMIT.
    /// A uniqueness conflict or any ambiguous failure is NOT negative proof.
    /// </summary>
    Task RevokeAsync(
        DbConnection openConnection,
        SqlAgentToolType provider,
        DmlTargetCommitWitnessEvidence evidence,
        CancellationToken cancellationToken);

    Task<DmlTargetExecutionFenceEntry?> ReadAsync(
        DbConnection openConnection,
        SqlAgentToolType provider,
        string requestId,
        CancellationToken cancellationToken);
}

public sealed record DmlTargetExecutionFenceEntry(
    string EvidenceFingerprint, string EntryKind);

/// <summary>
/// Fixed, parameterized SQL for an independently provisioned table. Both
/// admission and revocation are ordinary INSERTs with an enforced PK; no
/// upsert, timeout lease, implicit retry or SQL dialect-specific lock escape.
/// </summary>
public sealed class RelationalDmlTargetExecutionFence : IDmlTargetExecutionFence
{
    public const string AdmissionKind = "Admitted";
    public const string RevocationKind = "Revoked";
    private const string TableName = "HS_SQL_AGENT_DML_FENCE";

    public Task AdmitAsync(
        DbConnection connection,
        DbTransaction transaction,
        SqlAgentToolType provider,
        DmlTargetCommitWitnessEvidence evidence,
        CancellationToken cancellationToken) =>
        InsertAsync(connection, transaction, provider, evidence, AdmissionKind, cancellationToken);

    public async Task RevokeAsync(
        DbConnection openConnection,
        SqlAgentToolType provider,
        DmlTargetCommitWitnessEvidence evidence,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(openConnection);
        if (openConnection.State != ConnectionState.Open)
            throw new InvalidOperationException("Fencing requires an open target connection.");

        // A committed revocation row survives crashes. A concurrent admitted
        // worker's uncommitted unique-key row must commit/rollback before
        // the new insertion can be confirmed by the target database.
        var commitAcknowledged = false;
        ExceptionDispatchInfo? primaryFailure = null;
        try
        {
            await using var transaction = await openConnection.BeginTransactionAsync(
                IsolationLevel.Serializable, cancellationToken);
            try
            {
                await InsertAsync(openConnection, transaction, provider, evidence,
                    RevocationKind, cancellationToken);
                // A lost COMMIT acknowledgement is NOT a confirmed fence.
                await DmlTransactionFailureRecovery.CommitAsync(transaction, cancellationToken);
                commitAcknowledged = true;
            }
            catch (Exception failure)
            {
                await DmlTransactionFailureRecovery.RollbackWithoutMaskingAsync(
                    transaction, failure);
                try
                {
                    DmlTransactionFailureRecovery.ThrowIfRollbackUnconfirmed(failure);
                }
                catch (DmlRollbackOutcomeUnknownException rollbackUnknown)
                {
                    primaryFailure = ExceptionDispatchInfo.Capture(rollbackUnknown);
                    throw;
                }

                primaryFailure = ExceptionDispatchInfo.Capture(failure);
                throw;
            }
        }
        catch (Exception) when (commitAcknowledged)
        {
            // Revocation COMMIT is positively acknowledged. DisposeAsync
            // cannot turn that durable barrier into a reported failure.
            return;
        }
        catch (Exception cleanupFailure) when (
            primaryFailure is not null
            && !ReferenceEquals(cleanupFailure, primaryFailure.SourceException))
        {
            // A provider cleanup error cannot erase a lost COMMIT
            // acknowledgement or failed ROLLBACK classification.
            primaryFailure.Throw();
            throw;
        }
    }

    /// <summary>
    /// Check the *current mutation transaction's* physical admission just
    /// before application SQL. A schema/capability verifier must never
    /// silently terminate, replace or roll back the admitted transaction.
    /// This guard is independent of the post-COMMIT evidence check.
    /// </summary>
    internal static async Task EnsureAdmittedInTransactionAsync(
        DbConnection connection,
        DbTransaction transaction,
        SqlAgentToolType provider,
        DmlTargetCommitWitnessEvidence evidence,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(evidence);
        ValidateRequestId(evidence.RequestId);
        if (!ReferenceEquals(transaction.Connection, connection)
            || connection.State != ConnectionState.Open)
            throw new InvalidOperationException(
                "Target execution admission must be read within its exact open DML transaction.");

        var prefix = ParameterPrefix(provider);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT EVIDENCE_FINGERPRINT, ENTRY_KIND FROM {TableName} " +
                              $"WHERE REQUEST_ID = {prefix}request_id";
        Bind(command, "request_id", evidence.RequestId, prefix);
        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SingleRow, cancellationToken);
        var present = await reader.ReadAsync(cancellationToken);
        var fingerprint = present ? reader.GetString(0).TrimEnd() : null;
        var kind = present ? reader.GetString(1).TrimEnd() : null;
        if (kind != AdmissionKind ||
            !string.Equals(fingerprint, evidence.EvidenceFingerprint, StringComparison.OrdinalIgnoreCase))
            throw new DmlCommitOutcomeUnknownException(new InvalidOperationException(
                "The enlisted target-fence admission vanished or changed before application SQL. " +
                "The worker must not execute the approved DML or replay the consumed approval."));
    }

    public async Task<DmlTargetExecutionFenceEntry?> ReadAsync(
        DbConnection openConnection,
        SqlAgentToolType provider,
        string requestId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(openConnection);
        ValidateRequestId(requestId);
        if (openConnection.State != ConnectionState.Open)
            throw new InvalidOperationException("Reading target execution fence requires an open connection.");
        using var command = openConnection.CreateCommand();
        var prefix = ParameterPrefix(provider);
        command.CommandText = $"SELECT EVIDENCE_FINGERPRINT, ENTRY_KIND FROM {TableName} " +
                              $"WHERE REQUEST_ID = {prefix}request_id";
        Bind(command, "request_id", requestId, prefix);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new DmlTargetExecutionFenceEntry(
            reader.GetString(0).TrimEnd(), reader.GetString(1).TrimEnd());
    }

    private static async Task InsertAsync(
        DbConnection connection,
        DbTransaction transaction,
        SqlAgentToolType provider,
        DmlTargetCommitWitnessEvidence evidence,
        string kind,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(evidence);
        ValidateRequestId(evidence.RequestId);
        if (evidence.EvidenceFingerprint is not { Length: 64 }
            || !evidence.EvidenceFingerprint.All(Uri.IsHexDigit))
            throw new ArgumentException("Fence evidence fingerprint must be exactly 64 hexadecimal characters.");
        if (connection.State != ConnectionState.Open
            || !ReferenceEquals(transaction.Connection, connection))
            throw new InvalidOperationException("Fencing admission must enlist in the exact open DML transaction.");

        var prefix = ParameterPrefix(provider);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"INSERT INTO {TableName} " +
                              "(REQUEST_ID, EVIDENCE_FINGERPRINT, ENTRY_KIND) " +
                              $"VALUES ({prefix}request_id, {prefix}evidence_fingerprint, {prefix}entry_kind)";
        Bind(command, "request_id", evidence.RequestId, prefix);
        Bind(command, "evidence_fingerprint", evidence.EvidenceFingerprint, prefix);
        Bind(command, "entry_kind", kind, prefix);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("Target fence admission must affect exactly one row.");
    }

    private static void ValidateRequestId(string requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId) || requestId.Length > 64)
            throw new ArgumentException("Target fence request ID must have 1-64 characters.");
    }

    private static string ParameterPrefix(SqlAgentToolType provider) => provider switch
    {
        SqlAgentToolType.Postgres or SqlAgentToolType.MySQL or SqlAgentToolType.Sqlite
            or SqlAgentToolType.MsSqlServer or SqlAgentToolType.Firebird => "@",
        SqlAgentToolType.Oracle => ":",
        _ => throw new NotSupportedException("Target fencing requires a supported SQL provider.")
    };

    private static void Bind(DbCommand command, string name, string value, string prefix)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = prefix == ":" ? name : prefix + name;
        parameter.DbType = DbType.String;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
