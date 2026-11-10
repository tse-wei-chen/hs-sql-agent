# Native DML ROLLBACK response timeout and PostgreSQL COMMIT disconnect — #575

## Specific security failure

`DmlTransactionFailureRecovery.RollbackWithoutMaskingAsync` is the
shared cleanup path after a native DML mutation, validation error, or
uncertain COMMIT. It supplies a fresh **10-second** cleanup cancellation
token rather than reusing the canceled request token, but previously
directly awaited the ADO.NET `DbTransaction.RollbackAsync(token)` task.
Some drivers can ignore token cancellation while a ROLLBACK acknowledgement
is blocked by transport loss. If that task never finishes, the cleanup
helper cannot return the original mutation failure or publish the typed
`DmlRollbackOutcomeUnknownException` needed to prevent unsafe approval
replay.

## RED-first proof

[RED GitHub Actions Tests run 42925848188](https://github.com/tse-wei-chen/hs-sql-agent/actions/runs/42925848188):
`sqlagent-test` ran `NativeRollbackIgnoresCancellation_ReportsUnconfirmedInsteadOfHangingForever`
against the old production helper. The test's cancellation-ignoring
native-transaction double kept the ROLLBACK task incomplete past the
configured 10-second cleanup token, leading to an actual **14-second
failure**. The job reported **8,143 passed, 1 failed**; nine existing
unrelated cases in that broad suite were skipped. RED was a runtime
timeout, not a build failure.

## Correction

The existing cleanup timeout, exception types, and ownership remain
unchanged. The helper now awaits

```csharp
await transaction.RollbackAsync(cleanup.Token).WaitAsync(cleanup.Token);
```

This bounds only the **local wait** when the ADO.NET driver ignores its
token. A canceled or faulted cleanup records the original exception in
`RollbackFailureDataKey`; `ThrowIfRollbackUnconfirmed` then emits
`DmlRollbackOutcomeUnknownException` unless COMMIT was already
uncertain, in which case `DmlCommitOutcomeUnknownException` takes
precedence. A timed-out ROLLBACK is **not evidence** the server rolled
back, that a native command has stopped, or that the connection can be
safely reused. Consumed approvals remain non-replayable.

Resource disposal, individual provider behaviors that ignore cancellation
even while disposing a native connection, and external process
termination remain separate concerns. The helper's bounded return is
not a global bound on the lifetime of all provider resources.

## Native PostgreSQL 16 TCP disconnect proof

`DmlNativePostgresCommitDisconnectTests` constructs an actual deferred
PostgreSQL constraint trigger that waits inside COMMIT. An independent
Npgsql connection observes `pg_stat_activity` showing the worker
backend actively sleeping on the COMMIT command, then issues
`pg_terminate_backend(pid)`. This terminates a **real PostgreSQL backend
and its client connection**, not an ADO.NET wrapper injecting an error
after COMMIT.

The in-flight `DmlTransactionFailureRecovery.CommitAsync` must classify
the transport loss as a **non-retryable unknown COMMIT outcome**. An
independent connection verifies no application row was committed by
this intentionally terminated backend; the positive database observation
does **not** authorize replay of the consumed approval. This test is
explicitly wired to the PostgreSQL 16 CI service and CI requires
**exactly one succeeded, zero skipped** in its dedicated step, preventing
a filter mismatch from silently producing a green job.

A backend forcibly terminated *before commit* is not the same as
disconnecting the client at the last microsecond after the database
committed. Neither test proves real database crash recovery, storage
durability, connection failover, nor six-provider post-COMMIT network
ACK loss.

## Remaining release gate

#575 remains open and v3.1.0 stays blocked. Native SQL cancellation,
true COMMIT/network ambiguity under packet loss, database-side
connection pools, process death after COMMIT and before Admin Store
acknowledgement, and cross-provider recovery are not closed by this
bounded correction. Maintain the exact-HEAD **18/18 GitHub Actions
Tests jobs**, required native PostgreSQL live test with no skipped
cases, and SqlCore baseline comparison before merging.
