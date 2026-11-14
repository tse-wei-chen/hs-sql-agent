# DML admission lease revocation before native COMMIT (#575)

## Confirmed RED (not a synthetic claim)

The SQL concurrency limiter's Redis lease is revocable since #602, but three
DML pre-commit paths previously accepted an already-revoked lease and kept using
the original caller cancellation token:

- The runtime-server-profile probe could open a target DB connection.
- Single-statement and batch DML Preview could construct approval evidence and
  proceed toward a human approval, despite losing their Redis admission.
- After an interactive approval, the new commit lease was never checked for
  revocation before the native DML transaction started. A lost distributed
  admission could therefore still allow physical writes.

The test-first PR #605 RED runs establish both classes of defect:

- [RED Tests 42810316877](https://github.com/tse-wei-chen/hs-sql-agent/actions/runs/42810316877):
  the server job reported **3 new failures**, 915 passed; all three failures
  were already-revoked preflight/Preview cases, not build failures.
- [RED Tests 42811062762](https://github.com/tse-wei-chen/hs-sql-agent/actions/runs/42811062762):
  the server job reported **1 new failure**, 918 passed, 0 skipped. A real
  SQLite DB successfully previewed and human-approved an INSERT, then the
  second (COMMIT) Redis lease was already revoked. The existing runtime
  nevertheless returned `Committed=true`; the physical write actually
  executed, violating the expected no-write admission boundary.

## Correction

Read-only preflight and DML Preview (single, batch, and durable re-preview)
now use `SqlExecutionLeaseCancellation`. They check revocation **before**
starting physical work, propagate the linked cancellation token to the native
provider, and recheck **after** the awaited runtime. A native driver that
ignores cancellation cannot make a late successful preview available for
human approval. All preview leases end before the potentially long human
approval wait; fresh SQL admission is required for commit.

Immediately before **starting** DML commit, `SqlDmlCommitLeaseAdmission`
checks the freshly acquired lease. A revoked lease is denied with stable
`dml.lease_revoked_before_commit` (non-retryable), and the human is directed
to a fresh preview + approval. The same check runs on the durable completion
path after the execution claim, before calling `CommitTransactionAsync`.
No mutation or approval challenge consumption is attempted at this check.
The completed-but-revoked approval is not automatically replayed.

## Explicit limits (remaining #575)

The admission check is a local pre-COMMIT decision and is not a globally
fenced database transaction. Redis revocation **during** an already-started
COMMIT must not be interpreted as rollback or definite failure. The commit
runtime still receives the **original request cancellation token**, not
the lease revocation token; its target transaction / witness mechanism owns
`DmlCommitOutcomeUnknownException` and `DmlRollbackOutcomeUnknownException`.
An acknowledged COMMIT remains authoritative even if admission is lost or
secondary audit/state I/O fails.

The same check does not prove that all in-flight mutations stop immediately
after a distributed partition, that multiple physical server sessions obey
a single global resource cap, or that process crashes reconcile without a
target-side witness. Database-side connection caps, strict `FailClosed`
admission configuration and the outstanding native six-provider failure
tests are still required. No SQL AST, F# compiler, approval schema, tool count
or public CLR limiter contract changed.

Keep #575 OPEN; do not release v3.1.0. Squash merge only after exact-head
18/18 required Tests jobs, zero skipped real-provider proof, and SqlCore
baseline comparison both pass.
