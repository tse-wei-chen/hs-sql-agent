# Physical Host Query injection and read-only regression

This test exercises **actual MCP Host Query -> F# SqlCore -> first-party SQLite
provider -> on-disk SQLite**, rather than only asserting the compiler's AST or
mocked provider calls.

- 32 stacked query-to-mutation injection attempts (4 separator shapes x 8
  second statement kinds) must fail and preserve the full physical snapshot.
- 5 unauthorized `secrets`-table paths (direct, CTE, JOIN, UNION and EXISTS)
  must fail with no rows returned and no physical state change.
- A SQL injection-shaped **valid string literal** must remain a single bound
  parameter and remain queryable; legitimate `OR` and CTE forms must work.
- Each test uses an isolated SQLite file, disables connection pooling and
  deletes the file during cleanup. No Docker, network access or privileged
  database credentials are needed.

This is a focused SQLite runtime proof, **not** a claim that all six provider
executors or the DML approval/replay pipeline have received equivalent
physical coverage. Those are separate follow-up gates.

## PostgreSQL 16: physical host and persisted side-effect proof

`PostgresPhysicalHostInjectionTests.cs` uses a real PostgreSQL 16
Testcontainers database and the first-party `PostgresProvider`, with a separate
throwaway schema per scenario. Actual MCP `ExecuteQuerySql` enters the F# compiler
and Dapper-backed executor; direct Npgsql setup and snapshots are independent.

- 32 stacked SELECT-to-mutation requests (DELETE/UPDATE/DROP/CREATE/INSERT/
  TRUNCATE/ALTER/sequence effects) must reject before any physical change.
- Five unauthorized `secrets` references via direct, CTE, JOIN, UNION, EXISTS
  must yield no rows and preserve the physical snapshot.
- Two calls to native sequence functions (`nextval`, `setval`) must not advance
  a real persisted PostgreSQL sequence through the Query Host tool.
- Critically, a real `VOLATILE` PL/pgSQL function that updates the **allowed**
  `users` table must be refused by the PostgreSQL read-only execution fence.
  A test-first GitHub Actions run reproduced a successful, unsafe Query before
  the production fix.
- Positive controls assert a malicious-looking quoted literal is sent as a
  typed parameter and remains queryable; ordinary OR and CTE forms still work.
- Query result rows, actual user data, secret count, added columns, created
  objects, and sequence `last_value/is_called` are checked from a separate
  database connection after each attempt.

The read-only transaction protects PostgreSQL persisted changes even when the
compiler supports a native opaque function name. It is **not** a general-purpose
PostgreSQL sandbox (advisory locks and out-of-band effects require separate
privilege/resource controls), nor is this a claimed live proof for other providers.
