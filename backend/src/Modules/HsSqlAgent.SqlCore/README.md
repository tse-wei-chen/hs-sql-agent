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

### Strict SQLite conflict-skip schema validation

The closed F# Strict Schema Snapshot validates the *physical target-column shape* of exactly one SQLite `INSERT ... VALUES (...) ON CONFLICT (id) DO NOTHING` row with a single explicit conflict target. This structural validation **does not prove that `id` is UNIQUE**: only the Host can inspect a real SQLite PRIMARY KEY, ensure it is the sole enforced unique key, verify the native table/index/trigger definition, bind the preview conflict state and revalidate it in the write transaction. The typed Host currently permits this one-row DO NOTHING exception only with a built-in native schema verifier and `DmlMaxAffectedRows=1`. Conflict UPDATE, general UPSERT/MERGE, batches and other providers remain excluded from this Host feature.

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
