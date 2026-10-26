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
  MySQL admits simple `EXPLAIN` on queries/mutations, while
  `EXPLAIN ANALYZE` remains unsupported without proven server version and
  `explain_format` session capabilities; SQLite supports `EXPLAIN QUERY PLAN SELECT`.
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

## Cross-provider static Session capabilities

`SqlCoreFacade.AnalyzeScript` exposes immutable `SessionBefore`,
`SessionAfter` and `FinalSession` snapshots. A recorded setting is a
**script or caller-declared premise only**: the analyzer does not connect
to a database, check whether a database exists or authorize access.

- **PostgreSQL** retains the restricted single ordinary `SET search_path
  TO schema` / `SET search_path = schema` model, including previously
  proven transaction rollback snapshots.
- **MySQL** recognizes `USE database` with a plain ASCII identifier.
  `CurrentDatabase` preserves this requested context. Only an explicit
  caller `SqlProviderCapabilityProfile` with
  `SessionSettings["LOWER_CASE_TABLE_NAMES"]="1"`, and a safe fully
  qualified virtual Catalog, can establish `IsDatabaseScopeProven` and
  safely resolve subsequent unqualified table names. Otherwise switching
  databases is `PartiallyAnalyzed`, invalidates old Catalog proofs, and
  subsequent name binding remains unverified.
- **SQL Server** recognizes `USE database` but never confuses database
  context with a default schema. The old Virtual Catalog is invalidated
  because unqualified and two-part table names may refer to a different
  database. It recognizes `SET NOCOUNT ON|OFF` as a separate
  `Nullable<bool>` `NoCount` snapshot without treating output-message
  settings as authorization or schema changes.
- Oracle, Firebird, SQLite, and unmodeled provider-specific session
  statements are fail-closed. Unknown or syntactically unsupported
  settings discard previous scope/option and Catalog proofs. Quoted,
  multipart, wildcard, conditional or other complex `USE` statements
  are not admitted by this restricted grammar.

`DetailedEffects` distinguishes `SetSearchPath`, `UseDatabase`, and
`SetNoCount`. A successful static `USE` never certifies that it ran
on the server. MySQL and SQL Server do not borrow PostgreSQL's
transactional `search_path` rollback behavior.

References: [MySQL USE](https://dev.mysql.com/doc/refman/8.4/en/use.html),
[SQL Server USE](https://learn.microsoft.com/en-us/sql/t-sql/language-elements/use-transact-sql),
[SQL Server SET NOCOUNT](https://learn.microsoft.com/en-us/sql/t-sql/statements/set-nocount-transact-sql).

## Transactional catalog snapshots and provider-specific TCL

`SqlCoreFacade.AnalyzeScript` uses a closed, immutable `TransactionContext`
to track a **static Virtual Catalog**, not to execute SQL or guarantee any
server transaction completed. DQL, DML, triggers and external grants remain
outside effective transaction execution/authorization proof.

- **PostgreSQL:** `BEGIN`/`START TRANSACTION`, `COMMIT`, `ROLLBACK`,
  `SAVEPOINT name`, `ROLLBACK TO [SAVEPOINT] name`, and
  `RELEASE [SAVEPOINT] name` retain transactional DDL catalog snapshots.
  Most-recent matching duplicate names win; `ROLLBACK TO` keeps the targeted
  mark, whereas `RELEASE` removes it and newer marks. Plain
  `SET search_path TO schema` / `= schema` is now snapshotted atomically
  with the Virtual Catalog on every savepoint: `ROLLBACK TO` restores both
  without ending the transaction; `RELEASE` preserves the current Session
  scope, and full `ROLLBACK` restores the state before `BEGIN`.
  Conditional settings, `SET LOCAL` and unknown Session commands remain
  fail-closed.
- **SQLite:** Basic `BEGIN`/`COMMIT`/`ROLLBACK`, nested savepoints, and
  standalone outermost `SAVEPOINT` are supported. Releasing that outermost
  savepoint ends the transaction; `ROLLBACK TO` leaves it active. The
  existing SQLite dynamic-affinity type-boundary remains unverified.
- **SQL Server:** A deliberately bounded `BEGIN TRAN[SACTION]`,
  `COMMIT TRAN[SACTION]`, `ROLLBACK TRAN[SACTION]`,
  `SAVE TRAN[SACTION] name`, and `ROLLBACK TRAN[SACTION] name` subset is
  analyzed. Nested `BEGIN TRAN` is **not** modeled as an independently
  committable nested transaction (`@@TRANCOUNT` and potential distributed
  promotion matter); instead it invalidates prior Catalog rollback proofs.
  Savepoint names are case-sensitive and capped to 32 ASCII identifier
  characters, as required by SQL Server.
- **MySQL:** Basic explicit `START TRANSACTION` / `BEGIN` and boundaries
  can be recognized, but `CREATE`/`ALTER`/`DROP` during an active
  transaction fail closed with `SQL_TRANSACTION_IMPLICIT_COMMIT_UNPROVEN`,
  because DDL can implicitly commit and not be reversed by `ROLLBACK`.
  DML in an active transaction similarly loses rollback proof unless the
  storage-engine transaction contract is established separately.
- **Oracle / Firebird** remain explicitly unmodeled for these TCL forms.
  Unsupported names, subcommands, runtime conditions, distributed options,
  nested transactions and unknown savepoints invalidate the prior transaction
  and Virtual Catalog proofs, rather than creating rollback deltas.

Savepoints are bounded to 32 active marks per transaction. All syntax is
analyzed using the source dialect's strict lexer and typed F# commands. The
returned `SqlCatalogChange` on a rollback describes a simulated catalog
snapshot transition only, never a database write or execution guarantee.

Vendor documentation:
[MySQL implicit commits](https://dev.mysql.com/doc/refman/8.4/en/implicit-commit.html),
[SQL Server SAVE TRANSACTION](https://learn.microsoft.com/en-us/sql/t-sql/language-elements/save-transaction-transact-sql),
[PostgreSQL ROLLBACK TO SAVEPOINT](https://www.postgresql.org/docs/current/sql-rollback-to.html),
[PostgreSQL SET](https://www.postgresql.org/docs/current/sql-set.html),
[SQLite Savepoints](https://www.sqlite.org/lang_savepoint.html).

## Transaction effect provenance (static evidence, not execution)

`SqlCoreFacade.AnalyzeScript` exposes
`SqlStatementAnalysis.TransactionProvenance` and the
`SqlScriptAnalysisResult.TransactionProvenance` read-only statement-order
collection. Each typed `SqlTransactionEffectProvenance` includes:

- `Origin`: `OutsideTransaction`, `TransactionControl`,
  `CatalogMutation`, `SessionMutation`, `PossibleImplicitCommit`,
  `UnprovenRuntimeEffects` or `UnknownTransactionState`.
- `ControlOperation`: a closed grammar classification of
  `Begin`, `Commit`, `Rollback`, `Savepoint`,
  `RollbackToSavepoint`, `ReleaseSavepoint`, or `Unknown`. The
  classification comes from the **same F# transaction parser**, not a
  string-prefix test or an execution signal.
- `RollbackScope`: `NotApplicable`, `StaticCatalog`,
  `StaticCatalogAndSession`, or `Unproven`. This is a proof solely
  about the in-memory Virtual Catalog/Session snapshot, not the server.
- `TransactionBefore`, `TransactionAfter`, `PotentialEffects`
  and `Span` for deterministic script diagnostics.

**`RuntimeCertainty` is always `Unknown` and
`IsServerRollbackProven` is always `false`**. Neither parsed DML nor
a static `ROLLBACK TO` proves an effective permission, that SQL actually
ran, or that the storage engine committed/rolled back. PostgreSQL
modeled search-path changes can carry `StaticCatalogAndSession` evidence;
a modeled DDL Catalog snapshot carries `StaticCatalog` evidence.
Provider-specific MySQL DDL implicit-commit warnings are preserved and
surfaced as `PossibleImplicitCommit`; unproven runtime mutation,
functions, settings and transaction states explicitly retain `Unproven`.
The model does not invent a transitive, effective transaction-effect graph.

Example usage:

```csharp
var report = SqlCoreFacade.AnalyzeScript(
    "BEGIN; SET search_path TO app; SAVEPOINT s; ROLLBACK TO s; COMMIT;",
    SqlAgentToolType.Postgres,
    SqlCatalogState.KnownEmpty());

foreach (var evidence in report.TransactionProvenance)
    Console.WriteLine($"{evidence.ControlOperation}: {evidence.Origin}, " +
                      $"{evidence.RollbackScope}; server proof={evidence.IsServerRollbackProven}");
```

## PostgreSQL Function parameter symbol binding (strict subset)

The bounded SQL-language Function analyzer now accepts up to **eight named,
IN-only primitive arguments**, in declared source order. Each public
`SqlFunctionParameterSymbol` records `Ordinal` (1-based), normalized
`Name`, primitive `DeclaredType`, absolute header `Span`, and immutable
`RuntimeCertainty = Unknown`. Function definitions expose their ordered
read-only `Parameters` collection. Procedures and zero-argument Functions
continue to return an empty collection.

Only a **single standalone SELECT without FROM/CTEs/joins/filter/set
operations** may use these named parameter symbols for return evidence.
The result must be a single unquoted `Expr.Column` with exactly one
identifier component, and its normalized name must match a declared
parameter whose primitive type **exactly** matches `RETURNS`. This creates
`SqlFunctionReturnEvidence.ParameterSymbolMatch`, not an executable
binding or server type proof.

```sql
CREATE FUNCTION app.identity(amount integer)
RETURNS integer LANGUAGE SQL
AS $body$ SELECT amount; $body$;
```

Unknown names, duplicate declarations, missing names, quoted symbols,
OUT/INOUT/default parameters, return type mismatches, unmodeled expressions,
ambiguous table-column names, and multi-statement parameterized bodies
remain fail-closed. PostgreSQL `$1` positional parameters are **not accepted by the closed
general query parser**. The Function Analyzer now has a **separate bounded, non-executable
expression parser** for one FROM-free `SELECT` using `$n` ordinal IN
arguments. The closed internal expression DU accepts grouping, numeric
`+`/`-`/`*`, boolean `NOT`/`AND`/`OR`, compatible primitive literals,
named argument references mixed with `$n`, and exact-typed or int4-to-int8
widening `CAST` of a directly referenced argument. Parentheses and operator
precedence are parsed structurally; casts of computed expressions stay
unsupported. The internal tokenizer is bounded to 64 tokens, expression
depth to 16, and each ordinal must refer to one of at most eight declared
IN parameters. Direct ordinal references receive `PositionalParameterMatch`,
narrow casts receive `ExplicitCastMatch`, and arithmetic/boolean expressions
receive `PrimitiveExpressionMatch`. This grammar never substitutes SQL text,
introduces general SQL identifiers, exposes executable bindings, or relaxes
ordinary query/mutation compilation. SQL comments, joins, aggregates,
dynamic SQL, multiple statements, arbitrary casts, unmodeled types and
other providers stay fail-closed. The inferred type is a static nominal
observation, never proof of server execution, overload resolution or rights.

This is a narrow *typed symbol* capability. Bounded primitive arithmetic
and boolean expressions now have nominal type evidence, but full SQL type
inference, overload resolution and routine execution proof remain unsupported. See
[PostgreSQL SQL function arguments](https://www.postgresql.org/docs/current/xfunc-sql.html).


## PostgreSQL SQL Function return contracts (static only)

The `AnalyzeScript` procedural analyzer accepts a restricted
`CREATE [OR REPLACE] FUNCTION schema.name(named_type_arguments)`
`RETURNS integer|bigint|boolean|text|void LANGUAGE SQL AS $tag$...$tag$`
(or `AS ... LANGUAGE SQL`) grammar. It never produces executable SQL.

For non-void results, the final statement must be a single FROM-free
SELECT projection, without CTE, WHERE, set operation, ORDER BY or LIMIT.
The closed F# AST can establish only these evidence categories:

- `LiteralMatch`: exact scalar literal with Int32 bounds for `integer`.
- `ParameterSymbolMatch`: directly referenced, unquoted IN-only parameter
  with the same primitive return type.
- `ExplicitCastMatch`: compatible literal CAST, or typed parameter CAST
  preserving its type or widening `integer` to `bigint`. Text-to-number
  and narrowing parameter casts remain unsupported.
- `PrimitiveExpressionMatch`: at most 64 AST expression nodes involving
  typed local parameter names, integer literals, unary numeric `+`/`-`,
  integer `+`/`-`/`*`, and boolean `NOT`/`AND`/`OR`. Mixed
  int4/int8 arithmetic has the `bigint` result type.
- `VoidNoValueRequired`: void does not require a final result SELECT.

Both the source-validated Query AST and Function-only positional AST
now use one internal `RewritePrimitiveTypeEvidence` rule set for
primitive literals, unary operators, integer/boolean binary operators,
and exact or widening parameter CAST. This module belongs to Function
analysis only: it is **not** a general Query/DML type checker, Catalog
binding proof or executable compiler path. Paired grammar tests verify
agreement on both accepted and rejected type shapes.

All these are **static nominal type-shape observations**, not proof
of server-side function/operator resolution, successful conversion,
runtime arithmetic overflow safety, actual execution or permission.
`RuntimeCertainty` remains `Unknown` throughout.

```sql
CREATE FUNCTION app.shift(n integer) RETURNS integer
LANGUAGE SQL AS $body$ SELECT n * 2 + 3; $body$;

CREATE FUNCTION app.inactive(enabled boolean) RETURNS boolean
LANGUAGE SQL AS $body$ SELECT NOT enabled; $body$;
```

Potential mutations inside a function body are **not** actions executed
by CREATE FUNCTION. Dynamic SQL, unmodeled positional argument forms, arbitrary
function calls, division, comparisons, NULL inference, overloaded
operators, unmodeled casts, FROM-sourced names, implicit conversions
and unproven expressions fail closed without a partial definition.
Relevant diagnostic codes include `SQL_PROCEDURAL_RETURN_UNPROVEN`
and `SQL_PROCEDURAL_BODY_UNSUPPORTED`.

References: [PostgreSQL SQL functions](https://www.postgresql.org/docs/current/xfunc-sql.html)
and [CREATE FUNCTION](https://www.postgresql.org/docs/current/sql-createfunction.html).

## Bounded PostgreSQL SQL procedure body AST

`ProceduralDefinitions` offers an ordered, bounded, non-executable
syntactic inspection of qualified zero-argument SQL-language routines,
using the shared Script Parser, closed F# AST and source span system.

```sql
CREATE PROCEDURE app.cleanup() LANGUAGE SQL AS $body$
  SELECT id FROM app.orders;
  DELETE FROM app.orders WHERE id = 1;
$body$;
```

The body is limited to 16 statements and 16,384 characters. Only
parsed DQL/DML statements supply immutable kind, operation, referenced
tables, absolute spans and potential effects. These observations do
**not** certify semantic Catalog binding, server validity, creation,
execution, authorization or effective permissions. Defining a routine
conservatively invalidates Virtual Catalog completeness. Dynamic SQL,
unmodeled DDL/TCL and nested procedural statements never produce a
partially trusted parsed body.

## Opaque procedural invocation analysis

`SqlCoreFacade.AnalyzeScript` reports `ProceduralIntents` on each statement
and on the full script, preserving source order. The first provider-specific
subset recognizes **zero-argument static procedure invocations** only:

- PostgreSQL: `CALL schema.procedure()`.
- MySQL: `CALL database.procedure()` or `CALL database.procedure`.
- Oracle: `CALL schema.procedure()`.
- SQL Server: `EXEC schema.procedure` or `EXECUTE schema.procedure`.
- Firebird: `EXECUTE PROCEDURE procedure` (a syntactic procedure name
  only, **not** a resolved package/database identity).
- SQLite: no native stored procedure invocation is claimed.

The API exposes `SqlProceduralInvocationKind`, the syntactically normalized
`Routine` name, `PotentialEffects`, `Span`, and fixed proof boundaries:
`BodyAnalyzed = false`, `RuntimeCertainty = Unknown`.
`MayExecuteSql = true` describes *potential server-side behavior* and
does not cause execution. The typed `InvokeProcedure` DetailedEffect always
has unknown certainty and **no trusted ObjectIdentity**. Since a procedure
may issue DQL, DML, DDL, security changes, session changes, transaction
control or dynamic SQL, all potential effect categories include
`UnknownEffects`. Every invocation invalidates the Virtual Catalog's
closed-world completeness; invocations in active transactions invalidate
transaction rollback proof.

Parameters, expressions, dynamic `EXEC`, unqualified/ambiguous SQL Server,
PostgreSQL, MySQL and Oracle names, and procedure/function/trigger bodies
remain **fail-closed** with `SQL_PROCEDURAL_UNSUPPORTED`. Unknown calls do
not emit partial intents. Routine definitions and anonymous PL/SQL, T-SQL or
Firebird blocks are treated as **opaque script regions**, so their internal
semicolons cannot be misclassified as validated independent SQL.
No procedure body is parsed, compiled, executed, or authenticated, and an
invocation is not a proof that the target exists or is callable.

Sources:
[PostgreSQL CALL](https://www.postgresql.org/docs/current/sql-call.html),
[MySQL CALL](https://dev.mysql.com/doc/refman/8.4/en/call.html),
[SQL Server EXEC](https://learn.microsoft.com/en-us/sql/relational-databases/stored-procedures/execute-a-stored-procedure),
[Firebird EXECUTE PROCEDURE](https://www.firebirdsql.org/file/documentation/chunk/en/refdocs/fblangref60/langref-dml-execproc.html).


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
