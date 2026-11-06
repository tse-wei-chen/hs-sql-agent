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
- `pg_catalog.query_to_xml` can run a hidden SELECT over a disallowed relation,
  even inside a read-only transaction. An independent PostgreSQL test-first RED
  execution reproduced the secret leak. Policy now rejects this native call.
- A qualified SQL-language STABLE UDF selecting from the forbidden table must
  be denied by the Query policy; a same-name user UDF that shadows LOWER in
  search_path must not override the explicitly pg_catalog-bound safe function.
- Positive controls assert a malicious-looking quoted literal is sent as a
  typed parameter and remains queryable; ordinary OR and CTE forms still work.
- Query result rows, actual user data, secret count, added columns, created
  objects, and sequence `last_value/is_called` are checked from a separate
  database connection after each attempt.

The read-only transaction protects PostgreSQL persisted changes even when the
compiler supports a native opaque function name. It is **not** a general-purpose
PostgreSQL sandbox (advisory locks and out-of-band effects require separate
privilege/resource controls), nor is this a claimed live proof for other providers.


## PostgreSQL operator search_path shadowing (G31)

PostgreSQL resolves **operators as well as functions and types** through a
configurable schema search path. A client connection that explicitly puts a
writable schema before `pg_catalog` can select a user-defined `=(int4,int4)`
over built-in integer equality even if all visible table sources are allowed.
A SQL-language function behind that operator can read an excluded table in a
READ ONLY transaction; the function-name policy alone cannot inspect or deny
this hidden operator body.

`ShadowedEqualityOperator_CannotLeakExcludedTableThroughApprovedQuery` is a
physical PostgreSQL 16, first-party Host regression. The test creates the
excluded-table-reading operator, independently verifies the overloaded
operator resolves to TRUE under attacker-first search_path, then runs an MCP
Query over two allowed aliases where ordinary integer equality must be FALSE.
The original RED CI failed precisely on the forbidden secret-derived TRUE.
It also checks snapshots and checks that a deliberately single-connection,
no-pool-reset session retains its original path **after Host rollback**.

GREEN fixes only the **existing Host Query execution transaction**. After
`SET TRANSACTION READ ONLY`, it uses schema-qualified `pg_catalog` functions
to transaction-locally prepend `pg_catalog` to the existing search_path
before running compiled SQL. Existing relation path entries remain in order,
so allowed non-system tables remain accessible; a rollback restores the
original connection setting, including for pooled connections. No user SQL
is interpolated into the Host's search-path command.

This is **not** a proof of safety for arbitrary user-defined data types,
operators with no pg_catalog equivalent, SQL inside functions, malicious
SECURITY DEFINER code, out-of-band effects or relations hidden by an explicitly
chosen schema path. Enforce least-privilege physical DB credentials and
CREATE restrictions on schemas that Query sessions can resolve.
