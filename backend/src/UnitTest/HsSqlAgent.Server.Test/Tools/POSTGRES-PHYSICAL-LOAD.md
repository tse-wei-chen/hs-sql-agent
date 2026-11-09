# PostgreSQL 16 — published Custom DML physical concurrency and cancellation (#575)

This is issue #575 / PR #595, extending the SQLite-only physical concurrency evidence from PR #583 to **real PostgreSQL 16** without rewriting the F# SQL compiler, transaction coordinator or SQL lease implementation.

## End-to-end, independently observed path

`PostgresPhysicalLoadIntegrationTests` drives the published `archive_order` Custom MCP Tool with a real `PostgresProvider`, F# SqlCore parsing/validation, two-statement typed preview/approval, and native Npgsql SQL execution. A transparent `DbConnection` / `DbCommand` / `DbTransaction` adapter forwards actual PostgreSQL SQL and transaction calls while instrumenting factory creations and **native target physical open/close**. The test deliberately pauses at three controlled phases:

- Runtime-profile preflight — the first physical target connection opening.
- Preview/metadata — the second physical target connection opening.
- Approved atomic transaction — the next physical target connection after the independent approval callback. This is observed after approval rather than assumed to be the sixth target open.

All three phase cases enforce `MaxConcurrentSql=1`: while one admitted request holds the lease and has an open target connection, exactly 24 concurrent MCP requests must be rejected with `server.busy`; the provider factory creation counter must not change, the measured peak instrumented target concurrency must not exceed one, and the active SQL lease must remain exactly one until the paused request is released. The accepted request then commits both PostgreSQL INSERTs; an **independent Npgsql connection** must read two physical rows, and the SQL lease and instrumented target connections must drop to zero.

Three additional cases cancel the accepted request at those same physical-open gates. The cancellation must not yield a false success or any real PostgreSQL insert. An independent witness must see **zero target rows**, no instrumented target connection remains open, and a new SQL lease must be immediately acquirable.

## CI and boundaries

A dedicated mandatory-live `sqlagent-test` job step uses the existing PostgreSQL 16 service (`TEST_POSTGRES_CONNECTION`); a missing test database is an explicit CI failure, not an implicit skip. The generic `server-test` shard excludes this live-only suite. The existing PostgreSQL lost COMMIT/ROLLBACK acknowledgement and persisted Audit proofs remain covered separately by PR #588.

These counts cover **target physical connections created through this decorated first-party provider for the tested published Custom DML paths**. Independent PostgreSQL verification/setup/cleanup connections, the separate SQLite-backed Admin Audit Store and any additional production service traffic are outside the measured target connection set. `MaxConcurrentSql` is an admitted-SQL-operation limit, not an asserted global physical DB pool limit, all-provider guarantee or cross-instance Redis/distributed semaphore proof. The test does not simulate actual network disconnect, driver crash, PostgreSQL failover or delayed SQL after a COMMIT attempt. It does not cover MySQL/SQL Server/Oracle/Firebird connection/load behavior.

**Release gate:** #575 remains OPEN and 3.1.0 publication stays blocked pending remaining multi-provider physical load/Query effect proofs and release-wide vulnerability/skip evidence.
