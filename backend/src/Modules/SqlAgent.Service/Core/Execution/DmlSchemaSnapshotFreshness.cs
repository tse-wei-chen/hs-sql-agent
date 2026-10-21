using System.Data.Common;
using HsSqlAgent.SqlCore.Core.Compilation;

namespace SqlAgent.Service.Core.Execution;

/// <summary>
/// Optional trusted host capability: reproduce the compiler's immutable schema snapshot digest
/// using live metadata on the given open connection and active transaction. The host must use
/// the same snapshot scope, declared version and canonicalization as SqlCore compilation.
/// Returning null means live schema assurance cannot be established (fail closed).
/// </summary>
public interface IDmlSchemaSnapshotFreshnessVerifier
{
    Task<string?> ReadCurrentSchemaDigestAsync(
        DbConnection connection,
        DbTransaction transaction,
        SqlAgentToolType provider,
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

        if (verifier is null)
            throw new InvalidOperationException(
                "DML was compiled with a schema snapshot, but no live schema freshness verifier is configured; commit rejected.");

        var current = await verifier.ReadCurrentSchemaDigestAsync(
            connection, transaction, command.TargetProvider, cancellationToken);

        if (current is null || !IsSha256(current)
            || !string.Equals(current, digests[0], StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "DML schema snapshot is stale or could not be verified on the commit connection; request a new preview.");
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);
}
