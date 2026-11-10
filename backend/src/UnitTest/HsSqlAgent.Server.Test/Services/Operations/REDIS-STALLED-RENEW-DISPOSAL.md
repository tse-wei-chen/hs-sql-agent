# Redis SQL lease disposal with stalled renewal acknowledgement — #575

## Security-relevant boundary

`RedisSqlExecutionConcurrencyLimiter.RedisLease.DisposeAsync()` previously
canceled the periodic renewal timer but then **awaited the in-flight Redis Lua
renewal response** before issuing its exact-token release. The
`StackExchange.Redis.IDatabase.ScriptEvaluateAsync` overload does not accept
a `CancellationToken`. If a renewal ACK is stalled/lost, disposal never
reaches `ZREM`. In the DML tool/approval flows, an `await using` lease can
therefore prevent an **already-acknowledged target COMMIT** from progressing
to the caller's terminal response or the durable Admin Store outcome write.

Do not confuse that secondary Redis I/O problem with a target COMMIT failure.

## Actual RED proof

The following tests were first added against the old implementation:

- [RED CI 42816846298](https://github.com/tse-wei-chen/hs-sql-agent/actions/runs/42816846298):
  `server-test` failed specifically
  `DisposeAsync_StalledRenewalAfterPhysicalSqliteCommit_StillReleasesLease`
  (**920 passed, 1 failed, 0 skipped**). A **real SQLite transaction**
  committed an INSERT while a mocked Redis client's renewal ACK was withheld;
  disposal did not finish within the bounded check.
- The same required workflow's `redis-approval-integration` job failed
  specifically `PhysicalRedis_LateRenewAckAfterLeaseDisposal_NeverResurrectsReleasedToken`
  (**10 passed, 1 failed, 0 skipped**). The test used **real Redis 7 Lua
  execution and a real Redis sorted set**, while a test client withheld its
  renewal ACK. The previous lease disposal blocked instead of releasing it.

These are controlled client-response delays, **not** a real TCP partition,
production process crash or proof of all native provider failure modes.

## Bounded correction

The periodic renewal worker now awaits
`RenewAsync(token).WaitAsync(_stopping.Token)`. During lease disposal, its
local waiting task is canceled immediately; the **already-issued Redis Lua
request is not canceled or assumed undone**. The existing `DisposeAsync`
finally block then sends the token-specific Redis `ZREM` release. No new
background processor, unbounded polling, timeout configuration or public
limiter API was introduced.

Safety relies on the existing server-atomic scripts:

- If delayed renewal Lua executes **before** release, the later `ZREM`
  removes exactly that admission.
- If Lua executes **after** release, `RenewScript` requires a live
  `ZSCORE` for the same token, so it cannot recreate the removed member.
- No other process's distinct lease token is removed by release; a second
  instance can acquire the freed capacity.

The required real Redis 7 regression now tests both orderings explicitly.
A completed Redis release does not imply that native SQL was interrupted.
Already-committed DML remains committed; `DmlCommitOutcomeUnknownException`,
`DmlRollbackOutcomeUnknownException`, target-side evidence/witness and
durable reconciliation retain their original semantics.

## Explicitly unproven / remaining release gates

If the **release** command itself is unresponsive, its best-effort cleanup can
still delay the `await using` block; Redis TTL remains the eventual
admission reclaim bound. Real Redis partition/failover, process kill after
COMMIT, target-side at-most-once fencing across crashes, six-provider native
cancellation and global physical pool caps are outside this PR.

This change provides bounded disposal against **stalled renew ACK only**.
No F# SqlCore, SQL dialect capability, MCP tool, approval schema or public
CLR API was changed. #575 remains OPEN and v3.1.0 release remains blocked.

Squash-merge only after the exact PR HEAD's complete 18 required Tests Jobs
(including real Redis 7 with no skipped cases) and SqlCore baseline comparison
both pass.
