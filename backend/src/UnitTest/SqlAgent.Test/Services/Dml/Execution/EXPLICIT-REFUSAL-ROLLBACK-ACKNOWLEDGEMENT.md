# #575 — A normal DML refusal still needs an acknowledged ROLLBACK

## Security boundary

The two DML coordinators have two separate rollback families:

1. **Exception cleanup:** `DmlTransactionFailureRecovery.RollbackWithoutMaskingAsync`
   was fixed by #608 to use an independent 10-second cancellation deadline
   and to classify unconfirmed native ROLLBACK as a terminal, non-replayable
   `DmlRollbackOutcomeUnknownException`.
2. **Ordinary commit-time refusal:** a changed row set, excess row count,
   conflicted target identity, changed source rows, or a mutation affecting
   fewer/more rows than approved previously called
   `transaction.RollbackAsync(requestCancellationToken)` directly and then
   returned `DmlCommitResult(Committed=false)` with a confident
   'rolled back' message.

The second family was **not** protected by #608. A request canceled before
the rollback can prevent the intended cleanup, while a native provider that
ignores token cancellation and never acknowledges ROLLBACK can block the
entire request indefinitely. It would be unsafe to interpret a timed-out
or unacknowledged rollback as a confirmed undo.

## Actual RED evidence (test-first)

[GitHub Actions Tests run 42932766118](https://github.com/tse-wei-chen/hs-sql-agent/actions/runs/42932766118)
on the test-only branch, **before** changing production code:

- `ExplicitRefusalRollback_UnacknowledgedNativeRollbackMustNotReturnSafeFalse`:
  a cancellation-ignoring ADO.NET transaction held its native rollback
  reply forever. The old explicit atomic refusal helper exceeded the
  14-second test bound instead of reporting typed uncertainty after the
  existing 10-second cleanup deadline.
- `ExplicitRefusalRollback_AlreadyCancelledRequestStillCleansUp`:
  a canceled request was passed straight into native rollback and rejected
  immediately; the old helper never obtained independent cleanup consent.

The SQL service job reported **8,145 passed, 2 failed, 9 existing skipped**.
Both failing cases were newly added behavioral regression tests; this
was **not** a compilation error.

## Bounded correction

`DmlTransactionFailureRecovery.RollbackForRefusalAsync` reuses **exactly
the same existing 10-second local wait and cleanup exception evidence**
as #608, but is called for ordinary/expected refusal rather than as a
secondary catch. An acknowledged native rollback returns normally so
the coordinator can retain the old `Committed=false` result and
statement-specific refusal reason.

The single-statement coordinator now uses it for every commit-time
stale/excess-row/conflict-identity/affected-row refusal; the atomic
coordinator uses it for each in-transaction refusal, including a later
statement whose affected-row count differs from the approved set.
A typed rollback-unknown outcome receives a dedicated catch that
captures and rethrows it through outer disposal error handling **without
issuing a second blind native ROLLBACK**. It cannot silently become
a normal/refundable refusal or a retriable approval.

`RollbackForRefusalAsync` never passes the original canceled client token
to native cleanup; the new cleanup token is deliberately independent.
If its local wait expires, it reports `DmlRollbackOutcomeUnknownException`;
this means the ACK is *unknown*, **not** that the database has rolled back
or that the underlying driver command has definitely stopped.

## Physical SQLite regression

The regression uses a real file-backed SQLite target. Both INSERTs are
previewed and approved; the first actually inserts a row inside the
native transaction. A `BEFORE INSERT` trigger with `RAISE(IGNORE)`
suppresses the second insert so its physical affected-row count is 0,
rather than the approved 1. This exercises the ordinary atomic
`RollbackAsync` refusal branch, **not** an exception path. After
acknowledged ROLLBACK, an independent SQLite connection must observe
zero committed rows.

Existing six-dialect positive, negative and live CI regressions are
required to remain green. The preview-only read transaction rollback
does not use this post-approval refusal helper, since it has not executed
approved mutations.

## Limits and release status

This fixes **only the local acknowledgement wait and typed result for
ordinary refusal branches**. A disconnected native command may still
be running in the provider or the database, and asynchronous native
transaction/connection disposal may have different timeouts; no global
physical-resource lifetime cap is claimed. It does not prove a lost
ACK after a successful native COMMIT, a real process crash, multi-worker
database fencing, or six-provider transport-failover recovery.

No public CLR or MCP tool contract, F# grammar, SQL compilation,
approval payload, or database schema changes. The release gate remains
#575 **OPEN**, and v3.1.0 must not ship solely because of this fix.
Require RED→GREEN on the exact PR HEAD, **all 18 Tests CI jobs**,
SqlCore baseline SUCCESS, and mandatory real-service checks before
squash merging to `develop`.
