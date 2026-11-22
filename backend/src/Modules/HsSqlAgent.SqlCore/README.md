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
The Query `EXTRACT` model supports a proven PostgreSQL↔Firebird mapping for
ISO-8601 `WEEK` and Sunday-zero `DOW` (Firebird spells it `WEEKDAY`).
PostgreSQL `ISODOW` numbers Monday=1 through Sunday=7; the Firebird
translation applies `MOD(WEEKDAY + 6, 7) + 1` with an explicit `SMALLINT`
result. Source Firebird `ISODOW` is still invalid, and Firebird native
`WEEKDAY` remains Sunday-zero. One Firebird 5 physical readback covers
Sunday/Monday/Saturday values and the native return type.
PostgreSQL `ISODOW` also targets MySQL (`WEEKDAY(date) + 1`)
and SQLite (`STRFTIME('%w', date)` converted through integer modulo).
PostgreSQL Sunday-zero `DOW` and native Firebird `WEEKDAY` also target
MySQL using `DAYOFWEEK(CAST(date AS DATE)) - 1`, and SQLite using
`CAST(STRFTIME('%w', date) AS INTEGER)`; `EXTRACT(DOW ...)` remains
PostgreSQL-only source syntax. This mapping is for date-valued operands,
not a guarantee of timezone-aware timestamp coercion parity.
Their native numbering is not ISO and the compiler retains the
source meaning; non-PostgreSQL `ISODOW` source spelling is rejected.
The MySQL 8.4 and in-memory SQLite physical differential paths
verify Sunday=7, Monday=1, Saturday=6. SQL Server and Oracle
remain target-gated for ISO weekday.
Firebird target also supports native `HOUR`, `MINUTE`, and `SECOND` using
a `TIME` cast, without truncating clock values to `DATE`. Fractional seconds
follow Firebird's native `TIME` precision (up to four decimal places);
this is not a guarantee of arbitrary PostgreSQL microsecond precision.
Firebird target `QUARTER` uses `(EXTRACT(MONTH) + 2) / 3` with integer
division and an outer `SMALLINT` cast to match native result metadata;
it does not require Firebird 5's native `EXTRACT(QUARTER)`.
Physical differential parity is exercised against Firebird 5. The source-side
acceptance of native Firebird `QUARTER` still depends on the server version.
PostgreSQL `EXTRACT(DOY)` (January 1 = 1) and Firebird
`EXTRACT(YEARDAY)` (January 1 = 0) are distinct source fields.
The compiler lowers PostgreSQL DOY to Firebird `YEARDAY + 1`
and Firebird YEARDAY to PostgreSQL `DOY - 1` with a SMALLINT
result; wrong dialect source spellings fail during parsing.
MySQL target now uses native `DAYOFYEAR(CAST(value AS DATE))`;
SQLite uses `CAST(STRFTIME('%j', value) AS INTEGER)`.
For Firebird-source YEARDAY, each target subtracts 1 to retain 0..365.
PostgreSQL-only `DOY` source spelling is rejected for other dialects.
Leap-year boundaries are verified against Firebird 5, MySQL 8.4 and
in-memory SQLite; no cross-provider timezone coercion is claimed.
PostgreSQL `EXTRACT(ISOYEAR)` returns the ISO week-year, not necessarily the
calendar year: 2018-12-31 belongs to ISO year 2019, while 2021-01-01
belongs to ISO year 2020. MySQL 8.4 targets use
`YEARWEEK(CAST(date AS DATE), 3) DIV 100`; SQLite targets compute the year
of the week's Thursday with `DATE(date, '-3 days', 'weekday 4')` and
`STRFTIME('%Y')` converted to `INTEGER`. This avoids requiring SQLite
3.46+ `%G` support. Only PostgreSQL `ISOYEAR` source syntax is admitted;
Firebird, SQL Server and Oracle ISOYEAR targets remain fail-closed.
Other unproven date-part mappings remain provider-specific and fail-closed.

Query compilation also checks that normalization/validation has not introduced
additional named data-source references after binding. This **does not** expand
views, functions, or other database-side effects, and cannot grant data-access
permissions. PostgreSQL Query with a table whitelist admits a bounded set of
window functions pinned to `pg_catalog`; other unproven functions remain
policy-denied. Host read-only enforcement and least-privilege DB credentials
remain necessary.

`ParseScript` / `AnalyzeScript` also cover bounded DCL, transaction, session,
utility and procedural models. Analysis simulates catalog/effects in statement
order and reports uncertainty; it does not determine actual affected rows or
replace a database optimizer.

Compilation requires no human approval. Consumers receive SQL, parameters and
plans, and choose their own execution workflow.

**U6 Script Execution decision: NO-GO.** Static `AnalyzeScript` evidence,
including typed dependencies and modeled savepoint rollback, is never an
executable Script Plan. For supported multi-DML atomic operations use the
existing Host-owned `execute_dml_sql` workflow and its independent approval;
do not wrap it in caller `BEGIN`/`COMMIT`. See the
[U6 Script Execution decision record](U6-script-execution-decision.md) for
the proof-gap matrix and exact reopening criteria.

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

## Release compatibility

The [published v3.1.0 → develop migration and ABI evidence guide](RELEASE-COMPATIBILITY.md)
covers the four capability boundaries (Parse/Analyze/Compile/Host Execute),
the existing ASP.NET Core Hosting sample and required approval configuration,
the actual released NuGet API comparison for net8.0/net10.0, and same-runner
p50/p95/allocated-byte observations. The newer typed Script analysis and
Query source-consistency additions are **develop-only until released**.

## Documentation

- [SQL syntax and capability reference](https://sql-agent.net/en/docs/sql-compiler/sql-support)
- [Architecture and contribution guide](https://sql-agent.net/en/docs/development/architecture)
- [Full documentation](https://sql-agent.net/en/docs/)
- [Release history and feature discussions](https://github.com/tse-wei-chen/hs-sql-agent/pulls?q=is%3Apr+is%3Amerged)
