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

## Static script DCL analysis (no authorization)

`SqlCoreFacade.AnalyzeScript` can report potential, strongly typed permission
changes without connecting to a database or executing SQL. A `SecurityIntent`
identifies an action (`Grant`, `Revoke`, or SQL Server `Deny`), table-level
privilege, structured source object name, and recipient. It is **not** an
effective ACL, access-check result, grantor proof, or executable SQL plan.
All `SqlSecurityIntent.Certainty` values remain `Unknown`.

```csharp
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;

var report = SqlCoreFacade.AnalyzeScript(
    "GRANT SELECT ON app.orders TO 'alice'@'localhost';",
    SqlAgentToolType.MySQL,
    SqlCatalogState.KnownEmpty());

foreach (var intent in report.SecurityIntents)
    Console.WriteLine($"{intent.Action}: {intent.Privilege} on {intent.Target.DisplayName} " +
                      $"for {intent.Principal}@{intent.PrincipalHost}; {intent.Certainty}");
```

The MySQL grammar currently requires **explicit, exact** `'user'@'host'`
accounts and `ON database.table`. Wildcard hosts, omitted hosts, role
membership, `db.*` / `*.*`, account options, and privilege delegation are
not yet modeled and remain fail-closed. The MySQL `@` account separator is
recognized only by script analysis; raw query/mutation compilation still
rejects unbound `@parameters`.

SQL Server's modeled subset accepts `GRANT`, `REVOKE`, and `DENY` for
`ON OBJECT::schema.table`. `DENY` is **not** equivalent to `REVOKE` or a
negative effective-permission proof; inheritance, ownership, principal
membership, grant options and explicit DENY precedence are outside the model.
PostgreSQL, Firebird and Oracle retain their provider-specific proven table
privilege subsets. SQLite does not claim SQL GRANT/REVOKE support.

Neither these intents nor `DetailedEffects` authorize DML or change the
virtual Catalog. Database permission enforcement remains the host's duty.

### PostgreSQL role and delegation dependency analysis

`AnalyzeScript` additionally exposes read-only `SecurityDependencies` on
both each statement and the complete script. Edges have a typed kind:
`RoleMembership` (role → recipient), or `TableDelegation` (table privilege
and potential delegation option → recipient). A dependency is **only a
syntactically observed possible edge**. It is not a persisted role graph,
server ACL snapshot, proof of inheritance, delegated authority, or permission
check. `Certainty` is always `Unknown`.

The modeled PostgreSQL subset includes `GRANT reporting TO analyst`,
`GRANT reporting TO analyst WITH ADMIN OPTION`,
`REVOKE ADMIN OPTION FOR reporting FROM analyst RESTRICT`,
`GRANT SELECT ON TABLE sales.orders TO analyst WITH GRANT OPTION`, and
`REVOKE GRANT OPTION FOR SELECT ON TABLE sales.orders FROM analyst CASCADE`.
Ordinary `REVOKE` also reports potential delegation impact. `CascadeMode`
records the author's `CASCADE` / `RESTRICT` choice, **not** that any
recursive revoke succeeded or which principals actually lost access.

The graph is not used by the executable SQL compiler. Dependencies are bounded
to 128 syntactic edges per DCL statement. Unmodeled role options (`SET`,
`INHERIT`, grantor attribution, version-specific semantics), `PUBLIC` role
membership, and delegation syntax for other database providers remain
fail-closed until modeled separately.

## Utility statement analysis (potential effects only)

`SqlCoreFacade.AnalyzeScript` now reports `UtilityIntents` per statement and
per script, alongside typed `DetailedEffects`. Utilities are never executable
plans or permissions.

- `EXPLAIN SELECT/UPDATE/DELETE` is distinguished from
  `EXPLAIN ANALYZE SELECT/UPDATE/DELETE` by
  `SqlUtilityExecutionMode.PlanningOnly` versus `ExecutesStatement`.
  A plain explain does **not** claim the underlying mutation ran.
- PostgreSQL admits a bounded `EXPLAIN` / `EXPLAIN ANALYZE` subset;
  MySQL admits simple `EXPLAIN` on queries/mutations and
  `EXPLAIN ANALYZE SELECT`; SQLite supports `EXPLAIN QUERY PLAN SELECT`.
  All nested query/DML SQL must first pass the strict source parser and
  agree with its closed F# AST statement kind.
- PostgreSQL `ANALYZE schema.table` and `VACUUM schema.table` are typed
  potential maintenance operations. The whole-database and option-rich forms
  stay fail-closed. On other providers, `ANALYZE` / `VACUUM` are not
  interpreted with PostgreSQL syntax.
- `SqlUtilityIntent.Certainty` is always `Unknown`. Actually executing or
  maintenance-related shapes carry `UnknownEffects` and invalidate Virtual
  Catalog completeness, since triggers, functions and system state are not
  proven. `EXPLAIN ANALYZE` can actually execute writes on a real server;
  it is **not read-only**. Inside an active transaction, utility statements
  also invalidate unsupported rollback proofs.
- SQL Server `SET SHOWPLAN_*`, Oracle `EXPLAIN PLAN FOR`, Firebird's
  provider-native planning commands, and SQLite `PRAGMA` have distinct
  semantics and remain unmodeled. Unknown syntax never gets an incomplete
  typed intent.

The analyzer only *describes* possible execution; it never opens a connection
or issues `EXPLAIN`, `ANALYZE`, `VACUUM`, or any inner query/DML.

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


## Opt-in metadata-backed column validation

The compiler accepts an immutable, version-labelled, **host-provided** `SqlSchemaSnapshot`.
It checks physical table existence, qualified/unqualified column references, join predicates,
ambiguous columns, CTE/derived projections, correlated subqueries, and ordinary
INSERT/UPDATE/DELETE target columns against that snapshot.

```csharp
using HsSqlAgent.SqlCore.Core.Pipeline;

var schema = new SqlSchemaSnapshot(
    SqlAgentToolType.Postgres,
    "catalog-snapshot-2026-10",
    new[] {
        new SqlSchemaTable("public.users", new[] { "id", "name", "active" })
    });
var context = new SqlPlanValidationContext("app-policy-v2")
    .WithSchemaSnapshot(schema);
var command = SqlCoreFacade.CompileQuery(
    "SELECT id, name FROM public.users WHERE active = true",
    SqlAgentToolType.Postgres,
    SqlAgentToolType.Postgres,
    context,
    new SqlExecutionPlanPolicy(100));
```

Without a snapshot, prior compilation behavior is unchanged. With a snapshot, unverifiable
shapes (currently recursive CTEs, NATURAL JOIN, MERGE and UPSERT conflict clauses) **fail
closed**, using typed Binding-stage diagnostics. Missing tables and columns, ambiguous columns
and unprovable projected column names also fail closed. The input snapshot must use actual
provider-catalog identifier spelling, including PostgreSQL and Oracle case rules.

This first stage establishes **name-existence** proofs, not a complete type checker:
function overloads, SQL expression types, NULL behavior, collations, generated columns,
uniqueness, metadata freshness and execution-time catalog drift are not yet proven.
The host is responsible for capturing trustworthy catalog metadata and versioning it.
Do not treat schema validation as a replacement for runtime DML approval, access control
or transaction-row verification.


### Optional portable type-family proofs

Typed metadata is an additional opt-in contract. Existing `new SqlSchemaTable(name, string[])`
remains name-only (`Unknown` type family). To validate proven type conflicts:

```csharp
var typed = SqlSchemaTable.WithTypes(
    "users",
    new[] {
        new SqlSchemaColumn("id", SqlSchemaValueKind.Numeric),
        new SqlSchemaColumn("name", SqlSchemaValueKind.Text)
    });
var typedContext = new SqlPlanValidationContext("strict-v1")
    .WithSchemaSnapshot(new SqlSchemaSnapshot(
        SqlAgentToolType.Postgres, "catalog-v1", new[] { typed }));
```

In typed snapshot mode, the compiler rejects **provably incompatible** comparisons,
numeric arithmetic, LIKE operands and literal INSERT/UPDATE assignments with
`SQL_SCHEMA_TYPE_MISMATCH` at `SemanticValidation`.
Explicitly `Unknown` columns, casts, function results, unproven CTE/derived output types
and provider-specific implicit coercions are not claimed as proven. This is
*not* a provider-exact type checker or NULL/collation/overload solver.
Typed metadata is part of compilation fingerprint and evidence identity.

## DML predicate safety

With the default DML policy, UPDATE and DELETE require a WHERE clause. The compiler now also
rejects WHERE conditions **provably TRUE for every row**, such as `WHERE 1 = 1`,
`WHERE 1 = 1 OR id = 5`, and `WHERE NOT (1 = 0)`.
These denials carry stable Policy-stage diagnostic codes
`SQL_POLICY_UPDATE_UNBOUNDED_PREDICATE` and
`SQL_POLICY_DELETE_UNBOUNDED_PREDICATE`. Explicit full-table DML authorization bypasses
this restriction, while missing-WHERE diagnostics remain unchanged.

This is a conservative proof, **not a completeness claim**: column-dependent tautologies,
unknown functions, implicit type conversions and collation-sensitive expressions are not
treated as known constants. Enforcement of per-request DML row counts, previews, approvals,
permissions, and query resource limits remains the responsibility of the hosting runtime.

## Package boundaries

- Use `HsSqlAgent.Provider.*` when you also need an ADO.NET driver, metadata discovery and
  provider-specific runtime behavior.
- Use `HsSqlAgent.Server` to embed the complete MCP SQL Agent in ASP.NET Core.
- SQL lowering is rendered directly from the validated canonical Core AST; SqlCore has no query-builder backend dependency.

Project: https://github.com/tse-wei-chen/hs-sql-agent
