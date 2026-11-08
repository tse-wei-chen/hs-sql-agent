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

## SQL Server 2022, Oracle 26ai, Firebird 5: real native Query-function identity proof (#585)

`QueryNativeFunctionLiveTests.cs` runs against the existing, versioned SQL Server,
Oracle, and Firebird CI services. Each case creates two **physical** relations:
a users table authorized by the MCP table whitelist and a secrets table that
is not authorized. The test registers a native SQL/PSQL scalar function whose
body reads the excluded secrets relation.

1. An independent direct provider command **must** return the sentinel through
   the function. This positive unsafe control proves a real hidden table read
   is physically possible, not a mock/query-text assumption.
2. The typed F# compiler must reject the qualified/quoted native function with
   `SQL_POLICY_NATIVE_QUERY_FUNCTION_NOT_PROVEN` at the Policy stage.
3. The published MCP `ExecuteQuerySql` Host path must return failure and no
   secret-bearing rows; a normal SELECT over the allowed users relation must
   still execute, and independently measured physical user/secret counts must
   be unchanged.
4. Each test is explicitly selected in its dedicated provider CI Job. Missing
   live connection configuration **fails** in CI. The general Server Job excludes
   these external-service tests; their absence from that generic job does not
   count as physical proof.

**Proof boundary:** these three experiments cover specifically **qualified /
quoted** UDF identities under an explicit table whitelist. They do not prove
ordinary unqualified function shadowing, opaque side effects from unwhitelisted
Query-only grants, or physically enforced read-only database roles across
providers. PostgreSQL, MySQL and SQLite physical evidence is tracked separately.
The broader #575 3.1.0 release blocker remains open.

## SQLite: built-in function shadowing on the physical Query connection (#586)

SQLite permits embedded hosts and loadable extensions to override a built-in
function with `CreateFunction`, even when the F# SQL plan uses an unqualified,
canonical built-in such as `ABS(id)`. An approved table whitelist cannot see
relations accessed inside the callback. A real on-disk SQLite RED test proves
that `ABS` can retrieve an excluded `main.secrets` sentinel and that the
published MCP `execute_query_sql` previously returned it.

Host Query now reads `PRAGMA function_list` on the **same open SQLite connection**
before executing compiled SQL, compares native and application-defined function
names and overlapping calling arities in managed code, and fails closed if any
built-in identity is shadowed. This guard is applied **regardless of whether
the query carries a table whitelist**, since Query-only keys without a whitelist
are still not authorization to execute arbitrary callbacks.

The check deliberately rejects the entire Query on a connection with a
colliding built-in signature: the compiler cannot prove which nested SQL,
operator or view might dispatch an overriden callback. This conservative
behavior impacts only host connections with a registered built-in override.
Separate real SQLite regression controls retain working `ABS` on the default
provider, permit an unrelated noncolliding custom function registration,
and require no-whitelist Query to fail closed.

**Boundary:** the attestation catches registered same-name/same-arity SQLite
built-in overrides on the active connection. It does **not** prove arbitrary
non-built-in callbacks, virtual tables, secondary connections opened by
callbacks, dynamic registrations after attestation, or read-only database-role
semantics. It is not a six-provider physical read-only guarantee; #575 remains
a release blocker.

## Oracle 26ai / MySQL 8.4 unqualified built-in versus schema-owned UDF (#587)

The independent physical test-first hypothesis on Oracle 26ai was initially
**falsified**: a stored SQLCORE.ABS(NUMBER) function reading an excluded
relation existed, but real unqualified SQL ABS(ID) evaluated to native
numeric 1, not to the excluded sentinel (initial CI run 42463244597).
This result is *not* a vulnerable RED result and required **no** runtime
or compiler rejection of ordinary Oracle ABS.

The corrected real-provider differential tests on **Oracle 26ai** and
**MySQL 8.4** each build two relations: approved users and excluded secrets,
plus a same-name native/stored ABS function that physically selects the
secret. On the **same real database and connection**:

- Direct unqualified ABS(ID) resolves to the intrinsic built-in and returns
  numeric 1. The published MCP Query path must still execute this permitted
  SQL when the table whitelist allows users.
- Direct explicitly qualified SQLCORE.ABS(ID) (Oracle) or
  database.ABS(ID) (MySQL) returns the excluded secret from the
  user-defined function. The typed F# Policy and published MCP Query must
  reject that same qualified function, returning no secret-bearing rows.
- Independent physical user/secret counts remain constant before and after
  MCP calls. The Oracle and MySQL tests live in their dedicated versioned
  CI service jobs. Missing database configuration fails in those jobs.

**Proof boundary:** this establishes only the tested ABS name and argument
signature on two engine versions; it cannot be extrapolated to every builtin,
unqualified procedure, package, synonym, security-definer function, all six
providers, or general Query-only side-effect safety. The #575 release gate
remains open until those independent boundaries and DML evidence are closed.
