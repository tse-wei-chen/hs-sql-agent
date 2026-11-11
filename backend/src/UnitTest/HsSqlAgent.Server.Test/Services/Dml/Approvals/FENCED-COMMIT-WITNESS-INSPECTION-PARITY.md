# #575 — target-fence-v1 independent witness verification parity

## Contract mismatch and RED

A durable DML approval can opt in to the versioned
`target-fence-v1` target-side admission protocol. The physical COMMIT
coordinator verifies **both** a committed
`HS_SQL_AGENT_DML_WITNESS` row and a committed
`HS_SQL_AGENT_DML_FENCE` `Admitted` row with the identical evidence
fingerprint before confirming a fenced transaction.

Previously, operator-initiated
`DurableDmlWitnessVerificationService.InspectAsync` only read the
committed Witness row. A matching witness alone was returned as
`CommittedWitnessMatch` even when the persisted protected approval
requested `target-fence-v1` and the target-side fence was:

- missing;
- explicitly `Revoked`; or
- `Admitted` with a different evidence digest.

This contradicted the stronger physical coordinator's COMMIT evidence
contract. It could mislead the operator into believing a fully fenced
transaction was proven, and prevent a contrary human attestation on
insufficient evidence.

**Real RED evidence:** [GitHub Actions run 42929104537](https://github.com/tse-wei-chen/hs-sql-agent/actions/runs/42929104537)
executed three new file-backed SQLite tests *before the production
change*. All three failed on precisely `MatchingCommitWitness == true`
with a lone matching Witness and invalid/missing Fence.
The `server-test` job reported **923 passed, 3 failed, 0 skipped**.
These are behavioral failures, not compilation errors.

## Bounded correction

The inspection decrypts the preexisting versioned resume payload and
checks its persisted `FenceProtocol` **before querying** a target.
It does not trust an operator's newly supplied protocol flag.

For `target-fence-v1`, a positive COMMIT inspection now requires:

1. A currently configured, built-in relational Witness verifier and
   target Fence verifier. Missing/unsupported components produce
   `FenceNotConfigured`, never a witness-only fallback.
2. The existing target-identity/configuration HMAC checks to pass.
3. A committed Witness with the expected 64-character evidence digest.
4. A committed `Admitted` Fence row, on the **same verified physical
   connection**, with the same digest. A missing row produces
   `FenceNotObserved`; wrong `ENTRY_KIND` or digest produces
   `FenceEvidenceMismatch`.
5. The existing Admin Store approval snapshot and mutable target
   configuration are re-read after target I/O. Their drift still causes
   `ApprovalChanged` or `TargetConfigurationChanged`.

An unrecognized nonnull protocol returns
`UnsupportedFenceProtocol`, never silently downgrading to a legacy
Witness-only proof. Approvals persisted with `FenceProtocol = null`
continue to support the historical Witness-only inspection contract.

The operator reconciliation policy already refuses contrary
`not_committed`/`indeterminate` attestations when
`MatchingCommitWitness == true`. This fix makes that flag correspond
to the complete evidence contract for the approval's persisted
protocol. A missing/mismatched record still **never proves noncommit,
never fences an in-flight worker, and never authorizes approval replay**.

## Physical regression matrix

The Server regression uses real, independent, on-disk SQLite
connections and SQL tables for both Witness and Fence. The approval is
actually persisted with `FenceProtocol = target-fence-v1`. The
independent inspection is tested with:

- Witness only → `FenceNotObserved`, no match
- Witness plus `Revoked` Fence → `FenceEvidenceMismatch`, no match
- Witness plus `Admitted` Fence with wrong digest →
  `FenceEvidenceMismatch`, no match
- Witness plus correct committed `Admitted` Fence →
  `CommittedWitnessMatch`, match
- Correct physical rows but no registered Fence verifier →
  `FenceNotConfigured`, no match
- Existing legacy non-fenced approvals retain their positive
  witness-only behavior and their preexisting reconciliation tests.

No public MCP tools, SQL grammar, F# compiler contracts, DML approval
payload schema, or physical commit/retry semantics were modified.

## Remaining #575 release gates

This does **not** independently prove all six-provider native COMMIT
ACK-loss, process kill, failover, PostgreSQL/MySQL service interruption,
or a transaction that may still be executing. Physical absence of
target evidence is not proof of a rollback. Operator disposition stays
an explicit, audited, non-replayable human attestation.

Do not close #575 or publish v3.1.0 based on this bounded fix.
Require 18/18 Tests Jobs and SqlCore baseline success on the **exact
final PR HEAD** before squash-merging to `develop`.
