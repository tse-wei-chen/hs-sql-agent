# Integration test ownership and physical evidence

This inventory describes the **actual CI routing** on `develop` as of November 2026.
It is a maintenance contract, not a claim that every transport or failure mode is
covered. Do not replace physical tests with mocked acknowledgement exceptions.

## Suite ownership

| Suite | Location | CI owner | Real dependency | Evidence |
| --- | --- | --- | --- | --- |
| Ordinary Server and Host | `HsSqlAgent.Server.Test` | `server-test` | SQLite/in-process where applicable | Default filter; physical and Redis suites excluded |
| PostgreSQL 16 native faults | `Server.Test/Services/Operations/PostgresNativeFaultTestcontainersTests.cs` | `server-test` dedicated Testcontainers step | PostgreSQL 16 Docker container | Real backend termination during Query and blocked COMMIT; real native cancellation; independent witness; consumed approval |
| Redis 7 process outage | `Server.Test/Services/Operations/RedisProcessOutageTestcontainersTests.cs` | `server-test` dedicated Testcontainers step | Redis 7 Docker container | Real service stop; held lease revocation and fail-closed second instance |
| Redis approval and concurrency | `Server.Test/Services/Dml/Approvals`, `Server.Test/Services/Operations` | `redis-approval-integration` | Redis 7 CI service | Cross-instance admission and approval |
| PostgreSQL/MySQL physical DML and load | `Server.Test/Tools` | `sqlagent-test` | PostgreSQL/MySQL CI services | Native transaction outcome and load, separately from Testcontainers faults |
| SQL Server physical DML, native Query, DDL and SQL grammar | `Server.Test/Tools`, `Server.Test/Services`, `SqlAgent.Test/Services/SqlCore` | `sqlagent-fence-sqlserver` | SQL Server CI service | Native server/permission behavior and parity |
| Oracle physical DML, native Query and SQL grammar | Same project families | `sqlagent-oracle-ddl-parity` | Oracle CI service | Native behavior and parity |
| Firebird physical DML, native Query and SQL grammar | Same project families | `sqlagent-firebird-ddl-parity` | Firebird CI service | Native behavior and parity |
| SQL grammar matrices | `SqlAgent.Test/Services/SqlCore` | `sqlagent-syntax-matrix` and SQL service jobs | Compiler and provider-specific live jobs | Positive/negative grammar contracts |
| SqlCore regression baseline | `HsSqlAgent.SqlCore` and test governance | `SqlCore baseline comparison` | Exact PR/develop comparison | Preserve six-provider grammar and validation parity |

The exact job names and positive selectors in `.github/workflows/test.yml`
remain authoritative; update this inventory when CI ownership changes.

## Rules for new integration tests

1. **Name the physical boundary**: process stop, backend termination,
   network partition, connection cancellation, COMMIT ACK ambiguity, or
   application crash are distinct scenarios. A wrapper throwing after a
   completed native COMMIT is not a transport-disconnect test.
2. **Run against the actual provider** and a live database. Prefer
   Testcontainers for isolated, self-owned dependencies; keep shared CI
   services where large provider images or version-specific fixtures make
   that more practical.
3. **Use an independent physical witness** to assert committed rows,
   rollback, replay prevention, or lease state. Test assertions must fail
   if the intended failure injection did not occur.
4. **Fail rather than skip in mandatory CI** when Docker, service connection
   or the expected native failure cannot be reached. A green job with zero
   executed physical cases is not evidence.
5. **Assign exactly one primary positive CI owner** to each suite. An
   excluded test class must have a positive selector elsewhere; the
   `audit-server-integration-ownership.sh` guard enforces this for Server.
6. **Preserve existing six-provider syntax parity**. Host/driver-specific
   security policies must not silently narrow general F# SQL grammar.
7. **Separate unit tests, native integration, and destructive fault tests**
   in reporting. Run a focused RED then GREEN before expensive full CI.

## Remaining cleanup and security work

- Replace the Server default negative-filter wall with declarative
  suite traits/collections or separate test assemblies, without changing
  which tests execute. Do this incrementally with exact test-count parity.
- Consolidate repeated per-provider connection/fixture setup and test
  cleanup. Do not share mutable physical sessions between tests.
- Add independent tests for network partition via a proxy, post-durable
  COMMIT response loss, real database restart/failover and Redis failover.
  The current PostgreSQL/Redis Testcontainers cases **do not** establish
  these properties.
- Expand ownership auditing to the `SqlAgent.Test` positive and negative
  selectors; verify the exact selected test counts in mandatory CI.
- Keep issue #575 open until its separate release acceptance gates are
  actually met.
