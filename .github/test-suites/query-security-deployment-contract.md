# Host Query security boundary — six-provider supported deployment (3.1)

This is a **deployment and runtime trust contract**, not a promise that an AST
`SELECT` statement proves physical read-only execution. The first-party SQL
compiler and Host only protect the specifically modeled SQL/runtime behavior.
Host-registered callbacks, external native code, privileged-definer routines,
cross-database resources and separate connections can escape a connection-local
read-only transaction. **Do not deploy Query-only MCP keys using DB identities
that possess those capabilities.**

## Security prerequisites by provider

| Provider | Mandatory database identity boundary | Host defense and live negative/positive evidence | Unsupported deployment |
|---|---|---|---|
| PostgreSQL 16 | Dedicated NOSUPERUSER/NOCREATEDB/NOCREATEROLE/NOBYPASSRLS identity; grant CONNECT, USAGE and SELECT only to intended schemas/tables; no membership granting write or privileged routine EXECUTE; restrict search_path/schema CREATE | Same-connection native READ ONLY transaction and pinned pg_catalog resolution; 11-case mandatory Testcontainers Host Query corpus exercises sequence/UDF writes, whitelist escapes, shadowed operators, injection and legitimate CTE controls | Privileged SECURITY DEFINER/dblink/external-side-effect routines that can bypass connection-local read-only |
| MySQL 8.4 InnoDB | Dedicated SELECT-only principal on intended objects, without INSERT/UPDATE/DELETE, CREATE TEMPORARY TABLES, EXECUTE/CREATE ROUTINE, FILE or elevated global roles; protect schemas from attacker-controlled routines | Next-transaction READ ONLY fence; 3-case mandatory live MySQL native/UDF corpus, including real TEMPORARY-function negative control and normal builtins | Native or stored routines performing separate-connection/external I/O; unqualified routines without physical identity proof |
| SQL Server 2022 | Dedicated restricted SQL Login with explicit object SELECT, no DML/DDL, EXECUTE AS/IMPERSONATE, TRUSTWORTHY/UNSAFE assembly privilege, elevated cross-database ownership chains or untrusted CLR EXECUTE | Same-connection effective principal/permission attestation for no-whitelist Query, plus 2-case mandatory native/UDF least-privilege corpus | SQL CLR/external code or privileged cross-database modules outside the attested principal scope |
| Oracle 26ai | Dedicated CREATE SESSION plus intended object SELECT grants only; no DML/DDL/ANY privileges or EXECUTE on definer-rights/autonomous/external PL/SQL packages; enforce PDB-scoped roles | Native provider READ ONLY transaction and 4-case mandatory real UDF, builtin and autonomous-transaction negative/positive corpus | Privileged autonomous transactions and external packages not restricted at the DB role boundary |
| Firebird 5 | Dedicated DB user/role with SELECT only on intended relations, no DML/DDL privileges and no EXECUTE on untrusted stored procedures/PSQL/external UDRs | Native READ ONLY transaction and mandatory Firebird real native/UDF whitelist negative control | EXTERNAL/privileged procedures or autonomous effects not sandboxed by the DB role |
| SQLite | Read-only filesystem permissions and a read-only connection for Query-only targets wherever possible; embedding hosts **must not register writable callbacks** or callbacks opening separate privileged connections | File-backed 11-case mandatory Host Query corpus for hidden tables, overridden builtins, same-connection and **separate-connection callback writes**, whitelist and legal CTEs; query_only fence, built-in collision check, and same-connection EXPLAIN attestation of invoked callback opcodes | Untrusted virtual tables, host code outside invoked SQL function opcodes, writable same-process handles or external I/O outside the inspected Query |

### SQLite callback execution contract

The SQLite engine's `PRAGMA query_only` only fences the Query connection.
A user-defined function can open a **second writable connection** and commit
outside that fence. A physical native SELECT using a separately registered
function demonstrates the counterexample; this is not a hypothetical syntax
or an exception injected into the driver.

The Host now checks `PRAGMA function_list` and compiles `EXPLAIN` for the
*actual authorized, parameterized SELECT* on the **same already-open execution
connection**, after `query_only` has been attested. The pre-execution
opcode check rejects a function whose invoked signature is registered as
application-defined; a non-invoked extension may remain registered and
ordinary native built-in function calls continue working. Missing or malformed
opcode/function identity evidence refuses execution. The original built-in
override detection and `query_only` cleanup still apply.

This is a bounded SQL-function invocation defense, **not** a general-purpose
sandbox for arbitrary host code, virtual-table modules, preparation-time
callbacks, extension loading, unrelated process writes or writable external
handles. Deploy SQLite Query using a filesystem/connection-level read-only
identity where available and prohibit untrusted host extensions; do not
interpret this guard as authorization for arbitrary external callbacks.

### Configuration acceptance

- A Query-only MCP tool grant is **not** a DB privilege grant. A null/empty table allowset means *no table whitelist*, not an empty readable set. To protect confidential rows, configure explicit target DB privileges **and** the intended MCP table allowset. Do not issue production Query keys bound to sysadmin/root/DBA identities.
- Avoid privileged functions whose behavior the compiler cannot prove. Deny their EXECUTE at the database role level and disable/limit extensions, SQL CLR, native UDRs and callbacks as appropriate; fail closed instead of claiming arbitrary code purity.
- Use per-provider real positive SELECT plus explicit negative forbidden SELECT/native routine controls before allowing new production credentials. Native functions with external effects are unsupported unless the deployment explicitly constrains them using DB role/host sandbox controls outside the SQL compiler.
- The 32 physical Query cases across the six engines (PostgreSQL 11, SQLite 11, MySQL 3, SQL Server 2, Oracle 4, Firebird 1) are **bounded security evidence**, not a proof of arbitrary third-party routine purity. Each CI owner must prove nonempty, exact pass counts and zero skips.

## Physical concurrency and DML outcomes

- `MaxConcurrentSql` means *admitted operations*. It is not an upper bound on all database driver pools, setup sessions, witnesses, Admin Store or unrelated applications.
- Standard Redis SQL concurrency supports **one authoritative standalone primary** and requires `FailureMode=FailClosed`. Explicit Sentinel/multiple-endpoint SQL-coordination configurations are rejected because this lease is not quorum-fenced. Real two-primary Testcontainers evidence demonstrates two independent primaries may each admit one operation for the same key. HA promotion, async replication, DNS/proxy split brain and an absolute global session ceiling are **unsupported strong guarantees**.
- A returned DML `outcome_unknown` is never a positive COMMIT/ROLLBACK assertion; do not replay the consumed approval. Preserve the original approval ID and use independent durable-state reconciliation before a new approved operation. The existing real PostgreSQL wire-EOF proof and six-provider post-native synthetic loss suites have different scopes.

## Release evidence and CodeQL scope

The mandatory 18-job Tests workflow records physical Query, Redis/outage,
native COMMIT/rollback, load/cancellation and no-skip gates. The infrastructure
job publishes live NuGet/pnpm advisory reports and exact-head Host/F# compiler
warning logs. **CodeQL does not semantically analyze F#** (it covers the
configured Actions, C# and JavaScript/TypeScript languages); F# needs the SQL
grammar, negative capability, differential parity and physical runtime tests.

Release 3.1.0 remains blocked until every #575 checkbox is supported, the
issue is closed as completed, and the *actual promoted main push SHA* passes
all 18 Tests jobs and the SqlCore baseline. Develop-PR success is necessary
but not sufficient. Never bypass this preflight for publication.
