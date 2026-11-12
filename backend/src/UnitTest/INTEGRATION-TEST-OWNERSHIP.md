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

The machine-readable `.github/test-suites/server-integration.tsv` is the
**single source** for Server's excluded test classes and their positive job
owners. `.github/workflows/test.yml` owns their actual invocations. The
`audit-server-integration-ownership.sh` script validates both and prints the
default negative filter with `--default-filter`; the CI job never hand-edits
an exclusion string. A class with methods split by database dialect has
multiple explicit positive owners in the manifest.

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

## Adding or moving a Server physical suite

1. Give the test a class name describing the database/fault boundary. The
   default Server test shard includes new classes **unless** they appear in
   the manifest; do not depend on a CI skip to hide a missing dependency.
2. If the suite needs a real external service, add a tab-separated
   `class<TAB>owning-job<TAB>positive-selector` row to the manifest. For
   provider-specific methods in one class, add one row for **each** provider
   job, as for `QueryNativeFunctionLiveTests`.
3. Add or retain that exact `FullyQualifiedName~...` positive filter under
   the selected job in `.github/workflows/test.yml`. The audit deliberately
   checks the owning job body rather than a coincidental selector elsewhere.
4. For the destructive PostgreSQL 16 / Redis 7 Testcontainers suite the job
   invokes `require-physical-test-cases.sh 4`, which refuses zero matches,
   unexpected test-count changes, failures, and skips. Update the count only
   when the intentional physical corpus changes and verify real Docker logs.
5. Run `bash .github/scripts/test-server-integration-ownership-audit.sh` to
   verify the ownership guard rejects seven deliberate broken CI/manifest
   scenarios, rather than only passing a valid manifest. This is mandatory
   in the `server-test` CI job and does not mutate checked-out sources.
6. Run `bash .github/scripts/audit-server-integration-ownership.sh` and
   `bash .github/scripts/audit-server-integration-ownership.sh --default-filter`
   locally, then verify all 18 required Tests jobs and SqlCore baseline on the
   **exact final PR SHA** before merging to `develop`.

## Remaining cleanup and security work

- The Server YAML negative-filter wall has been replaced by the audited
  ownership manifest. Longer-term, migrate to assembly/class traits or
  dedicated physical test projects **with identical test case counts**;
  manifest entries remain required until that migration is measured.
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
