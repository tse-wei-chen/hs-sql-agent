# Firebird 5 — physical Custom MCP DML admission, load and cancellation (#575)

PR #599 completes the bounded physical Custom MCP admission test matrix for the sixth supported database. It extends SQLite (#583), PostgreSQL 16 (#595), MySQL 8.4 (#596), SQL Server 2022 (#597), and Oracle 26ai (#598) to a real pinned **Firebird 5.0.4** service, but the measured guarantee is intentionally limited.

## Physical target and production execution

The tests invoke the actual published `archive_order` Custom MCP DML tool and the production F# SqlCore compiler, typed two-statement DML preview, external approval and native Firebird transaction coordinator. A transparent `DbConnection`/`DbCommand`/`DbTransaction` adapter forwards SQL execution, COMMIT and ROLLBACK to `FirebirdSql.Data.FirebirdClient`, counting only first-party instrumented **target** connection opens/closes and factory creations. No fake or in-memory SQL transaction is used.

Each case creates a uniquely named `FL_*` physical Firebird table with an integer primary key and VARCHAR note column. Table setup and cleanup are outside the approved two-statement DML transaction. The native Firebird database identity is read with `SELECT TRIM(MON$GUID) FROM MON$DATABASE` and passed to the published MCP context, matching the existing physical Firebird lost-ACK test. Admin Audit is a separate file-backed SQLite database; independent physical `FbConnection` readbacks verify target rows.

## Six mandatory-live cases

At each of three physical points, the accepted Custom MCP request is paused **after a native `FbConnection.OpenAsync` succeeds**: (1) runtime-profile verification, (2) first typed preview/metadata connection, (3) first target physical connection opened **after approval callback**. The third gate is dynamically armed rather than assuming every SQL provider uses the same connection ordinal.

For each gate, with `MaxConcurrentSql=1`, one accepted request owns the lease and a real target connection. **24 competing published MCP calls** must all return `server.busy`, without creating a single additional instrumented target provider connection. The measured open-target peak must be <=1 and active SQL lease one until the original is released. Releasing the gate lets two approved INSERTs COMMIT in a real Firebird transaction; a separate real Firebird witness connection must see **two persisted rows**, and instrumented target connection/lease counts must return to zero with immediate lease reacquisition.

Three cancellation cases stop the accepted published MCP request at the same physical-open gates. They must not return false success; independent native Firebird witness readback must show **zero committed rows**, no instrumented target connection/lease can remain, and fresh SQL admission must be possible.

## CI and scope

The existing `sqlagent-firebird-ddl-parity` job runs `FirebirdPhysicalLoadIntegrationTests` against pinned `firebirdsql/firebird:5.0.4-bookworm`, with `TEST_FIREBIRD_CONNECTION` mandatory in CI; missing live setup fails rather than skipping. The generic `server-test` shard excludes this suite, preventing a bogus passing test when no DB is provided. Prior Firebird synthetic post-native COMMIT/ROLLBACK-ack lost proof, non-retryable MCP terminal status, consumed approval and persisted Audit proof remain independently covered by #592.

`MaxConcurrentSql` is an admitted-operation limit, **not** a global Firebird connection/session or pool-size guarantee. Only connections created via the decorated first-party target provider in the tested published Custom MCP DML paths are measured. Physical fixture setup/cleanup and independent witness connections, SQLite Admin Audit, server internal connections, other pools/processes, and distributed Redis limiters are outside the measured counter. Test connections set `Pooling=false`. No real network fault, process crash, Firebird failover, or arbitrary native Query UDF side effect is proven here.

**#575 and 3.1.0:** This does not close the open cross-instance/transport, six-provider Query function-effect, or release-evidence gates; 3.1.0 publication remains blocked until all security gate criteria are met.
