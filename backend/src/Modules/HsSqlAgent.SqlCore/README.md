# HsSqlAgent.SqlCore

Provider-driver-free SQL parsing, validation and compilation for HsSqlAgent.

It contains the structured SQL parser, AST, binding, normalization, semantic/capability validation,
provider-specific SQL lowering and query/DML compilation pipeline. It intentionally does not carry
ADO.NET database drivers, Dapper, MCP, ASP.NET Core, authentication or administration services.

## Install

```bash
dotnet add package HsSqlAgent.SqlCore
```

## Compile SQL

```csharp
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.SqlParsing;

var parsed = CoreSqlTextParser.ParseQuery(
    "SELECT id, name FROM users WHERE active = true",
    SqlAgentToolType.Postgres);

var command = CoreSqlCompiler.CreateDefault().Compile(
    parsed,
    SqlAgentToolType.Postgres,
    new SqlPlanValidationContext("app-policy-v1"),
    new SqlExecutionPlanPolicy(QueryMaxRows: 100));

Console.WriteLine(command.Sql);
foreach (var parameter in command.Parameters)
    Console.WriteLine($"{parameter.Name} = {parameter.Value}");
```

For mutations, use `CoreSqlTextParser.ParseDml` with `CoreDmlCompiler`. Compilation produces an
immutable `CompiledSqlCommand`; this package does not open database connections or execute SQL.

## Architecture invariants

The public CLR compatibility surface and the compiler core are intentionally separate:

- `RewriteCoreModel` is the closed F# DU source of truth after SQL enters the compiler.
- Compiler state advances through unforgeable stages: parsed → bound → canonical → validated → executable.
- Source dialect semantics travel as `VerifiedSource`; target runtime and target capability proofs travel together as `VerifiedTarget`.
- `RewriteLegacyAstAdapter`, `RewriteCompatibilityAstAdapter` and `RewriteFacadeAdapter` are the only rewrite-layer seams allowed to depend on the legacy `Core.Ast` / `ParsedStatement` compatibility model.
- Binder, normalization/validation, policy and native rendering must not depend on the compatibility AST. CI reflects over internal rewrite signatures and rejects any dependency that crosses this boundary.
- Typed diagnostics preserve code, compiler stage, category and source span without changing legacy exception compatibility.
- Rendering accepts executable typestate, not an arbitrary AST plus a separately supplied provider identity.

The compatibility AST remains available for CLR callers that inspect or replace `ParsedStatement.Statement`,
but it is a projection/ingress format rather than a second semantic source of truth.

## Temporal capability boundary

Date arithmetic units are represented as a closed F# algebra rather than free-form canonical strings.
PostgreSQL, MySQL, SQL Server, and Firebird currently have declared DATEADD lowering for DAY, WEEK,
MONTH, QUARTER, YEAR, HOUR, MINUTE, and SECOND. Oracle and SQLite currently admit DAY, WEEK, HOUR,
MINUTE, and SECOND only. MONTH, QUARTER, and YEAR remain fail-closed for Oracle and SQLite because
their calendar rollover behavior is not yet proven equivalent to the canonical source semantics.
Cross-provider non-DAY DATEDIFF also remains fail-closed because provider boundary-counting rules differ.

## DML capability boundary

SQLite UPDATE ... FROM is enabled only when both source and target capability profiles explicitly
prove ServerVersion 3.33 or newer. PostgreSQL, SQL Server, and version-proven SQLite keep their native
joined-update syntax; cross-provider joined-update lowering remains fail-closed when SQL Server or
SQLite participates because duplicate-match target-row selection is not proven equivalent.

SQLite 3.35+ rich RETURNING expressions are native-only and limited to the modified target table.
Auxiliary UPDATE FROM sources are not visible to RETURNING, and the existing proven expression subset
continues to reject top-level aggregates, window functions, subqueries, correlated references, and
unproven functions.

Firebird 5.0+ DSQL rich RETURNING uses the same proven scalar/predicate subset and may cross-lower
with PostgreSQL when ordinary expression capabilities are proven. Firebird-specific OLD/NEW row
contexts are not represented by the portable Core AST and remain outside this contract.

SQL Server INSERT/UPDATE/DELETE OUTPUT is represented by the same closed result-row AST for a
target-only column/wildcard subset and rendered with INSERTED/DELETED row images. Direct OUTPUT is
admitted only for same-provider SQL Server compilation when the validation context carries
metadata-backed DmlResultRowAssurance.NoEnabledTriggers for the exact target table and DML operation.
Cross-provider RETURNING/OUTPUT lowering stays fail-closed because SQL Server OUTPUT exposes the
pre-trigger row image while other providers do not share that trigger-timing contract.

PostgreSQL and Firebird UPDATE/DELETE target aliases share a proven alias-hides-original-target
contract. Both are represented structurally and may cross-lower between those two providers; target
aliases for the remaining providers stay fail-closed until their mutation-specific alias grammar and
name-resolution rules are modeled.

PostgreSQL DELETE ... USING can lower to SQL Server joined DELETE for the proven no-target-alias
shape by restating the target table in the Transact-SQL FROM source list. SQL Server native joined
DELETE ... FROM source grammar is represented by the same closed AST, while PostgreSQL USING spelling
remains invalid Transact-SQL source syntax.

Oracle 26+ direct-join UPDATE ... FROM and DELETE ... {FROM|USING} are version-gated by explicit
source and target profiles. Oracle joined UPDATE is same-provider native-only because duplicate target
matches raise ORA-30926, which is not the portable behavior of the other joined-update providers.
Oracle 26 joined DELETE participates in the PostgreSQL/SQL Server joined-delete intersection because
Oracle documents duplicate source matches as deleting each target row once. RETURNING/OUTPUT remains
independently capability-gated.

## Package boundaries

- Use `HsSqlAgent.Provider.*` when you also need an ADO.NET driver, metadata discovery and
  provider-specific runtime behavior.
- Use `HsSqlAgent.Server` to embed the complete MCP SQL Agent in ASP.NET Core.
- SQL lowering is rendered directly from the validated canonical Core AST; SqlCore has no query-builder backend dependency.

Project: https://github.com/tse-wei-chen/hs-sql-agent
