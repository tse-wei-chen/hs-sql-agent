using System.Data.Common;
using System.Security.Cryptography;

namespace SqlAgent.Service.Core.Execution;

/// <summary>
/// An acknowledged business COMMIT is never sufficient evidence for the
/// versioned target-fence contract. Once COMMIT is acknowledged, independently
/// inspect *both committed evidence rows* before returning success. If either
/// is missing, malformed or unreadable, surface a terminal, non-replayable
/// unknown outcome rather than pretending that the write was safely fenced.
/// </summary>
internal static class DmlTargetCommitEvidenceVerifier
{
    internal static async Task EnsureConfirmedAsync(
        DbConnection openConnection,
        SqlAgentToolType provider,
        DmlTargetCommitWitnessEvidence evidence,
        IDmlTargetExecutionFence fence,
        IDmlTargetCommitWitness witness,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(openConnection);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(fence);
        ArgumentNullException.ThrowIfNull(witness);

        try
        {
            var admission = await fence.ReadAsync(
                openConnection, provider, evidence.RequestId, cancellationToken);
            var committedWitness = await witness.ReadFingerprintAsync(
                openConnection, provider, evidence.RequestId, cancellationToken);

            if (admission is null
                || admission.EntryKind != RelationalDmlTargetExecutionFence.AdmissionKind
                || !MatchesFingerprint(admission.EvidenceFingerprint, evidence.EvidenceFingerprint)
                || !MatchesFingerprint(committedWitness, evidence.EvidenceFingerprint))
                throw new InvalidOperationException(
                    "Target COMMIT was acknowledged but the committed admission and witness " +
                    "could not both be verified. The approved SQL may have committed " +
                    "without the required safety evidence.");
        }
        catch (Exception error)
        {
            // This is after physical COMMIT: never use an ordinary SQL error
            // that could be interpreted as a safe rollback or retriable DML.
            throw new DmlCommitOutcomeUnknownException(error);
        }
    }

    private static bool MatchesFingerprint(string? actual, string expected)
    {
        if (actual is not { Length: 64 } || expected.Length != 64)
            return false;
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(actual), Convert.FromHexString(expected));
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
