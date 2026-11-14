# HsSqlAgent.SqlCore

**Driver-free SQL parsing, static analysis, and fail-closed compilation for .NET.**

`HsSqlAgent.SqlCore` is the compiler library used by [hs-sql-agent](https://github.com/tse-wei-chen/hs-sql-agent). It supports six SQL dialects: **PostgreSQL, MySQL, SQL Server, Oracle, SQLite, and Firebird**. Syntax and capabilities are deliberately provider- and version-specific; support for a dialect does **not** mean every statement is executable.

The compiler uses a closed F# AST, typed validation and capability checks, and provider-native rendering behind a CLR-friendly C# facade. **It does not connect to a database or execute SQL.**

## Capabilities

Support depends on statement shape, provider, version, compilation policy and proof inputs.

| Family | Compilation | Static analysis |
| --- | --- | --- |
| DQL | `CompileQuery`: supported SELECT shapes and proven dialect transformations | Query binding, dependencies and diagnostics through script analysis |
| DML | `CompileDml`: supported INSERT / UPDATE / DELETE and bounded conflict / MERGE forms | Modeled mutation effects and dependencies, with explicit uncertainty |
| DDL | `CompileDdl`, limited PostgreSQL `CompileDdlBatch`, immutable plans and static `PreviewDdl` | Modeled schema changes, virtual catalog evolution and dependencies |

The six dialects do not have identical support. Cross-provider compilation requires
proven transformations. Parsing a statement does not imply compilation support.

`ParseScript` / `AnalyzeScript` also cover bounded DCL, transaction, session,
utility and procedural models. Analysis simulates catalog/effects in statement
order and reports uncertainty; it does not determine actual affected rows or
replace a database optimizer.

Compilation requires no human approval. Consumers receive SQL, parameters and
plans, and choose their own execution workflow.

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

| API | Purpose |
| --- | --- |
| `CompileQuery` / `TryCompileQuery` | Validate and render a query under an explicit policy |
| `CompileDml` / `TryCompileDml` | Validate and render a bounded mutation under DML policy and applicable proof requirements |
| `CompileDdl` / `TryCompileDdl` | Compile an explicitly supported single DDL operation into an immutable schema plan |
| `CompileDdlBatch` / `TryCompileDdlBatch` | Compile the limited, proven PostgreSQL DDL batch topologies |
| `PreviewDdl` / `TryPreviewDdl` | Inspect a static before/expected-after schema change |
| `ParseScript` / `AnalyzeScript` | Inspect multi-statement SQL, catalog effects, dependencies, and uncertainty |

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

DDL preview computes a **static** before/expected-after schema diff from the supplied virtual catalog. It does not inspect a live database. Supported operations and batches depend on provider and statement shape.

## Package boundary

SqlCore contains dialect-aware parsing, source spans, binding, typed semantics,
source/target capability checks, compilation, immutable plans and static analysis.
Database drivers, connections, transactions and execution belong to the consumer.

Unsupported syntax, ambiguous identities and unproven transformations are rejected.
The package does not claim complete SQL grammar or cross-dialect equivalence.

## Documentation

- [SQL syntax and capability reference](https://sql-agent.net/en/docs/sql-compiler/sql-support)
- [Architecture and contribution guide](https://sql-agent.net/en/docs/development/architecture)
- [Full documentation](https://sql-agent.net/en/docs/)
- [Release history and feature discussions](https://github.com/tse-wei-chen/hs-sql-agent/pulls?q=is%3Apr+is%3Amerged)
