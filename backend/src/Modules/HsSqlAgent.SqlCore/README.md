# HsSqlAgent.SqlCore

**Driver-free SQL parsing, static analysis, and fail-closed compilation for .NET.**

`HsSqlAgent.SqlCore` is the compiler library used by [hs-sql-agent](https://github.com/tse-wei-chen/hs-sql-agent). It supports six SQL dialects: **PostgreSQL, MySQL, SQL Server, Oracle, SQLite, and Firebird**. Syntax and capabilities are deliberately provider- and version-specific; support for a dialect does **not** mean every statement is executable.

The compiler uses a closed F# AST, typed validation and capability checks, and provider-native rendering behind a CLR-friendly C# facade. **It does not connect to a database or execute SQL.**

## Install

```bash
dotnet add package HsSqlAgent.SqlCore
```

## Compile a query

```csharp
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;

var command = SqlCoreFacade.CompileQuery(
    "SELECT 1",
    SqlAgentToolType.Postgres,
    SqlAgentToolType.Postgres,
    new SqlPlanValidationContext("my-query-policy"),
    new SqlExecutionPlanPolicy(100));

Console.WriteLine(command.Sql);
```

`CompiledSqlCommand` contains native SQL, bound parameters, and compiler evidence. The caller remains responsible for database access, connection identity, permissions, and runtime limits.

### Bounded conditional DISTINCT COUNT (G10/Q02)

A valid FILTER source such as PostgreSQL
`COUNT(DISTINCT amount) FILTER (WHERE status = 1)` can compile to
MySQL `COUNT(DISTINCT CASE WHEN status = 1 THEN amount END)` and
SQL Server `COUNT_BIG(DISTINCT CASE WHEN status = 1 THEN amount END)`.
Only a direct local column, one DISTINCT argument and a proven bounded
predicate are authorized. NULLs, duplicates, no-match zero counts and
group-by results are regression-tested in PostgreSQL, MySQL and SQL Server.
`DISTINCT` window aggregates remain rejected on both targets (they are not
supported in MySQL 8.4 or SQL Server 2022); computed DISTINCT arguments,
multi-argument DISTINCT, other FILTER aggregates and invalid source syntax
remain fail-closed. Cross-database string collations and type coercions are
provider-specific and are **not** claimed equivalent by this numeric parity
evidence; SqlCore does not execute SQL.

### Bounded cross-provider COUNT FILTER lowering (G10/Q02)

PostgreSQL `COUNT(*) FILTER (WHERE status = 1)` and
`COUNT(amount) FILTER (WHERE status = 1)` can now compile for MySQL as
`COUNT(CASE WHEN ... THEN 1/amount END)`, and for SQL Server as
`COUNT_BIG(CASE WHEN ... THEN 1/amount END)`. The SQL Server form
preserves 64-bit counts. Both preserve empty-group results (zero, not NULL), NULL-sensitive
`COUNT(amount)` and window-frame counts. Only unqualified, non-DISTINCT
COUNT over `*` or a direct local column with bounded column/literal
predicates is proven. `SUM`, DISTINCT, computed COUNT operands, functions,
subqueries and outer references remain rejected; raw MySQL/SQL Server
`FILTER` source syntax is still invalid. The separate
`aggregate.filtered_count` capability does not weaken generic
`expression.filter` rules. No SQL execution or approval is added.

### Native EXCLUDE frame membership (G10/Q02)

PostgreSQL 11+ and SQLite 3.28+ preserve `EXCLUDE CURRENT ROW`, `EXCLUDE GROUP`,
`EXCLUDE TIES`, and explicit `EXCLUDE NO OTHERS` for `ROWS`, `RANGE`
and `GROUPS` window frames. These alter which *peer rows* are visible to
frame-sensitive functions; they are **not** interchangeable with a WHERE
filter or another frame unit. Both raw SQL and structured
`WindowFrameDefinition.Exclusion` carry the explicit mode through the F#
AST and CLR compatibility layer. Other four source/target providers and
explicitly old runtime versions reject before SQL emission; the Host still
must verify its actual database engine before execution.

### SQL Server RANGE source-dialect fence (G10/Q02)

SQL Server does not permit numeric `PRECEDING` or `FOLLOWING` offsets for
`RANGE` frames. SqlCore rejects these as **source syntax** even when targeting
PostgreSQL or SQLite; it also rejects them when SQL Server is the target of
a structured query. Numeric `ROWS` offsets and supported
`RANGE UNBOUNDED PRECEDING/CURRENT ROW` forms remain valid.
PostgreSQL and SQLite retain their native numeric `RANGE` semantics.

### Native GROUPS peer-frame queries (G10/Q02)

For PostgreSQL 11+ and SQLite 3.28+, the F# query compiler can preserve a
bounded `GROUPS BETWEEN ... AND ...` window frame natively. Unlike `ROWS`,
`GROUPS` counts ordered peer groups rather than individual rows. An explicit
window `ORDER BY` is required; backward bounds and other unsupported shapes
fail closed. The source and target dialect are both checked, and an explicitly
declared server version older than the required minimum is rejected. An absent
version profile uses the current-supported-release compilation baseline, **not**
a claim that an arbitrary older runtime can execute the plan. The Host must
verify its actual runtime version; no fallback to `ROWS` or `RANGE` exists.
MySQL, SQL Server, Oracle and Firebird remain rejected for this capability.

## Other compiler APIs

| API | Purpose | Execution authority |
| --- | --- | --- |
| `CompileQuery` / `TryCompileQuery` | Validate and render a query under an explicit policy | None |
| `CompileDml` / `TryCompileDml` | Validate and render a bounded mutation under DML policy and applicable proof requirements | None |
| `CompileDdl` / `TryCompileDdl` | Compile an explicitly supported single DDL operation into an immutable schema plan | None |
| `CompileDdlBatch` / `TryCompileDdlBatch` | Compile the limited, proven PostgreSQL DDL batch topologies | None |
| `PreviewDdl` / `TryPreviewDdl` | Inspect a static before/expected-after schema change | None |
| `ParseScript` / `AnalyzeScript` | Inspect multi-statement SQL, catalog effects, dependencies, and uncertainty | None |

Use the `SqlCoreFacade` APIs from `HsSqlAgent.SqlCore`. More advanced overloads accept typed source/target capability profiles, compilation policies, proof inputs, and cancellation tokens.

### Analyze a script without executing it

```csharp
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;

var analysis = SqlCoreFacade.AnalyzeScript(
    "CREATE TABLE demo (id INT); SELECT id FROM demo;",
    new SqlScriptAnalysisOptions(
        SqlAgentToolType.Postgres,
        SqlCatalogState.KnownEmpty()));

Console.WriteLine(analysis.EffectCoverage);
```

`KnownEmpty()` is an **explicit caller assertion** about the starting virtual catalog. Without it, analysis starts from an unknown/open-world catalog. Neither state proves the real database schema.

### Compile a DDL plan, not a database change

```csharp
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Ddl;
using HsSqlAgent.SqlCore.Enums;

var options = new DdlCompilationOptions(
    SqlAgentToolType.Postgres,
    SqlAgentToolType.Postgres);

var preview = SqlCoreFacade.PreviewDdl(
    "CREATE TABLE public.demo (id INTEGER NOT NULL)",
    options);

Console.WriteLine(preview.Plan.Command.Sql);
Console.WriteLine(preview.AuthorizesExecution); // False
```

DDL preview is **static**. Even a successful compilation is not proof of physical object identity, a live schema snapshot, human approval, transactional behavior, or permission to execute. Some DDL operations and DDL batches are supported only for particular providers and shapes.

### SQL Server single-row MERGE target locking

The narrowly modeled native SQL Server `MERGE` (one explicit `USING (VALUES (...))` row, full metadata-assured UNIQUE/PRIMARY match key) compiles with `WITH (HOLDLOCK)` on the **target** to retain a serializable key-range fence, including absent keys. Real SQL Server tests verify key resolution, matched/unmatched actions and competing writes. SqlCore still does not open a transaction or grant execution authority. The Hosting/Server typed DML approval path still **rejects MERGE/Upsert** until existing-row impact and approval are independently proven.

### Strict SQLite single-key conflict-skip schema validation

The closed F# Strict Schema Snapshot validates the *physical target-column shape* of exactly one SQLite `INSERT ... VALUES (...) ON CONFLICT (id) DO NOTHING` row with a single explicit conflict target. This structural validation **does not prove that `id` has a physically enforced UNIQUE constraint**: only the Host can inspect a real SQLite PRIMARY KEY or simple UNIQUE key, ensure it is the sole enforced unique key, verify the native table/index/trigger definition, bind the preview conflict state and revalidate it in the write transaction. The typed Host currently permits this one-row DO NOTHING exception only with a built-in native schema verifier, one sole simple physical PRIMARY or UNIQUE key, a non-NULL exact INT64 key literal, no triggers and `DmlMaxAffectedRows=1`. Conflict UPDATE, general UPSERT/MERGE, batches and other providers remain excluded from this Host feature.

### Native PostgreSQL single-row conflict skip

The typed Hosting/Server DML approval path now also permits a **narrow PostgreSQL** `INSERT ... VALUES (one row) ON CONFLICT (one_column) DO NOTHING` case with a non-NULL exact 64-bit integer literal. The physical column must be a NOT NULL INT2/INT4/INT8 with the table's **sole simple immediately enforced (NOT DEFERRABLE) PRIMARY/UNIQUE index**. The built-in PostgreSQL provider captures a full table/index/constraint digest, rejecting partitioning, rules, user triggers and row-level security. Preview binds the actual key presence; Commit uses a PostgreSQL transaction-held `SHARE ROW EXCLUSIVE NOWAIT` lock to prevent competing writes during revalidation, checks the same digest, then executes the compiled mutation. Unproven keys, index changes, multi-row, UPDATE-on-conflict, RETURNING and batches remain rejected. The stronger table-level lock is intentional and may reduce concurrent write throughput.

### Native PostgreSQL single-row conflict UPDATE Host approval

Host-only, separately from the independent SqlCore NuGet compiler, PostgreSQL now has a bounded `INSERT INTO users (id, name) VALUES (7,'new') ON CONFLICT (id) DO UPDATE SET name=EXCLUDED.name` Preview/Approval/Commit path. Its physical target must have exactly two columns (one NOT NULL BIGINT sole immediate PRIMARY/UNIQUE key and one NOT NULL TEXT value), no default, CHECK/FK, additional index/column, trigger, rule, RLS, partition or inheritance. The full native physical metadata digest is bound to one-use approval with the proposed payload and the actual prior row. Preview displays INSERT or UPDATE with before/after values. Commit takes a transaction-held PostgreSQL `SHARE ROW EXCLUSIVE NOWAIT` lock, verifies the same schema and old-row state, and runs the canonical compiled statement atomically. Generic UPSERT, key updates, multi-column/row changes and arbitrary expressions remain fail closed; the table lock knowingly reduces concurrent writer capacity. No new MCP tool or public compiler API.

### PostgreSQL multi-column native conflict UPDATE approval (Host only)

M12 reuses the existing F# compiler and typed Host approval to support a bounded single-row PostgreSQL `INSERT INTO users (id, name, city) VALUES (7, 'new-name', 'new-city') ON CONFLICT (id) DO UPDATE SET name=EXCLUDED.name, city=EXCLUDED.city`. The actual permanent plain table must contain exactly one NOT NULL BIGINT sole immediate PRIMARY/UNIQUE key and **1–4 TEXT NOT NULL value columns**; every inserted non-key column must receive its own `EXCLUDED.column` assignment. Preview binds INSERT/UPDATE, every old/new value, the canonical payload and full physical catalog SHA. Commit acquires `SHARE ROW EXCLUSIVE NOWAIT` until transaction completion and re-attests all old values before executing the compiled mutation. Missing/duplicate/partial expressions, five or more updated columns, extra physical columns/indexes, defaults, CHECK/FK, triggers, rules, RLS or partitioning fail closed. This lock limits concurrent writes. No new MCP tool or public signature; newly versioned approval bindings require an updated preview after deployment.

### PostgreSQL typed, nullable native conflict UPDATE approval (Host only)

M13 extends M12's same one-row, 1–4-value PostgreSQL `ON CONFLICT DO UPDATE` approval without changing the F# compiler's public contracts. A single permanent plain physical table with a sole immediately enforced NOT NULL BIGINT unique/primary key may now contain value columns of `TEXT`, `SMALLINT`, `INTEGER` or `BIGINT`, each nullable or NOT NULL. The Host matches string literals only to TEXT, signed integral literals to exact INT2/4/8 ranges and SQL NULL only to physically nullable fields. It never infers safety from implicit casts, default values or a virtual schema. The Preview binds every Before/After value (including NULL), the real old-row fingerprint, physical schema SHA and compiled mutation to one-time approval. Commit locks the actual table with `SHARE ROW EXCLUSIVE NOWAIT`, revalidates types, NULL constraints, schema and all old values, then executes exactly once. Date/time, NUMERIC, floating-point, UUID, arbitrary expressions, extra indexes/constraints, triggers and batches remain denied. Internal binding `v3` requires a fresh Preview for unfinished M12 approvals; strong table locking intentionally limits parallel writers.

### PostgreSQL M13 Host integer-source and quoted-column identity fence

For the native typed single-row PostgreSQL `ON CONFLICT DO UPDATE` Host approval only, values intended for SMALLINT/INTEGER/BIGINT columns require an original **plain decimal integer token** (for example `42` or `-12`). The Host checks the verified F# AST's source span and original parsed SQL; normalized scientific or fractional spellings (`1e10`, `42.0`, `4.2e1`) do not qualify as an approved integer literal. PostgreSQL quoted column identities that differ only by case (such as `id` and `"ID"`) or collide with synthetic preview fields after case folding are rejected, so the existing case-insensitive row-fingerprint reader never ambiguously approves another physical column. This does not alter the general SQL grammar or the existing TEXT/SMALLINT/INTEGER/BIGINT/SQL NULL capability, one-use Host challenge and transaction-time physical catalog revalidation. No new public API or MCP tool.

### PostgreSQL native conflict UPDATE direct RETURNING Host approval

The single-row PostgreSQL `INSERT INTO users(id,name,amount) VALUES(7,'new',42) ON CONFLICT(id) DO UPDATE SET name=EXCLUDED.name, amount=EXCLUDED.amount RETURNING id,name,amount` uses the existing F# closed DML AST/renderer and physically attested typed Host Preview/Approval/Commit. RETURNING is restricted to 1–5 distinct, unqualified direct physical key/value target columns, already covered by the same old-row and proposed-value approval. Expression, wildcard, aliased, qualified, duplicate and system-column RETURNING do not mint approval. PostgreSQL `SHARE ROW EXCLUSIVE NOWAIT` holds the physical catalog and UNIQUE key stable at transaction-time recheck. Actual result rows, including SQL NULL, are emitted through the existing structured DML result only after confirmed commit. No new public API, MCP tool, dependency or extra metadata query; atomic conflict batches remain denied.

### PostgreSQL conflict-skip key-only RETURNING (Host only)

On the existing one-row PostgreSQL `ON CONFLICT (id) DO NOTHING` Preview/Approval/Commit path, `RETURNING id` is admitted only when `id` is the **same single, direct, unqualified physical PRIMARY/UNIQUE key** attested by the live provider. The original NOT NULL INT2/INT4/INT8 key, sole immediate enforced unique index, full physical schema fingerprint, one-time approval, transaction-held `SHARE ROW EXCLUSIVE NOWAIT`, revalidation and one-row cap remain mandatory. Commit returns the **actual inserted key row** only on confirmed insertion; a proven conflict skip returns zero rows. Drift, approval replay or changed conflict state return no rows and do not authorize a write. Other RETURNING columns, wildcards, aliases, expressions, qualifiers and system fields remain denied, as does SQLite conflict-skip RETURNING. No new MCP tool, public SqlCore API, metadata round trip or DML batch capability.

### SQL Server MERGE matched DELETE Host approval (develop only)

For real SQL Server 2022, a one-row `MERGE INTO dbo.inventory AS t USING (VALUES (7)) AS s (id) ON t.id = s.id WHEN MATCHED THEN DELETE;` now uses the existing F# MERGE compiler and typed Host single-statement approval, **only** when `id` is the exact non-null native INT, non-identity, sole physically enforced primary key of a regular permanent dbo table. No extra UNIQUE key, trigger, FK, CHECK, default, security policy or unsupported table definition is permitted. `MERGE WITH (HOLDLOCK)` compiler output alone is never sufficient: Host first attests the live complete schema, unique key and original integer source token; Preview binds the current 0/1 matching key to a one-time authorization, and Commit holds a serializable `TABLOCKX/HOLDLOCK` lock while rechecking schema and row presence before execution. Unexpected row/key/index drift cancels. Existing `execute_dml_sql` handles the result with no extra tool or public API. The exclusive lock reduces concurrent writers. MERGE UPDATE/INSERT/mixed actions, arbitrary predicates, composite/multiple source keys, cross-provider execution and atomic MERGE batches all remain denied.

### SQL Server 2022 MERGE matched UPDATE approval (develop only)

In addition to the narrow matched-DELETE Host capability, `MERGE INTO dbo.inventory AS t USING (VALUES(7,25,4)) AS s(id,quantity,status) ON t.id=s.id WHEN MATCHED THEN UPDATE SET quantity=s.quantity,status=s.status;` is permitted only for one source row with a single exact physically enforced non-identity INT PRIMARY key and 1–3 distinct, direct, non-null native INT payload columns. Preview shows each real physical before-value and the proposed after-value; full old-value fingerprints, policy, actor, target and complete schema digest are sealed to a one-use approval. Commit holds a serializable SQL Server table-wide `TABLOCKX/HOLDLOCK` while reattesting the physical catalog and **all old values**. Any drift cancels rather than overwriting concurrent changes. The exclusive lock intentionally limits concurrent writes. No MERGE INSERT, mixed actions, expressions, casts, NULL, primary-key assignment, composite keys, multiple rows or MERGE batches; no new MCP tool and no changes to default DML-off or four read-only selections.

### SQL Server 2022 MERGE NOT MATCHED INSERT Host approval (develop only)

The existing one-row native SQL Server MERGE pipeline now also admits **`WHEN NOT MATCHED THEN INSERT` only**, such as `MERGE INTO dbo.inventory AS t USING (VALUES(7,25,4)) AS s(id,quantity,status) ON t.id=s.id WHEN NOT MATCHED THEN INSERT(id,quantity,status) VALUES(s.id,s.quantity,s.status);`. The table must be an ordinary permanent `dbo` table with one sole physically enforced non-NULL non-identity INT PRIMARY key and **1–3 direct INT payload fields**. INSERT must enumerate every physical column exactly once and use the identically named source field. Full physical Catalog and source integer token proof forbids omitted columns/default values, other unique keys, triggers, FKs, CHECK and unknown side effects.

Preview binds the actual existing-key absence/presence result to the proposed complete row, exact compiled MERGE, physical schema and one-use actor/policy approval. When missing, it shows the full proposed row and predicts one INSERT; when present, zero affected rows and no proposed insertion. Commit holds SQL Server SERIALIZABLE `TABLOCKX/HOLDLOCK` and verifies the real schema and same key absence/presence again, cancelling if another writer inserted/deleted the key or revoked physical indexes. The exclusive table lock reduces concurrent writes; no Host throughput improvement is implied. No general MERGE or mixed actions, extra predicates, casts, implicit coercions, nullable values, composite keys or atomic MERGE batches. Reuses `execute_dml_sql` without adding a new MCP tool; the four default read-only tools remain selected and DML disabled.

## Package boundary and safety

- **Inside SqlCore:** dialect-aware parsing, source spans, binding, typed semantics, source/target capability proofs, query/DML/DDL compilation, immutable plans, and static script/catalog analysis.
- **Outside SqlCore:** ADO.NET drivers, MCP, HTTP, authentication, authorization, DML/DDL approval, live metadata and row-set revalidation, database transactions, execution, and audit.
- **Fail closed:** unsupported syntax, unknown effects, ambiguous identities, and unproven cross-provider transformations must not become executable SQL through a fallback.
- **No general SQL completeness claim:** always consult the actual SQL capability reference. `ParseScript` / `AnalyzeScript` are inspection APIs and do not grant execution authority.

To host governed SQL execution in an existing ASP.NET Core application, use [HsSqlAgent.Hosting](../HsSqlAgent.Hosting/README.md), not SqlCore by itself.

## Documentation

- [SQL syntax and capability reference](https://sql-agent.net/en/docs/sql-compiler/sql-support)
- [Architecture and contribution guide](https://sql-agent.net/en/docs/development/architecture)
- [ASP.NET Core hosting integration](https://sql-agent.net/en/docs/integration/aspnet-core)
- [Security architecture](https://sql-agent.net/en/docs/security/overview)
- [Full documentation](https://sql-agent.net/en/docs/)
- [Release history and feature discussions](https://github.com/tse-wei-chen/hs-sql-agent/pulls?q=is%3Apr+is%3Amerged)

Provider-specific proof matrices, CI history, performance experiments, and individual implementation decisions belong in the corresponding PRs, tests, and documentation pages—not in an ever-growing package README.
