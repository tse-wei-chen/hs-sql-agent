using System.Data.Common;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.SqlParsing;

namespace SqlAgent.Service.Core.Execution;

/// <summary>
/// Optional host capability for capturing a trusted current schema snapshot before
/// strict-schema DML compilation. Null means metadata completeness is unprovable.
/// </summary>
public interface IDmlSchemaSnapshotSource
{
    Task<SqlSchemaSnapshot?> CaptureAsync(
        DbConnection connection,
        SqlAgentToolType targetProvider,
        ParsedStatement parsedMutation,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional trusted host capability: reproduce the compiler's immutable schema snapshot digest
/// using live metadata on the given open connection and active transaction. The host must
/// capture the same snapshot scope/version and return its SqlSchemaSnapshot.Sha256.
/// Never return the approved digest without an independent live metadata read.
/// Returning null means live schema assurance cannot be established (fail closed).
/// </summary>
public interface IDmlSchemaSnapshotFreshnessVerifier
{
    Task<string?> ReadCurrentSchemaDigestAsync(
        DbConnection connection,
        DbTransaction transaction,
        SqlAgentToolType provider,
        SqlSchemaSnapshot expectedSnapshot,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Enforces live schema attestation only for commands that actually carry a compile-time proof.
/// Merely including a digest in compile evidence or an approval fingerprint is NOT live validation.
/// </summary>
internal static class DmlSchemaSnapshotFreshness
{
    internal static async Task EnsureFreshAsync(
        DbConnection connection,
        DbTransaction transaction,
        CompiledSqlCommand command,
        SqlSchemaSnapshot? expectedSnapshot,
        IDmlSchemaSnapshotFreshnessVerifier? verifier,
        CancellationToken cancellationToken)
    {
        var evidence = command.CompileEvidence;
        if (evidence is null)
            return;

        var assurances = evidence.Assurances
            .Where(item => string.Equals(item.Kind, "sqlcore.schema_snapshot", StringComparison.Ordinal))
            .ToArray();
        if (assurances.Length == 0)
            return;
        if (assurances.Length != 1)
            throw new InvalidOperationException("DML schema assurance is duplicated; commit rejected.");

        var digests = assurances[0].Details
            .Where(detail => string.Equals(detail.Name, "sha256", StringComparison.Ordinal))
            .Select(detail => detail.Value)
            .ToArray();
        if (digests.Length != 1 || !IsSha256(digests[0]))
            throw new InvalidOperationException("DML schema assurance is malformed; commit rejected.");

        if (expectedSnapshot is null
            || expectedSnapshot.Provider != command.TargetProvider
            || !string.Equals(expectedSnapshot.Sha256, digests[0], StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "DML compiled schema assurance and the immutable plan snapshot disagree; commit rejected.");

        if (verifier is null)
            throw new InvalidOperationException(
                "DML was compiled with a schema snapshot, but no live schema freshness verifier is configured; commit rejected.");

        var current = await verifier.ReadCurrentSchemaDigestAsync(
            connection, transaction, command.TargetProvider, expectedSnapshot, cancellationToken);

        if (current is null || !IsSha256(current)
            || !string.Equals(current, digests[0], StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "DML schema snapshot is stale or could not be verified on the commit connection; request a new preview.");
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);
}
