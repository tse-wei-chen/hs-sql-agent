# Oracle 26ai — real physical Custom MCP DML SQL lease/load/cancellation (#575)

PR #598 extends the published Custom MCP operation-limit physical proof from SQLite (#583), PostgreSQL 16 (#595), MySQL 8.4 (#596) and SQL Server 2022 (#597) to **real Oracle 26ai**. It uses first-party `OracleProvider`, the production F# SqlCore compiler, typed DML preview/approval, native Oracle.ManagedDataAccess ADO.NET connections, commands and transactions, and the published `archive_order` Custom MCP execution entrypoint. SQL execution is not mocked.

## Setup and isolated physical witness

Each test creates a unique `SQLCORE.OL_*` table in the existing Oracle Free PDB, with `id NUMBER(10,0) PRIMARY KEY` and `note VARCHAR2(64) NOT NULL`. Physical table creation and `DROP TABLE ... PURGE` use separate native ODP.NET administrative connections **outside** the approved two-statement DML transaction because Oracle DDL implicitly commits. Container startup readiness uses the same bounded OracleException retry as #591. The runtime context uses the live `SYS_CONTEXT('USERENV','CON_NAME')` PDB identity rather than assuming the connection string's source name. The separate Admin Audit Store is a file-backed SQLite database.

## Three-phase admission and cancellation matrix

A transparent `DbConnection`/`DbCommand`/`DbTransaction` decorator forwards actual Oracle SQL, commits and rollbacks, and instruments only first-party target provider factory creations and native physical opens/closes. After the native `OracleConnection.OpenAsync`, a test gate pauses at one of three points: the first runtime-profile preflight target connection, the first preview/metadata target connection, and the **next target physical open after the approval callback**. The last gate is explicitly armed at approval instead of assuming that Oracle opens the same ordinal number of connections as other providers.

With `MaxConcurrentSql=1`, the accepted Custom MCP DML request holds one real target connection and one SQL admission lease. **24 concurrent competing published MCP invocations** all must return `server.busy`, with no additional instrumented target factory creates; peak instrumented open target connections must remain at most one. After releasing the paused native-open gate, the admitted two-statement Oracle transaction must succeed, and a separate independent Oracle driver connection must see exactly **two committed rows**. All measured target connections and SQL admission leases must be released, and a new lease must be immediately acquirable.

Three additional tests cancel the accepted operation at those same three native-open gates. The published Custom MCP response must not claim success, independent Oracle witness reads must show **zero committed target rows**, instrumented target opens and the lease must return to zero, and a new admission must be possible immediately.

## Mandatory CI, safety boundaries

The existing `sqlagent-oracle-ddl-parity` job runs the dedicated `OraclePhysicalLoadIntegrationTests` filter against the real pinned `gvenzl/oracle-free:23.26.3-slim-faststart` image with `TEST_ORACLE_CONNECTION`. The general `server-test` excludes this suite to prevent passing by silent external-database skips; missing Oracle credentials in CI cause an explicit failure. Existing Oracle synthetic post-native COMMIT/ROLLBACK ACK loss, consumed-approval no-replay and persisted Audit proof #591 remains separate.

`MaxConcurrentSql` is an **admitted operation bound**, not a universal physical database-session or pool-size guarantee. Only target connections opened through this instrumented provider in the bounded published Custom DML paths are measured. Oracle setup, cleanup, independent witness, Admin Audit, unrelated server sessions/pools and other processes are not counted. The connection string disables pooling for clear native-open observation. This does not simulate real TCP disconnections, process crashes, Oracle failover, distributed semaphore behavior or native Query function side effects; Firebird physical load remains unproven.

**Issue #575 remains OPEN and 3.1.0 publication remains BLOCKED** pending remaining Firebird physical concurrency cases, broader Query function safety and release-evidence audits.
