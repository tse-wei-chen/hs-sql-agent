# Redis SQL lease revocation and Query fail-closed (#575)

## Confirmed physical defect

The distributed SQL limiter stores token deadlines as millisecond Unix timestamps
in a Redis sorted set. Before this change, its `RenewScript` checked only
`ZSCORE` existence: **it did not compare the score to Redis server TIME**.

A token can remain in a sorted set after its score has expired when no
subsequent acquire prunes it. A late renew would assign it a future deadline,
resurrecting an invalid SQL admission. The required real Redis 7
`ExpiredRedisLease_CannotBeResurrectedByLateRenewal` regression was committed
first; [CI run 42804653081](https://github.com/tse-wei-chen/hs-sql-agent/actions/runs/42804653081)
failed at this exact physical assertion on the old script (**RED**, actual
Redis 7, not a compilation failure).

Now Redis atomically verifies `ZSCORE > TIME` before renewing, and removes a
stale member instead of resuscitating it. A new contender never inherits
the old token identity.

## Bounded execution revocation

An active Redis lease implements an **internal** `IRevocableSqlExecutionLease`
contract. The lease's `RevocationToken` is canceled:

- when the renewal Lua script rejects a missing/expired token;
- when the Redis renewal fails and the lease cannot be verified; or
- at a local deadline if the Redis request hangs and has not renewed in time.

An on-time positive renewal resets the local watchdog. Once canceled,
the token is never restored by a late Redis reply.

`SqlExecutionLeaseCancellation` links the caller's cancellation with that
revocation for **read-only Query** execution. The host passes the linked token
through all three Query entrypoints: built-in MCP `execute_query_sql`,
published Custom MCP Query, and Admin Custom Query. In addition to asking the
provider/driver to cancel, the host checks the token after awaited execution
and rejects a late successful provider result. Memory/third-party
`IAsyncDisposable` limiter implementations retain their existing caller token
without changing the public `ISqlExecutionConcurrencyLimiter` CLR contract.

Unit tests exercise deterministic denied renewal, stalled Redis reply and
late provider success despite cancellation. Required real Redis 7 tests
exercise actual expired-score behavior and the existing two-instance,
24-contender healthy-Redis proof.

## Explicit limitations and remaining release gates

This is **best-effort Query execution revocation**: forwarding a canceled
token does not prove the physical database immediately terminates a native
statement, especially during driver/network failure. A provider that ignores
cancellation can still execute work on the server until its timeout or
connection disposal; the host now fails closed on the result.

The **DML COMMIT** path is deliberately not bound to a generic canceled
admission token. After a COMMIT attempt, cancellation cannot establish whether
the database committed or rolled back; existing unknown-outcome reconciliation
and approval evidence must be preserved. The DML lease-loss semantics,
six-provider native-command interruption, multi-process Redis network
partitions and production pool sizing remain open #575 release gates.

Redis `FailOpen` remains an explicitly permissive configuration during
admission failures; use `FailClosed` and database-side resource caps for
strict installations. The local watchdog is not a globally coordinated
fencing token. No change was made to the F# AST or supported SQL grammar.

Keep issue #575 OPEN and v3.1.0 publication blocked until all remaining
release gates have complete evidence. Merge this bounded PR only after
exact-head 18/18 Tests Jobs (including Redis service) and SqlCore baseline
comparison are green.
