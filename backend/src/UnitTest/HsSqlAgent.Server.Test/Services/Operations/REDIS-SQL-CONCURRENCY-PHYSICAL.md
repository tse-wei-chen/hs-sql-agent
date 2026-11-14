# Redis 7 SQL admission: canceled in-flight requests and cross-instance contention (#575)

## Confirmed race (RED → GREEN)

The Redis SQL limiter uses an atomic Lua script to reserve a sorted-set
token, but StackExchange.Redis `IDatabase.ScriptEvaluateAsync` has no
request cancellation parameter. Previously `TryAcquireAsync` checked
`CancellationToken` **only before** sending the script:

1. Request A sends the Lua acquire command and blocks waiting for Redis.
2. Request A is canceled.
3. Redis returns a late successful acquire (or full-capacity refusal).
4. The old limiter would return a live lease (or `null`) instead of
   cancellation. The live lease could outlive the original request and
   unnecessarily consume capacity until disposed/expired.

Test-first PR #601 CI **RED** exercised both deterministic reply orders in
`RedisSqlExecutionConcurrencyLimiterTests` and observed two real failures.
The production code now checks cancellation **after** the Redis response:
a late successful admission releases **only its own exact Redis token** via
the existing atomic release script, then throws cancellation. A late denied
reply throws cancellation without touching other tokens. A Redis exception
during cancellation never activates `FailOpen` and is reported as
cancellation instead.

If the cleanup command itself fails due to Redis outage, the existing
best-effort release logs the error. The orphan token may live until its TTL;
no false claim of guaranteed instantaneous cleanup during an outage.

## Required Redis 7 physical proof

`RedisSqlConcurrencyIntegrationTests` runs in the existing required
`redis-approval-integration` GitHub Actions job, with a real Redis 7
service. The ordinary `server-test` job excludes it, so missing Redis
can never silently pass the required real-provider step.

The test creates **two separate limiter objects**, each with an independent
Redis connection, using the same unique Redis sorted-set key and a shared
`MaxConcurrentSql = 1` policy:

- Instance 1 obtains the one lease, with **2-second TTL**.
- After **5 seconds** (several TTL periods), physical Redis still reports
  one lease because the background renewal has maintained it.
- Twenty-four competitors across both instances simultaneously attempt
  admission; all 24 must be denied without a second live token.
- Disposal removes the Redis token; instance 2 can acquire/release normally.

This proves cross-instance coordination for two independent connections
in one real Redis service under **healthy communication**, not a multi-host
network partition or process crash simulation. The Redis sorted set is
checked independently from the limiter's return values.

## Release boundaries deliberately left open

- Redis outages longer than lease TTL can cause the lease to expire while
  the original SQL statement is still executing; the current
  `IAsyncDisposable` lease API has no execution cancellation/revocation
  contract. A fresh admission after expiry does **not** establish a
  production-wide physical connection bound under partition.
- The explicitly configurable `FailOpen` policy permits new admission
  during Redis outages. Use `FailClosed` for strict security
  deployments, plus database-side connection/pool limits.
- The test does not include two OS processes, a real network partition,
  multi-worker crash, or database physical pool size; those remain
  separate #575 release gates.
- No F# SqlCore, SQL compiler dialect support, MCP tool count, DML approval
  or durable state schema changed. This PR alone cannot unblock 3.1.0.

Merge only on exact-head **18/18 Tests jobs** plus **SqlCore baseline**
success, and keep #575 OPEN.
