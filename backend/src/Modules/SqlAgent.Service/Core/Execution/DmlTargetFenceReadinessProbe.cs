using System.Data;
using System.Data.Common;
using System.Security.Cryptography;

namespace SqlAgent.Service.Core.Execution;

/// <summary>
/// Non-mutating deployment readiness probe on the *actual target connection*.
/// Proves that the selected worker identity can insert both target-fence and
/// witness evidence inside one transaction and that rollback removes both.
/// No application SQL is issued and the probe NEVER commits to the target.
///
/// This is not evidence that old workers have had their credentials revoked,
/// nor that independently connected databases share the same authority.
/// </summary>
public sealed class DmlTargetFenceReadinessProbe
{
    /// <summary>
    /// Verify only the built-in, audited relational implementations. It is
    /// unsafe to accept a caller-supplied no-op fence or witness as proof.
    /// </summary>
    public Task VerifyAsync(
        DbConnection openConnection,
        SqlAgentToolType provider,
        CancellationToken cancellationToken = default) =>
        VerifyWithDependenciesAsync(
            openConnection, provider,
            new RelationalDmlTargetExecutionFence(),
            new RelationalDmlTargetCommitWitness(), cancellationToken);

    // Test seam for forced witness errors. It is not part of the public
    // operator readiness contract and cannot be used by external consumers
    // to substitute a no-op proof.
    internal async Task VerifyWithDependenciesAsync(
        DbConnection openConnection,
        SqlAgentToolType provider,
        IDmlTargetExecutionFence fence,
        IDmlTargetCommitWitness witness,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(openConnection);
        ArgumentNullException.ThrowIfNull(fence);
        ArgumentNullException.ThrowIfNull(witness);
        if (openConnection.State != ConnectionState.Open)
            throw new InvalidOperationException(
                "Target fence readiness requires an open connection using the intended v3 DML identity.");

        var evidence = new DmlTargetCommitWitnessEvidence(
            "fence-probe-" + Guid.NewGuid().ToString("N"),
            Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));

        // Phase 1: both real writes must enlist in one physical DML
        // transaction, and the fence REQUEST_ID must be UNIQUE. A table
        // that merely accepts INSERT/ROLLBACK is NOT a valid fence.
        await using (var transaction = await openConnection.BeginTransactionAsync(
                         IsolationLevel.Serializable, cancellationToken))
        {
            try
            {
                await fence.AdmitAsync(
                    openConnection, transaction, provider, evidence, cancellationToken);
                await witness.WriteAsync(
                    openConnection, transaction, provider, evidence, cancellationToken);

                var duplicateRejected = false;
                try
                {
                    await fence.AdmitAsync(
                        openConnection, transaction, provider, evidence, cancellationToken);
                }
                catch (DbException)
                {
                    duplicateRejected = true;
                }

                if (!duplicateRejected)
                    throw new InvalidOperationException(
                        "Target fence readiness failed: REQUEST_ID admission was not rejected twice. " +
                        "A database-enforced unique key is required.");
            }
            catch (Exception failure)
            {
                await DmlTransactionFailureRecovery.RollbackWithoutMaskingAsync(
                    transaction, failure);
                DmlTransactionFailureRecovery.ThrowIfRollbackUnconfirmed(failure);
                throw;
            }

            // A duplicate-key error may abort the entire transaction (e.g.
            // PostgreSQL). Always roll back before starting the second probe.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await transaction.RollbackAsync(cleanup.Token);
        }

        // Phase 2: the witness REQUEST_ID must also be UNIQUE. Include the
        // matching admission to support deployments with a witness→fence
        // foreign key. Again, no probe transaction is ever committed.
        await using (var transaction = await openConnection.BeginTransactionAsync(
                         IsolationLevel.Serializable, cancellationToken))
        {
            try
            {
                await fence.AdmitAsync(
                    openConnection, transaction, provider, evidence, cancellationToken);
                await witness.WriteAsync(
                    openConnection, transaction, provider, evidence, cancellationToken);

                var duplicateRejected = false;
                try
                {
                    await witness.WriteAsync(
                        openConnection, transaction, provider, evidence, cancellationToken);
                }
                catch (DbException)
                {
                    duplicateRejected = true;
                }

                if (!duplicateRejected)
                    throw new InvalidOperationException(
                        "Target fence readiness failed: REQUEST_ID witness was not rejected twice. " +
                        "A database-enforced unique key is required.");
            }
            catch (Exception failure)
            {
                await DmlTransactionFailureRecovery.RollbackWithoutMaskingAsync(
                    transaction, failure);
                DmlTransactionFailureRecovery.ThrowIfRollbackUnconfirmed(failure);
                throw;
            }

            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await transaction.RollbackAsync(cleanup.Token);
        }

        // The second, committed-visibility check catches a provider that
        // writes outside the enrolled transaction or uses nontransactional
        // storage. A missing/unreadable table must fail closed.
        var admission = await fence.ReadAsync(
            openConnection, provider, evidence.RequestId, cancellationToken);
        var observedWitness = await witness.ReadFingerprintAsync(
            openConnection, provider, evidence.RequestId, cancellationToken);
        if (admission is not null || observedWitness is not null)
            throw new InvalidOperationException(
                "Target fence readiness failed: rolled-back admission or witness remains visible. " +
                "Do not enable fenced DML on this target or connection identity.");
    }
}
