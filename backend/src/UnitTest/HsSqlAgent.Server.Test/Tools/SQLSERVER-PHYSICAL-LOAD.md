# SQL Server 2022 — physical Custom MCP DML lease/load and cancellation (#575)

PR #597 extends bounded real SQLite (#583), PostgreSQL 16 (#595) and MySQL 8.4 (#596) physical Custom DML contention proofs to **real Microsoft SQL Server 2022**. It exercises the published `archive_order` Custom MCP tool with the production F# SQL compiler, `MsSqlServerProvider`, native SqlClient SQL commands/transactions and the typed two-statement approval runtime. It is not an in-memory transaction or SQL executor mock.

## Test fixture and physical instrumentation

Each of the six tests creates a uniquely named `dbo.load_*` physical SQL Server table with a primary key and an NVARCHAR note column on the dedicated real SQL Server 2022 CI service. The `master` test database and startup-readiness retry match existing real-provider tests. Table setup/cleanup uses a different SqlClient connection and is outside the approved DML transaction. Independently persisted Admin Audit uses a file-backed SQLite store.

A transparent `DbConnection`/`DbCommand`/`DbTransaction` instrumenter forwards native `Microsoft.Data.SqlClient` SQL and transaction operations, counting physical target opens/closes and provider factory creations. The pause occurs after native `OpenAsync`, allowing the accepted Custom DML operation to hold the real physical connection and its SQL lease in one of three phases: (1) runtime profile verification, (2) first live metadata/preview connection, or (3) the **next physical target connection after external approval**. The last phase is armed by approval callback rather than assuming a fixed number of opened connections.

## Bounded test matrix

- For each phase, set `MaxConcurrentSql=1`, begin one accepted Custom MCP operation and hold it at the native-open gate. Invoke 24 additional published Custom MCP tools using the *same* SQL admission limiter. Every competing response must contain `server.busy`, and their combined calls must create **no new instrumented target provider connections**. Measured target peak <=1 and lease count stays one until release. Following release the accepted two-statement DML must report success and insert exactly two rows, verified with a separate real SQL Server witness connection. All target connections and SQL leases must return to zero; a fresh lease must be acquirable.
- Repeat the same three gates with cancellation rather than competitors. The MCP request must not report success; an independent real SQL Server witness must see zero committed target rows, target open connection count and active lease both return to zero, and the admission limiter immediately accepts a new lease.

## CI and limits

These tests execute in a dedicated mandatory-live step of `sqlagent-fence-sqlserver` against the existing `mcr.microsoft.com/mssql/server:2022-latest` service with `TEST_SQLSERVER_CONNECTION`. Missing configuration fails CI rather than skipping; the regular `server-test` shard excludes this live test class. Existing SQL Server synthetic post-native COMMIT/ROLLBACK-ACK loss, no-replay and persisted Audit proof #590 remains independently checked.

The physical connection counter covers **only target connections created through this decorated first-party Provider in the tested Custom MCP DML path**. Separate table setup/cleanup and witness SqlClient connections, the SQLite-based Admin Audit Store, unrelated services, SQL Server background connections, other connection pools and distributed instances are not included. The test connection string disables SqlClient pooling for unambiguous opens/closes. `MaxConcurrentSql` limits **admitted SQL operations**, not global database sessions. It does not prove physical transport disconnect, SQL Server failover, SQL CLR side-effect safety, distributed Redis rate/admission or Oracle/Firebird performance.

**Issue #575 remains Open; 3.1.0 stays blocked** until the remaining Oracle/Firebird physical concurrency cases, Query-function safety and release-evidence gates receive independent proof.
