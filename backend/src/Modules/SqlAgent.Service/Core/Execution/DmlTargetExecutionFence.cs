using System.Data;
using System.Data.Common;

namespace SqlAgent.Service.Core.Execution;

/// <summary>
/// Persistent, target-enlisted admission barrier. The first admitted writer
/// and any later revoker compete for the same UNIQUE request ID in the target
/// database. The admission INSERT must happen at the beginning of the
/// mutation transaction, before schema queries and before application SQL.
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
        await using var transaction = await openConnection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        try
        {
            await InsertAsync(openConnection, transaction, provider, evidence,
                RevocationKind, cancellationToken);
            // A lost COMMIT acknowledgement is NOT a confirmed fence.
            await DmlTransactionFailureRecovery.CommitAsync(transaction, cancellationToken);
        }
        catch (Exception failure)
        {
            await DmlTransactionFailureRecovery.RollbackWithoutMaskingAsync(
                transaction, failure);
            DmlTransactionFailureRecovery.ThrowIfRollbackUnconfirmed(failure);
            throw;
        }
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
