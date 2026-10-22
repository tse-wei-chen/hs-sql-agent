using System.Data;
using System.Data.Common;

namespace SqlAgent.Service.Core.Execution;

/// <summary>
/// Identity of one previously durable, server-owned approval. The immutable
/// evidence fingerprint binds the witness to the previewed mutation plan.
/// A witness is not an authorization token and never permits a retry.
/// </summary>
public sealed record DmlTargetCommitWitnessEvidence(string RequestId, string EvidenceFingerprint);

/// <summary>
/// Write a witness using the EXACT target connection and transaction of the
/// approved mutations. No implementation may create another transaction or
/// acknowledge the witness before the caller's target COMMIT.
/// </summary>
public interface IDmlTargetCommitWitness
{
    Task WriteAsync(
        DbConnection connection,
        DbTransaction transaction,
        SqlAgentToolType provider,
        DmlTargetCommitWitnessEvidence evidence,
        CancellationToken cancellationToken);

    /// <summary>
    /// Read a committed witness on an independently opened target connection.
    /// Absence is NOT evidence that an Executing worker has been fenced or
    /// cannot commit later.
    /// </summary>
    Task<string?> ReadFingerprintAsync(
        DbConnection openConnection,
        SqlAgentToolType provider,
        string requestId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Opt-in target-enlisted witness for a *pre-provisioned* table with a unique
/// primary key on REQUEST_ID. Uses fixed SQL and ADO parameters only.
/// Schema ownership, privileges and required DDL are documented separately.
/// It never creates tables and fails closed on a missing/inaccessible table.
/// </summary>
public sealed class RelationalDmlTargetCommitWitness : IDmlTargetCommitWitness
{
    private const string TableName = "HS_SQL_AGENT_DML_WITNESS";

    public async Task WriteAsync(
        DbConnection connection,
        DbTransaction transaction,
        SqlAgentToolType provider,
        DmlTargetCommitWitnessEvidence evidence,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(evidence);
        ValidateConnection(connection, transaction);
        ValidateRequestId(evidence.RequestId);
        if (evidence.EvidenceFingerprint.Length != 64
            || !evidence.EvidenceFingerprint.All(Uri.IsHexDigit))
            throw new ArgumentException("DML witness requires a 64-character hexadecimal evidence fingerprint.");

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var placeholder = Placeholder(provider);
        command.CommandText = $"INSERT INTO {TableName} (REQUEST_ID, EVIDENCE_FINGERPRINT) " +
                              $"VALUES ({placeholder}request_id, {placeholder}evidence_fingerprint)";
        Bind(command, "request_id", evidence.RequestId, placeholder);
        Bind(command, "evidence_fingerprint", evidence.EvidenceFingerprint, placeholder);
        var count = await command.ExecuteNonQueryAsync(cancellationToken);
        if (count != 1)
            throw new InvalidOperationException("Target commit witness must insert exactly one row.");
    }

    public async Task<string?> ReadFingerprintAsync(
        DbConnection openConnection,
        SqlAgentToolType provider,
        string requestId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(openConnection);
        ValidateRequestId(requestId);
        if (openConnection.State != ConnectionState.Open)
            throw new InvalidOperationException("Target witness verification requires an open target connection.");

        using var command = openConnection.CreateCommand();
        var placeholder = Placeholder(provider);
        command.CommandText = $"SELECT EVIDENCE_FINGERPRINT FROM {TableName} " +
                              $"WHERE REQUEST_ID = {placeholder}request_id";
        Bind(command, "request_id", requestId, placeholder);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? null : Convert.ToString(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void ValidateConnection(DbConnection connection, DbTransaction transaction)
    {
        if (connection.State != ConnectionState.Open || !ReferenceEquals(transaction.Connection, connection))
            throw new InvalidOperationException("Target commit witness must use the exact open DML transaction connection.");
    }

    private static void ValidateRequestId(string requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId) || requestId.Length > 64)
            throw new ArgumentException("Target commit witness request ID must be 1-64 characters.");
    }

    private static string Placeholder(SqlAgentToolType provider) => provider switch
    {
        SqlAgentToolType.Postgres or SqlAgentToolType.MySQL or SqlAgentToolType.Sqlite
            or SqlAgentToolType.MsSqlServer or SqlAgentToolType.Firebird => "@",
        SqlAgentToolType.Oracle => ":",
        _ => throw new NotSupportedException("Target commit witness requires a supported SQL provider.")
    };

    private static void Bind(DbCommand command, string name, string value, string placeholder)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = placeholder == ":" ? name : placeholder + name;
        parameter.DbType = DbType.String;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
