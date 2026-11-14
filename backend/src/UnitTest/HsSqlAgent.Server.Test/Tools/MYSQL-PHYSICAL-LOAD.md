# MySQL 8.4 InnoDB — real physical Custom DML SQL admission/load and cancellation (#575)

PR #596 continues the real PostgreSQL 16 physical published Custom DML concurrency proof from #595 using **MySQL 8.4**. The checks exercise actual `MySql.Data.MySqlClient` connection, command and native transaction behavior; only administrative mocks and instrumentation are used. The SQL itself, F# SqlCore parsing, typed preview/approval, Custom MCP proxy and DML execution route remain the same production implementations.

## Physical experiment

Each test creates a unique MySQL database, with a real `items` InnoDB table (`id INTEGER PRIMARY KEY`, `note VARCHAR(64) NOT NULL`) and a separate file-backed SQLite Admin Audit Store. MySQL `CREATE DATABASE` and `CREATE TABLE` are completed **before** the approved DML transaction, and `DROP DATABASE` happens after the test, since DDL would otherwise implicitly commit.

A transparent provider connection decorator counts factory creation and target native physical opens/closes. It forwards every actual command and transaction to `MySqlConnection`, `MySqlCommand` and `MySqlTransaction` and pauses after native `OpenAsync` at one of three gates: runtime-profile verification, first preview/metadata read, and first physical open after external approval (the post-approval stage is explicitly armed rather than assuming a universal provider connection ordinal).

At each gate, with `MaxConcurrentSql=1` and the admitted operation holding a native target connection, exactly **24 competing published MCP requests** must return `server.busy` without causing further target factory calls. The instrumented open-target peak must remain at most one, and the active SQL lease must remain one until release. On release, the admitted two-statement atomic transaction must succeed, an independent native MySQL connection must see exactly two inserted rows, and the target/lease counters must return to zero. A fresh lease must be immediately acquirable.

Three additional cases cancel the in-flight published Custom DML request while it is held at each physical-open gate. The response must not claim success; an independent MySQL witness must observe **zero** inserted rows. The physical target connection and lease counters must return to zero, and new admission must be possible immediately.

## Mandatory CI and scope limits

GitHub Actions `sqlagent-test` uses the existing MySQL 8.4 service with `TEST_MYSQL_CONNECTION` and runs `MySqlPhysicalLoadIntegrationTests` explicitly. Missing configuration in CI is a **failure**, not a skip. The general `server-test` excludes this live-only test class, preserving meaningful physical evidence rather than a simulated or skipped pass. Previous synthetic lost-COMMIT/ROLLBACK ACK and persistent Audit evidence from #589 remain separate.

`MaxConcurrentSql` limits admitted SQL operations; **it is not a universal connection-pool limit**. The measurements cover only native target connections created through the instrumented first-party provider in these bounded published Custom DML paths. The setup/cleanup/independent witness MySQL connections, Admin Audit SQLite store, any unrelated production SQL traffic, pooled unrelated connections and distributed instances are not counted. Test connection strings explicitly disable MySQL pooling to measure each native target open/close. No true socket failure, DB failover, process crash, transaction retry after ambiguous transport loss, non-InnoDB engine or multi-provider load proof is claimed.

**Release:** Issue #575 remains open, and 3.1.0 remains blocked until the remaining SQL Server, Oracle, Firebird physical admission/load cases and six-provider Query effect and release-evidence gates have been independently satisfied.
