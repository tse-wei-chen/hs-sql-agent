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
