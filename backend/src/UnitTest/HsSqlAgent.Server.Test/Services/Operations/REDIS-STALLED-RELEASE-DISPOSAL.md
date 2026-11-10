# Redis SQL admission: bounded release ACK cleanup after target COMMIT (#575)

## Confirmed scope and RED evidence

A previous change (#606) prevents a stuck *renewal* acknowledgement from
blocking lease disposal. The remaining `ReleaseAsync(token)` path still
awaited the Redis `ZREM` Lua acknowledgement without a cancellation deadline.

When the database **already acknowledged a native COMMIT**, returning the SQL
or DML outcome may still pass through `await using` of the admitted SQL
lease. If the independent Redis cleanup ACK never comes, the target operation
is physically committed but the response may never be delivered, and an
asynchronous durable approval may not reach its final Admin Store state.

Tests were committed **before** the fix and run against the old implementation:

- [RED Tests #1, run 42848892510](https://github.com/tse-wei-chen/hs-sql-agent/actions/runs/42848892510)
  reproduced a real SQLite INSERT + COMMIT with an unacknowledged Redis
  release call. The server job had **921 passed, 1 failed**. The first Redis
  job did not discover the new test because the CI uses an explicit class
  filter; it is not counted as Redis proof.
- [RED Tests #2, run 42849380114](https://github.com/tse-wei-chen/hs-sql-agent/actions/runs/42849380114)
  fixed the class filter by adding the Redis cases to the existing
  `RedisSqlConcurrencyIntegrationTests` partial class. The required
  **real Redis 7** job executed both new cases and reported **12 passed,
  2 failed, 0 skipped**; the server job separately reported **921 passed,
  1 failed, 0 skipped**. These are behavioral timeout failures, not builds.

The real Redis cases cover two physical execution orders:

1. Redis 7 executes the exact-token ZREM but the application never receives
   the reply. The client must finish local cleanup and allow a different
   limiter instance to acquire without waiting for the ACK.
2. Redis 7 does **not** execute the old ZREM before the client cleanup
   deadline. The old admission remains until its server-side lease score
   expires and the next acquisition prunes it. When the old ZREM finally
   executes, it must **never remove the new owner's different token**.

## Correction and its limitations

`ReleaseAsync` now uses one independent, **two-second local deadline**
for Redis connection acquisition and the exact-token release script's
reply. If it expires, the caller stops waiting and emits a warning that
the **release acknowledgement is unknown**; no claim is made that Redis
performed the release. The Lua operation is not canceled, rewritten,
retried or sent to another key. The existing Redis `ZREM` remains scoped
to the original random admission token and the existing sorted-set
expiry/score checks prevent reusing an expired token.

The timeout only concerns **post-SQL cleanup**, not native SQL/DML
cancellation, the physical COMMIT command or any durable target-side
witness. A positively acknowledged database COMMIT remains committed;
a truly ambiguous native COMMIT/ROLLBACK retains its unknown-outcome
error classification and non-replayable approval semantics.

This does **not** prove a genuine network partition or process crash, nor
does it guarantee immediate Redis capacity reclamation while Redis is
unreachable. In that case Redis's score/TTL bounds eventual reclamation,
and admission may temporarily be rejected. This also does not assert
native-command cancellation on six DB providers or globally enforced
physical connection pools. These remain open gates in #575.

No public CLR limiter API, SQL dialect capability, F# compiler, MCP
tool name or DML approval schema changed. Do not publish v3.1.0 on this
isolated fix. Merge only on exact-HEAD **18/18 Tests Jobs**, **real Redis 7
with zero skipped tests**, and **SqlCore baseline** success.
