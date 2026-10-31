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

## Typed Script parsing and analysis options

The CLR-friendly `SqlScriptParseOptions` and `SqlScriptAnalysisOptions`
collect source dialect, optional source capability profile, and cooperative
cancellation into immutable inputs. Analysis additionally accepts an explicit
starting `SqlCatalogState`. A missing Catalog defaults to **Unknown**
(open world), **not** `KnownEmpty()`; only the caller can claim a known empty
starting Catalog. Existing positional overloads still work unchanged, using
the same source-profile checks, grammar, resource limits and diagnostics.

```csharp
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;

using var cts = new CancellationTokenSource();
var parseOptions = new SqlScriptParseOptions(
    SqlAgentToolType.Postgres, cts.Token);
var syntax = SqlCoreFacade.ParseScript(
    "CREATE TABLE demo (id INT); SELECT id FROM demo;", parseOptions);

var analysisOptions = new SqlScriptAnalysisOptions(
    SqlAgentToolType.Postgres, SqlCatalogState.KnownEmpty(), cts.Token);
var result = SqlCoreFacade.AnalyzeScript(
    "CREATE TABLE demo (id INT); SELECT id FROM demo;", analysisOptions);
Console.WriteLine(result.EffectCoverage);
```

A source capability profile may also be supplied to both options types,
e.g. for version-gated Oracle grammar. Provider/profile mismatches still fail;
option objects never imply successful binding, authorization or SQL execution.

## Opt-in cooperative compiler cancellation (P6)

The CLR-facing `SqlCoreFacade` exposes cancellation-token overloads for
`CompileQuery`, `CompileDml`, `TryCompileQuery` and `TryCompileDml` on
their basic SQL-text entry points, plus parsed-statement Query/DML compilation.
Existing overloads and return types remain unchanged. The token applies to
the same strict, synchronous F# Parse → Bind → Normalize → Validate → Policy →
Render pipeline and its resource-budget checkpoints. No new compiler,
database connection, SQL executor, or authorization path is introduced.

```csharp
using var cts = new CancellationTokenSource();
var compiled = SqlCoreFacade.CompileQuery(
    "SELECT 1", SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
    new SqlPlanValidationContext("cancellation-example"),
    new SqlExecutionPlanPolicy(), cts.Token);
```

Pre-cancelled requests and cancellation observed during compilation throw
`OperationCanceledException` (including the `TryCompile...` overloads):
cancellation is **not** a SQL failure diagnostic or a successful command.
An active token preserves existing SQL parameterization, policy and evidence.
Cancellation is cooperative at compiler checkpoints; it does not stop an
already-running database command. Each call restores its previous ambient
token scope so later independent requests are unaffected.

## P6: numeric literal overflow is a typed lexical failure

The F# Lexer represents numeric literals as signed 64-bit integers or
`DecimalLiteral` values. Well-formed integers, decimal fractions and
scientific notation that exceed both supported ranges now fail closed with
`SqlParseException` and the exact
`SQL_LEXICAL_NUMERIC_OUT_OF_RANGE` diagnostic (Lexical / Syntax), including
the source span of the **entire numeric literal**. No numeric truncation,
defaulting or untyped CLR `OverflowException` is permitted.

This is shared across all six dialects, Query and Mutation compilation, and
the static Script APIs. `TryCompileQuery` retains its existing outer
`SQL_PARSE_ERROR` result code; the specific lexical code is surfaced through
`TypedDiagnostics`. Representable `Decimal.MaxValue` values remain valid.
This does not add arbitrary-precision numeric SQL grammar.

## P6: lossless decimal literal contract

A successful CLR `Decimal.TryParse` is **not** sufficient SQL semantic
evidence: it may round significant digits or underflow a nonzero literal to
zero. For all six dialects, the F# Lexer now compares canonical exact decimal
values (significant digits and decimal exponent), from both the SQL source and
the parsed `DecimalLiteral`. A difference rejects the entire numeric token
with `SQL_LEXICAL_NUMERIC_PRECISION_LOSS` (Lexical / Syntax and exact source
span), rather than accepting a **different** numeric value.

Mathematically equivalent forms such as `1e-28`, `10e-29`, fractional
trailing zeros and exact decimal zero still work. Unrepresentable `1e-29`,
`1e-1000`, and significant-digit rounding fail closed. Out-of-range
magnitudes retain the separate `SQL_LEXICAL_NUMERIC_OUT_OF_RANGE` code.

The source/target SQL capability matrix, DML policy, TryCompile's broad outer
`SQL_PARSE_ERROR` code, typed diagnostics, and public compiler signatures
remain unchanged. This is **not** arbitrary-precision SQL numeric support;
such support requires a future closed numeric AST and target-dialect proof.

## P6: numeric-token lexical work and cancellation budget

Numeric lexemes are metered in the same synchronous `RewriteResourceBudget`
session as the original SQL source scan. Each independent CLR `Int64.TryParse`
and `Decimal.TryParse` pass, as well as exact-value canonicalization, charges
a deterministic number of work units **before** entering potentially large input scans or digit-buffer
allocation. Long numeric integer, fraction, exponent and canonicalization
loops also make amortized cancellation / deadline checkpoints.

All six dialects share the same behavior through Query/DML compilation and
Script parsing/analysis. Ordinary valid numeric literals, lossless precision
checks, source diagnostics and `TryCompile` contracts are unchanged.
Pathological single numeric tokens may now be rejected with
`SQL_COMPILER_STAGE_WORK_LIMIT` (Lexical / Resource) even while they remain
below the independent 4 Mi-character total SQL input limit. Work quotas
apply to total parsing activity, not just the number of emitted tokens; no
independent global numeric budget or new SQL grammar is introduced.

## P6: exact DECIMAL/NUMERIC CAST source/target capability proof

The closed F# `SqlDecimal(precision, scale)` AST is not sufficient evidence
that the source and target database can implement the declared SQL shape.
The compiler now verifies explicit precision/scale independently at source
parsing and target capability validation, including same-provider CASTs:

| Provider | Maximum explicit precision | Maximum scale | Additional proof |
| --- | ---: | ---: | --- |
| PostgreSQL | 1000 | scale ≤ precision in the portable Core model | Unbounded NUMERIC remains native PostgreSQL |
| MySQL | 65 | 30 | Cross-provider unbounded NUMERIC is rejected (MySQL defaults to DECIMAL(10,0)) |
| SQL Server | 38 | precision | Includes same-provider CAST |
| Oracle | 38 | precision in the portable Core model | NUMBER maps by exact modeled precision |
| Firebird | 18 on v3; 38 on v4+ | precision | Precision 19..38 requires explicit 4.0+ source/target capability profiles |
| SQLite | No fixed-point precision proof | No fixed-point scale proof | Native NUMERIC affinity remains valid; cross-provider bounded/unbounded exact NUMERIC CAST cannot claim DECIMAL(p,s) fidelity |

Out-of-range declarations fail closed with typed
`SQL_SOURCE_TYPE_REJECTED` source diagnostics, or target compilation errors
before rendering and authorization. `ParseScript` retains its existing
inspection contract: a failing statement is marked `Invalid` with the
corresponding diagnostic, rather than allowing it to compile. No precision
clamping, lossy default scale inference, or arbitrary-precision CLR literal
expansion is introduced.

SQL numeric literals and CAST *type declarations* are distinct: the lexer
still accepts only exactly representable CLR integer/decimal literals; this
CAST capability proof does not claim to execute 1000-digit numeric parameters.
The separate target DB / provider must prove or reject the actual data shape.

## P6: first closed exact-NUMERIC literal capability (PostgreSQL)

PostgreSQL SQL text can now represent **bounded arbitrary-precision NUMERIC
literals** (initial acceptance envelope: at most 1000 decimal integer/fraction
positions, up to 4096 source characters) without coercing the source value to
CLR `decimal` or `double`. Source grammar is lexed and validated first; the
new private F# `ExactNumeric` DU then carries the original verified decimal
lexeme and independently folded unary sign. Exactly representable ordinary
CLR integer/decimal literals retain their original typed parameter path.

Query and DML rendering binds the exact numeric as a **text parameter** and
emits `CAST(parameter AS NUMERIC)` on PostgreSQL; it never concatenates the
numeric lexeme into executable SQL. The declared SQL numeric is not rounded
before server evaluation. Static type analysis yields **Numeric family-only**
evidence, never an invented exact primitive/overload proof.

All **five other source dialects** keep their previous lexical rejection of
numbers they cannot represent exactly within the current CLR scalar model.
Compilation from PostgreSQL to any unproven other target refuses
`numeric.exact_literal` before SQL rendering. Legacy CLR AST projection also
fails closed instead of turning the value into a string, decimal, or float.
This is a staged capability expansion, **not** a claim of six-provider
driver-level exact-numeric parity; each future target needs proven parameter,
precision, scale and runtime driver semantics first.

The existing source length, per-stage work, compilation deadlines, typed
lexical diagnostics and cancellation remain active. This feature does not
alter the six-dialect `DECIMAL(p,s)` CAST type-declaration bounds.

## P6: bounded exact NUMERIC for MySQL and SQL Server targets

The closed F# `ExactNumeric` model (introduced for PostgreSQL source SQL)
can now lower **losslessly** to MySQL or SQL Server DECIMAL when the precise
integer/fraction digit shape fits their proven bounds:

- **MySQL target:** explicit `DECIMAL(p,s)`, `1 <= p <= 65`, `0 <= s <= 30`.
- **SQL Server target:** explicit `DECIMAL(p,s)`, `1 <= p <= 38`, `0 <= s <= p`.

The compiler strips insignificant leading/trailing coefficient zeroes,
applies the verified decimal exponent exactly, then derives the minimum
`p = max(1, integerDigits + fractionalDigits)`, `s = fractionalDigits`.
It checks these bounds **before** allocating the final fixed-decimal string,
without any CLR decimal or double rounding. The result is a **text-valued
bound parameter** plus `CAST(parameter AS DECIMAL(p,s))`; scientific source
literals like `1e-29` are bound as exact `0.000...001`, *not* as text
scientific notation interpreted differently by the target driver.

Oversized integer, fractional scale, total-precision or exponent cases
reject with `numeric.exact_literal` during target capability validation.
The PostgreSQL source's existing 1000-digit envelope stays unchanged;
**MySQL and SQL Server source dialects are not expanded by this change**.
PostgreSQL targets retain unbounded `CAST(parameter AS NUMERIC)`; Oracle,
Firebird and SQLite remain fail-closed pending independent exact parameter
and server-equivalence proofs.

Positive and negative Query/DML tests cover both targets, and separate
database-backed tests compare the actual MySQL and SQL Server results
after driver binding with exact expected decimal text. These live cases
run in **separate CI shards**: `sqlagent-test` enforces MySQL parity via
`TEST_MYSQL_CONNECTION`, while `sqlagent-fence-sqlserver` enforces SQL
Server parity via `TEST_SQLSERVER_CONNECTION`. The selected provider is
declared by `SQLCORE_EXACT_NUMERIC_LIVE_PROVIDER`; a missing required
connection fails its designated CI job, not silently skips the proof.
The tests are read-only and do not modify database state. The readback is explicitly
stringified *on the server* to avoid CLR decimal deserialization limits.

## P6: Oracle exact NUMBER target proof and locale-stable parameterization

The existing closed F# `ExactNumeric` model now supports a third bounded
target: **Oracle `NUMBER(p,s)`**, with `p <= 38` and nonnegative
`s <= p`. It uses the same exact coefficient/exponent-to-fixed-decimal
normalization as MySQL and SQL Server, with explicit source/target proof and
no floating-point coercion. Values exceeding 38 combined decimal digits
or 38 fractional places fail with `numeric.exact_literal` before rendering.

Oracle SQL text is emitted as a **bound string parameter** inside
`CAST(TO_NUMBER(parameter, generatedFormat, 'NLS_NUMERIC_CHARACTERS=''.,''') AS NUMBER(p,s))`.
The generated number format model contains only `FM`, `9` and optional
`D` digits derived from the proven `p,s`. The source numeric token is
never spliced into SQL. This explicit `TO_NUMBER` NLS argument is necessary:
Oracle's implicit VARCHAR2-to-NUMBER conversion depends on the session's
decimal separator. Live parity intentionally sets the session decimal
character to a comma and verifies the original fixed decimal value using
server-side invariant `TO_CHAR` (no CLR `decimal` deserialization).

The Oracle 23ai database-backed read-only tests run in the existing
`sqlagent-oracle-ddl-parity` CI shard, using its pinned Oracle service and
`TEST_ORACLE_CONNECTION`. The shard sets
`SQLCORE_ORACLE_EXACT_NUMERIC_LIVE=true` and missing required Oracle
credentials fail the job. Other CI shards do not claim to have executed
Oracle parity.

This expansion affects **PostgreSQL source SQL compiled to an Oracle
target**; the Oracle source lexer still rejects non-CLR-representable
numeric tokens pending independent source semantics proof.
`NUMBER(p,s)` beyond the portable F# type model, and all unproven
SQLite/Firebird exact target conversions, remain disabled.

**Firebird assessment:** version 4+ offers `INT128`-backed fixed
`DECIMAL/NUMERIC` (up to 38 digits), but `DECFLOAT(34)` has only
34 significant digits and may round longer input. A Firebird server
version profile alone does not prove driver string binding, decimal
rounding, or stable format conversion. Both remain fail-closed until
independent live target tests establish the intended exact-value
contract; Oracle's NUMBER proof cannot be reused as Firebird proof.

## P6: Firebird 4+ typed INT128 exact integer target

Firebird 4+ supports `NUMERIC/DECIMAL` declarations up to precision 38,
with `INT128` storage backing precision 19..38 in dialect 3.
**This does not guarantee that a VARCHAR-bound numeric reaches INT128
losslessly.** The Firebird 5 / .NET provider live test discovered that
string parameters cast as `NUMERIC(38,s)` are coerced via CLR Decimal:
38-digit values can overflow `DbValue.GetDecimal()`, while some
fractions are rounded or underflow to zero. Such binding is forbidden.

The currently *proven* Firebird ExactNumeric target is therefore
**integer-only, scale 0, at most 38 digits**. The closed F# model
converts its lexically validated PostgreSQL-source number into an exact
`BigInteger` parameter; the generated SQL uses
`CAST(CAST(parameter AS INT128) AS NUMERIC(p,0))`. A target-side
`numeric.decimal_extended` proof must attest an explicit Firebird
ServerVersion 4.0+ profile for precision above 18. Firebird 3 and
unprofiled >18-digit values are rejected.

All fractional exact values that cannot be represented losslessly by
the existing CLR Decimal model, including `1e-29` and 29-37 decimal
fractional places, **remain fail-closed** with
`numeric.exact_literal` even under Firebird 5. No reinterpretation
of a scaled `INT128` as an integer, no string-to-decimal fallback,
and no automatic downgrade to `DECFLOAT(34)` is permitted.

Live parity runs against the pinned Firebird 5.0.4 service with the
actual Firebird .NET driver, comparing readback of 31- and 38-digit
positive and negative integer values. This readback is performed on
the server as VARCHAR, without CLR decimal deserialization. The
dedicated `sqlagent-firebird-ddl-parity` job must pass; absence of
`TEST_FIREBIRD_CONNECTION` is a CI failure.

The source SQL dialect for high precision remains PostgreSQL;
Firebird source syntax above CLR decimal precision is not enabled.
A future fractional Firebird capability needs separately proven
driver-level scaled INT128 or another lossless wire format before
`NUMERIC(p,s)` for `s > 0` can be allowed.

## P6: isolated 256 KiB capability and deep-DML stack gate

The independent `sqlagent-capability-stack` GitHub Actions job runs an opt-in,
**subprocess-isolated** stack probe in both Debug and Release:

```bash
dotnet run -c Release --project backend/benchmarks/HsSqlAgent.SqlCore.Benchmarks -- --capability-stack-probe
```

The gate executes 30 independent child processes (six providers, each with
three capability-profile scenarios and two 2,048-predicate DML paths) on
262,144-byte CLR thread stacks. Matrix cases compare every ordered capability
ID, category, status and complete detail against the same runtime's
normal-stack fingerprint; the DML cases must either compile an explicitly
selective DELETE or reject an unbounded DELETE with the exact Policy diagnostic.
A fatal `StackOverflowException` or timeout is reported as a failed child
instead of terminating the CI test host. This does **not** attest to database
execution, approve writes, or declare any new SQL capability.

The large capability list uses streaming construction; changes must preserve
matrix content and ordinal order, existing policy/evidence contracts, and this
small-stack acceptance gate. The matrix's public version and methods are
unchanged by the gate.

## INSERT target shape and positional type evidence

Strict F# Mutation analysis checks the number of values in **every** VALUES
row and the projected width of INSERT ... SELECT against the proven target
columns. Explicit column-list cardinality and duplicate-target syntax remain
the existing strict Parser's responsibility. For INSERT statements **without**
a target column list, only an ordered, completely known, closed-world Virtual
Catalog produced by the static DDL analyzer can provide a proven physical
column order. That evidence also enables positional numeric/text/etc. type
family mismatch detection, including INSERT ... SELECT from a previously
proved PostgreSQL View.

A generic `SqlSchemaSnapshot` does **not** carry trustworthy physical
column-order provenance. The analyzer therefore never invents implicit
positional type evidence from a snapshot. SQLite still has dynamic affinity:
static column names/shape may be checked but fixed type families are not
asserted. Parser and catalog diagnostics do not authorize INSERT execution,
and an unknown/open Catalog remains unverified.

## Qualifier identity and Mutation source scope

Strict schema binding proves the uniqueness of a **relation qualifier**
before looking up a qualified column. If the write target alias and an
unaliased UPDATE FROM / DELETE USING source expose the same qualifier, or
two unaliased sources in different schemas expose the same table-name tail,
`src.unique_column` is **not** proof of a unique source even when that
column exists in only one of those relations. Binding fails closed with
`SQL_SCHEMA_QUALIFIER_UNPROVABLE`; type-family evidence never upgrades an
ambiguous source. An explicit distinct alias, or a fully qualified
`schema.table.column` identifying one source, remains supported.

This safety rule also applies to DQL with a host-supplied schema snapshot
for all six SQL dialects. It does not conflate qualified source names or
change lexical outer-scope shadowing, executable SQL permissions or the
`PartiallyAnalyzed` status of static scripts.

## Static source-match cardinality for joined mutations

`SqlStatementAnalysis.SourceMatchCardinality` is an independent, non-executable
cardinality result for PostgreSQL `UPDATE ... FROM` and `DELETE ... USING`:
`NotApplicable`, `Unverified`, or `ProvenAtMostOne`. It is deliberately
**not** part of `CatalogBinding`: verified names and type families do not prove
how many source rows can match one target row.

In a fully known, closed Virtual Catalog, a proven physical PRIMARY KEY or
UNIQUE constraint can establish at most one row from an auxiliary source
when **every** key column is constrained by a mandatory `=` equality to
a literal, the target row, or an already proven single source row. Multiple
source relations are handled by a bounded fixpoint, so unanchored cycles,
`OR`, non-key predicates, partial composite keys, ordinary non-unique
indexes and View outputs do **not** create a positive proof. Foreign keys
are not uniqueness proofs. The analyzer uses the closed, bound F# AST,
not a SQL text pattern or assumptions about actual records.

The result describes a *hypothetical* row-matching upper bound under the
modeled schema, not a guarantee of deterministic UPDATE value evaluation,
live server uniqueness, or runtime behavior. An unknown/invalid Catalog,
unvalidated statement or other SQL provider remains unverified. No SQL
execution permission, mutation approval, transaction assurance, or effect
coverage is upgraded by this property.

### Nullable UNIQUE and PostgreSQL null-safe key equality

Joined Mutation source-match evidence distinguishes ordinary `=` from
PostgreSQL `IS NOT DISTINCT FROM`. Ordinary equality is NULL-rejecting,
so a nullable physical UNIQUE key can still bound matches. A NULL-safe
comparison is accepted only when the key column is provably non-NULL
(`NOT NULL` or physical PRIMARY KEY), or the compared fixed literal or
already anchored physical column is provably non-NULL. A nullable UNIQUE key
compared NULL-safely to a nullable target column or `NULL` remains
`Unverified`: PostgreSQL permits duplicate NULLs in ordinary UNIQUE keys.
Every column of a composite key must satisfy its own comparison proof.

A uniquely resolvable **unqualified** local source column is also eligible
after schema/name binding. Ambiguous fields, `OR`, View-derived keys and
unanchored sources never gain an at-most-one proof. All classifications stay
read-only evidence and never authorize SQL execution.

### Transitive mandatory equality closure (PostgreSQL joined Mutation)

`SourceMatchCardinality` can now follow an **iterative**, resource-budgeted
equality graph from each source UNIQUE/PRIMARY key column to a literal,
the fixed target row, or an already proven single-match source. This
recognizes chains such as `d.id = s.bridge AND s.bridge = s.unique_id`,
multi-source key propagation, and independently constrained composite
keys without treating a non-key intermediate column as unique.

Only required `AND` equalities enter the graph. `OR`, `NOT`, opaque
expressions, unknown Catalog relations, views and unanchored cycles do
not supply new proof. Every traversed `IS NOT DISTINCT FROM` retains
NULL-aware semantics: a nullable UNIQUE key needs a proven non-NULL
column/literal along the chain **or** a mandatory ordinary `=` edge
(which itself excludes NULL matches). Nullable-only null-safe paths,
including ones comparing NULL to NULL, remain `Unverified`.
This is non-executable static evidence, not DML authorization, row-count
prediction, effect certainty or a live-server uniqueness attestation.

### SQL Server: exact INT match cardinality (opt-in catalog identity)

SQL Server's native `UPDATE ... FROM` and `DELETE ... FROM` now receive
a **restricted**, non-executable `ProvenAtMostOne` source-match analysis
when full Virtual Catalog name/type validation has already succeeded
under an explicit ASCII-safe `CATALOG_COLLATION` provider profile.
The typed proof reuses mandatory equality closure but permits **only
ordinary `=` with exact INT source/target/intermediate columns or
32-bit integer literals**. Every modeled physical UNIQUE/PRIMARY KEY
component must be INT and constrained by an anchored mandatory equality
path. No floating-point, BIGINT, text-collation or implicit conversion
can be borrowed to certify a unique match. No cross-provider inference
from the SQL Server comparison behavior is performed.

Unmodeled catalog collations, missing profiles, unknown table state,
unanchored sources, partial composite keys, OR predicates and
incompatible type families remain `Unverified` or are rejected by
the existing binding validator. PostgreSQL preserves its separate
NULL-aware proof; Oracle, Firebird and MySQL continue to
report `Unverified` for joined mutations unless their own provider
contract is proved. As before, cardinality evidence never authorizes
execution or upgrades DML approvals, runtime certainty or effect coverage.

### SQLite: exact INTEGER PRIMARY KEY rowid source-match evidence

A closed SQLite Virtual Catalog with fully verified physical table names
can now report *read-only* `ProvenAtMostOne` for SQLite 3.33+ `UPDATE FROM`
when a single-column, exact-type `INTEGER PRIMARY KEY` source key is
bounded by mandatory ordinary `=` predicates to a modeled exact
`INTEGER` column, a signed 32-bit integer literal, or a previously
anchored key. The shared bounded equality graph handles transitive
chains and multiple independently unique sources without borrowing
PostgreSQL or SQL Server type rules. The physical Catalog must have
simple ASCII canonical identities, and the analyzed SQL must be ASCII:
Unicode case folding is not a proven SQLite identifier contract.
All modeled SQLite DDL is a rowid
table subset; `WITHOUT ROWID`, key `DESC`, arbitrary `COLLATE` and
expression/partial indexes cannot generate trusted Virtual Catalog keys.

SQLite dynamic storage affinity still prevents complete type verification:
`NameBinding` stays `Verified`, while `TypeBinding` and
`CatalogBinding` remain `Unverified`. `INT`, `BIGINT`, `TEXT`, general
`UNIQUE`, composite keys, unknown sources, missing version provenance,
OR predicates and unproven match paths **never** acquire this proof.
A missing or open Catalog likewise stays `Unverified`. For statements
without joined sources, `SourceMatchCardinality` is `NotApplicable`.

**Critical boundary:** this is analysis evidence, not mutation approval.
The CompileDml validation context does not carry an attested closed
physical-key Catalog; CTE-prefixed UPDATE FROM with a physical source
continues to fail closed. No executable capability or general SQLite
column-type assurance is inferred.

### Oracle 26 direct-join UPDATE assignment name parity

Real Oracle 26ai live DML detects `ORA-00918` if an UPDATE SET target
is **unqualified** and a `FROM` relation has a column with the same
name. Source-name/type binding previously looked only in the physical
target for assignment names, yielding an incorrectly `Verified`
statement. Strict Catalog binding now fails closed with
`SQL_SCHEMA_UPDATE_ASSIGNMENT_AMBIGUOUS` in that case. The canonical
F# parser admits Oracle-specific `SET target.column = value` as the
unambiguous, target-qualified form; the qualifier must denote the
actual target table (or its parsed target alias), never a FROM source
alias. Other dialects retain their original assignment grammar.

The dedicated pinned real-Oracle integration job executes both the
valid target-qualified DML and the raw ambiguous spelling, checking
Oracle physical results, `ORA-00918` and `ORA-30926`. This only
strengthens static name evidence; it does not authorize execution.

### Oracle 26: conservatively proven direct-join NUMBER keys

Oracle 26+ native `UPDATE ... FROM` and `DELETE ... FROM` have a
provider-local static source-row cardinality proof only after full
Catalog name/type validation and an **explicit** Oracle 26+ source
profile. The proof admits physical PRIMARY KEY and UNIQUE constraints
whose complete key is modeled as **bare `NUMBER`**. Every participating
key/bridge/target expression must likewise resolve to a bare `NUMBER`
column, or a signed 32-bit integer literal. The existing bounded
mandatory `=` closure proves individual composite key components and
multi-source chains. Oracle unquoted identifiers are folded to
uppercase; quoted identifiers retain their exact case.

This is **not** a promise about execution: Oracle's direct-join
`UPDATE` can raise ORA-30926 when multiple source rows would update
the same target, whereas direct-join `DELETE` may delete the target
once even when the join has duplicate matches. `ProvenAtMostOne`
states only that the modeled physical key constraints bound the source
combination count per fixed target row. It does not attest to actual
server data, runtime triggers, exception absence or SQL authorization.
No inference is made for older/unspecified Oracle versions, other
numeric precision/scale declarations, string/floating coercions,
arbitrary expressions, OR, incomplete keys or View outputs. PostgreSQL
and SQL Server retain their independent proof semantics; the remaining
three providers stay unverified for joined mutations.

## Mutation source relations versus physical write targets

In static PostgreSQL `AnalyzeScript` analysis, `INSERT ... SELECT`,
`UPDATE ... FROM`, `DELETE ... USING` and subqueries in DML can read the
declared output columns/type families of a **proven** Virtual Catalog View.
The strict F# binder constructs two separate relation maps: read sources
(which can include proven Views) and physical write targets (tables only).
This prevents a View used as a source from becoming an INSERT, UPDATE or
DELETE target through catalog name/type validation. Missing and ambiguous
read-source columns and proven type-family mismatches fail closed. A removed
View, an unknown/open Catalog, or an unproven dialect retains the existing
refusal/uncertainty behavior.

This is **static Mutation analysis** rather than permission to update a View
or an executable SQL compiler feature. `CatalogBinding = Verified` does not
mean execution or policy approval. Analysis remains `PartiallyAnalyzed`
and effect coverage remains `Partial`; SQL Server/MySQL/SQLite/Oracle/
Firebird do not inherit PostgreSQL-only View semantics.

## PostgreSQL DQL over proven Virtual Catalog views

After a fully modeled PostgreSQL `CREATE VIEW`, `AnalyzeScript` can bind
subsequent `SELECT` statements to the view's declared output names and
type families, including direct reads, aliases, wildcard projections, CTEs,
JOINs and derived queries. Binding reads `SqlCatalogState.Views` without
changing or impersonating the physical `Tables` Catalog. Invalid or
ambiguous columns and demonstrably incompatible typed predicates remain
fail-closed, and dropping a View invalidates later references. An open or
incomplete Catalog still cannot prove any of these names.

This is **static DQL analysis only**, initially for PostgreSQL's proven View
subset. Other dialects do not inherit this capability. Query
`CatalogBinding = Verified` still means only static name/type evidence;
`Status = PartiallyAnalyzed` and `EffectCoverage = Partial` deliberately
remain unchanged. It does not authorize SQL execution or establish runtime
function effects. Views are never treated as physical INSERT/UPDATE/DELETE
targets by this feature.

## MySQL 8.0 statement-level WITH UPDATE (native, single-table subset)

MySQL 8.0+ read-only non-recursive statement-level CTEs may precede
a single-table UPDATE. The compiler uses the same closed F# `Update.Ctes`
model, Mutation WHERE authorization, physical read/write allowlists,
catalog/typed-column proofs, source/target capability checks, parameterized
native SQL rendering, work budgets and physical-only static effects.
Explicit older MySQL source or target profiles (pre-8.0) fail closed.

Multiple and aliased-output CTEs are supported within the typed query
subset. Joined/multiple-table UPDATE, CTE mutations, recursive CTEs,
MySQL RETURNING and cross-provider WITH UPDATE lowering are **not**
authorized by this milestone. Static parsing and analysis are not DML
execution authorization. Real MySQL integration verifies native-versus-
compiled affected rows and unchanged row contents.

## SQLite statement-level WITH UPDATE (native, strict source subset)

SQLite read-only, non-recursive statement-level CTEs can prefix ordinary
UPDATE statements through the existing closed F# `Update.Ctes` model.
Multiple CTEs, named output columns, bounded WHERE subqueries, and
SQLite 3.35+ RETURNING preserve Mutation policy, physical-table allowlists,
strict catalog/type proofs, parameterization and physical-only effects.

The SQLite source and target version must be 3.8.3+ for the WITH syntax.
Recursive or data-modifying CTEs, write-target aliases that lack a
native proof, and cross-provider WITH UPDATE lowering remain fail-closed.
SQLite UPDATE FROM is admitted only when the single FROM source is a
read-only statement-level CTE **structurally proven at most one row**:
a tableless SELECT or an ordered chain of CTEs that each reads only one
earlier proven singleton CTE. The proof examines the bound closed AST and
rejects set operations (including UNION ALL), JOINs, physical tables,
derived sources, recursive CTEs, nested CTEs and multiple FROM entries.
This protects SQLite's provider-specific arbitrary-row selection when
a target matches multiple source rows. SQLite 3.33+ source and target
UPDATE FROM capability and the existing bounded mutation/allowlist and
schema/type policies remain mandatory. This compiler-local cardinality
gate does not upgrade the independent public SourceMatchCardinality
analysis or authorize execution.

### SQLite CTE UPDATE FROM: opt-in live INTEGER primary-key assurance

A separately verified opt-in path allows one physical named `FROM` source in
native `WITH ... UPDATE ... FROM` only when source and target tables have
actual single-column `INTEGER PRIMARY KEY` rowid aliases. The host mints
`SqliteUpdateFromKeyAssurance.VerifyLive(connection, snapshot, targetTable,
targetPk, sourceTable, sourcePk)` using an open SQLite connection. It
independently checks SQLite 3.33+, `PRAGMA table_info` and `PRAGMA
index_list`, rejects indexed/composite/non-rowid primary keys and limits
identifiers to simple ASCII. A standalone schema snapshot or virtual
Catalog analysis is **not** an executable uniqueness assurance.

Attach the immutable evidence to a `SqlPlanValidationContext` with its
matching `SqlSchemaSnapshot` using
`WithSqliteUpdateFromKeyAssurance(assurance)`. The compiler checks the
snapshot SHA-256, both SQLite source/target profiles >= 3.33, exact
physical table and primary-key identities, exactly one FROM source, and
mandatory, bound AST equality of those two integer PK columns. OR-guarded
equality, unknown sources, changed snapshots and cross-provider lowering
stay fail-closed; normal physical allowlists, selective WHERE, parameter
binding and all DML approval policies still apply.

The assurance identity participates in policy/fingerprint evidence, so
it cannot be reused as the same compile proof without the metadata.
Hosts **must call `assurance.IsCurrent(executionConnection)` immediately
before execution** to recheck live keys and SQLite's schema cookie, and
coordinate the check with execution against concurrent DDL. This compiler
does not itself execute SQL, lock schema, or grant mutation approval.
`SourceMatchCardinality.ProvenAtMostOne` alone never enables compilation.

## MySQL 8.0 statement-level WITH DELETE (native, single-table subset)

MySQL 8.0+ accepts read-only non-recursive CTEs preceding a single-table
DELETE through the existing F# `Delete.Ctes` model. Multiple CTEs,
explicit output aliases and WHERE subqueries retain bound Query graphs,
physical read/write allowlists, typed Catalog checks, selective DELETE WHERE
policy, source/target capability proofs, parameterization, resource limits
and static physical-table effects. No new executor or public Tool exists.

MySQL 5.7 source and target profiles fail closed. Recursive/data-modifying
CTEs, MySQL multi-table DELETE / DELETE USING, RETURNING and cross-provider
WITH DELETE lowering remain unproven and denied. Real MySQL 8 tests compare
native and compiled affected rows and unchanged records.

## Native CROSS APPLY / OUTER APPLY (SQL Server + Oracle)

`ApplyJoin(CrossApply | OuterApply, DerivedTable)` is a distinct closed F#
Join case. The shared parser, heap scheduler and scope-aware Binder
allow the derived SELECT to reference preceding FROM sources. Native
CROSS APPLY removes unmatched source rows; OUTER APPLY preserves each
unmatched left row with NULLs in the derived columns. SQL Server 2005+
and Oracle 12.1+ are supported natively. Oracle requires independent,
explicit source and target ServerVersion >= 12.1. Unsupported source
dialects, cross-provider transformations, APPLY with ON/USING, explicitly
LATERAL right operands, TVF and collection sources remain fail-closed.
Native-vs-compiled SQL Server 2022 and Oracle 26ai tests compare all
result rows and unmatched NULLs using parameterized generated queries.

## Oracle 12.1+ LATERAL inline-view query sources

Oracle Database 12c+ native LATERAL inline views reuse the closed F#
`LateralDerivedTable`, scope-aware Binder and Query Compiler. CROSS JOIN,
INNER JOIN, and LEFT JOIN LATERAL correlate to preceding FROM items while
ordinary derived queries remain uncorrelated; Oracle syntax uses ON 1=1
rather than a non-portable Boolean TRUE predicate. Source and target
must separately declare Oracle ServerVersion 12.1 or newer; 11g and
undeclared versions are rejected. RIGHT/FULL LATERAL, unmodeled lateral
table sources and Oracle/PostgreSQL/MySQL cross-provider translation remain
fail-closed. Real Oracle row-parity tests compare native and compiled
queries including unmatched LEFT JOIN NULL results.

## MySQL 8.0.14+ LATERAL derived-table query sources

MySQL 8.0.14+ now uses the existing F# `LateralDerivedTable` and
scope-aware Binder to compile native CROSS JOIN, INNER JOIN and LEFT JOIN
LATERAL. Ordinary derived tables cannot capture preceding FROM aliases.
Both MySQL source and target must explicitly declare ServerVersion 8.0.14+
in separate profiles. The proof rejects older and undeclared versions,
cross-provider translation, RIGHT/FULL LATERAL joins and non-derived inputs.

Renderer emission uses the proven provider-specific syntax; real MySQL
query parity verifies native vs compiled rows including LEFT JOIN
nonmatches and parameterized derived queries.

## SQLite statement-level WITH DELETE (native, strict source subset)

SQLite read-only non-recursive CTEs can now prefix DELETE FROM in the closed
F# `Delete.Ctes` model, including multiple CTEs, named output columns,
`WHERE` subqueries and SQLite 3.35+ `RETURNING`. Native SQLite source/target
compilation retains physical-table allowlists, nontrivial DELETE WHERE policy,
typed Catalog binding, resource budgets, parameterized rendering and
physical-only static effects. The CTE itself is never a physical delete target
or execution authorization.

Source/target profiles declaring SQLite older than 3.8.3 cannot claim this
native CTE syntax; SQLite RETURNING remains gated to 3.35+. Recursive and
data-modifying CTE bodies, SQLite DELETE USING and all cross-provider
`WITH DELETE` lowering remain fail-closed. SQLite execution parity tests
compare actual affected rows and RETURNING output.

## PostgreSQL statement-level WITH DELETE (strict source subset)

Native PostgreSQL read-only statement-level CTEs now precede DELETE FROM,
including multiple CTEs and named outputs. The closed F# Delete record owns
the CTEs; Binder resolves CTE names in USING and WHERE without granting
physical table permissions to virtual CTE names. Mutation target allowlists,
nontrivial WHERE policy, parameterization, source/target capability proofs,
resource budgets, snapshot schema/type checks and physical-only static
effect inspection remain active. The PostgreSQL renderer emits WITH ahead
of DELETE and preserves native RETURNING.

Recursive or data-modifying CTE definitions, statement-level WITH MERGE
and cross-provider lowering remain fail-closed. Legacy CLR AST conversion
refuses this shape instead of discarding the CTEs. Parsing and static
analysis do not authorize a DELETE execution.

## PostgreSQL statement-level WITH UPDATE (strict source subset)

PostgreSQL source SQL admits read-only statement-level WITH CTEs preceding
UPDATE, including multiple sequential CTE definitions and explicit output
aliases. The closed F# Update record carries the CTEs directly; Binder
validates lexical visibility of CTE names in UPDATE FROM, WHERE subqueries
and expressions, while the PostgreSQL renderer emits the WITH clause before
UPDATE. Mutation target allowlists, selective-WHERE policy, source/target
capability checks, resource budgets, parameterization, inspection and
strict Virtual Catalog proofs remain active. CTEs cannot become physical
write targets and static script analysis does not authorize execution.

Only native PostgreSQL source/target compilation is proven. Recursive or
data-modifying CTE bodies, statement-level WITH MERGE, and
cross-provider lowering remain fail-closed. Legacy CLR AST projection
refuses this new form rather than silently dropping its CTEs.

## PostgreSQL statement-level WITH INSERT SELECT (strict source subset)

The PostgreSQL source grammar accepts a read-only statement-level CTE prefix
before `INSERT INTO ... SELECT`, including multiple ordered CTEs and explicit
CTE output-column aliases. The parser keeps every CTE as a closed, typed
`Query` within the existing `InsertInput.QuerySource` F# AST: no raw SQL
splicing, new execution path, or synthetic authorization proof is introduced.
The PostgreSQL renderer emits the CTEs before `INSERT`; Mutation binding,
resource budgets, table policy, parameterization and typed diagnostics are
unchanged. Script statement classification uses the *parsed* Statement DU, so
`WITH ... INSERT ... SELECT` is exposed as Mutation, not Query.

Statement-level PostgreSQL CTE INSERT SELECT can compose with typed
RETURNING and ON CONFLICT DO NOTHING through existing DML policy, proof and
PostgreSQL-native rendering. ON CONFLICT DO UPDATE is parsed, but remains
fail-closed at capability validation without explicit source-row uniqueness
assurance matching the complete conflict target. Existing target permissions,
parameterization and conflict-target proofs are not relaxed.

SQLite independently supports the same restricted read-only statement-level
WITH INSERT SELECT grammar. SQLite RETURNING requires a proven source
ServerVersion >= 3.35, while SQLite ON CONFLICT DO NOTHING requires a
proven source ServerVersion >= 3.24; a missing or older source capability
profile remains fail-closed. Both modifiers also pass the existing target
capability checks rather than inheriting PostgreSQL's version guarantees. Unlike PostgreSQL, SQLite's native INSERT SELECT UPSERT grammar
is ambiguous when the SELECT has FROM but no WHERE. The strict SQLite source
parser therefore requires an explicit SELECT WHERE for that combination
and does not add an implicit predicate. Native SQLite ON CONFLICT
DO NOTHING is admitted only for a SQLite source and SQLite target;
INSERT SELECT ON CONFLICT DO UPDATE remains fail-closed even if a caller
supplies an explicit source-uniqueness assurance. Cross-provider conflict
lowering remains unproven. Actual in-memory SQLite parity checks compare
native writes and RETURNING rows against compiled commands.

Both proven dialects still reject recursive statement CTEs, data-modifying
CTE bodies, top-level CTE INSERT VALUES or DEFAULT VALUES, and nested input
WITH. Top-level CTE UPDATE, DELETE, MERGE and MySQL, SQL Server, Oracle and
Firebird sources retain strict rejection until independently proven.
Parsing and static analysis never authorize DML execution.

## SQL Server GO comments in static scripts

The SQL Server Script Frontend recognizes a standalone client-side `GO` separator
even with trailing `-- comment`, inline `/* comment */`, or a leading
same-line block comment. This preserves source spans and ordered batch indices.
Only whitespace and fully closed same-line SQL comments may surround `GO`;
a T-SQL statement on the same line is **not** a separator. Physical lines
follow the SQL Lexer contract: **LF, CRLF, and CR-only** scripts preserve
identical batch order and absolute source spans. `BatchCount` counts only
**nonempty, analyzable batches**, while statement `BatchIndex` is a dense,
zero-based index into those batches; leading, adjacent and trailing `GO` lines
never create phantom batches or gaps. A script containing only separators,
whitespace and comments reports zero batches. Incremental GO-line scanning
avoids quadratic repeated line searches and charges scanning to the existing
ScriptDispatch resource budget. `GO 2` repetition
remains outside the modeled subset, and none of the other five dialects
inherits this SQL Server-specific behavior. Parsing never sends `GO` to a
database or confers compiler authorization.
SQL Server Script dispatch also recovers at a semicolon after an **extra
closing parenthesis** (for example `SELECT 1); SELECT 2`). The first
statement remains `Invalid` with the strict source-parser diagnostic; the
next statement retains its own span and syntax status. This is error
isolation for static inspection, **not** acceptance of malformed SQL.
Unclosed opening parentheses are not recovered at semicolons, opaque
procedure bodies remain intact, and `GO` batch behavior is unchanged.

## Cooperative cancellation for static SQL scripts

`ParseScript` and `AnalyzeScript` have `CancellationToken` overloads so an
ASP.NET host or other .NET caller can abandon an expensive analysis without
waiting for the resource deadline. The previous overloads remain available.

```csharp
using System.Threading;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;

using var cts = new CancellationTokenSource();
var syntax = SqlCoreFacade.ParseScript(
    "CREATE TABLE demo (id INT); SELECT id FROM demo;",
    SqlAgentToolType.Postgres, cts.Token);

var analysis = SqlCoreFacade.AnalyzeScript(
    "CREATE TABLE demo (id INT); SELECT id FROM demo;",
    SqlAgentToolType.Postgres, SqlCatalogState.KnownEmpty(), cts.Token);
```

Cancellation is **cooperative**: the token is checked at compiler work checkpoints,
stage boundaries, and before returning. Cancellation throws
`OperationCanceledException` rather than returning a partial result marked
`Analyzed`. The scope is restored even when analysis fails, and any nested
analysis inherits its caller's active cancellation. This is strictly static
SQL parsing/analysis; neither method opens a database connection or executes SQL.

## Script-wide effect coverage and Catalog change summary

`SqlScriptAnalysisResult.EffectCoverage` is a conservative, read-only
aggregation of the existing per-statement `EffectCoverage` and analysis status:

- `Complete` means every statement's **static effect description** is complete
  (also true for an empty script); it does **not** prove server execution,
  effective permissions or success of future writes.
- `Partial` means at least one statement was only partially modeled, and
  none is invalid, unsupported or of unknown effect coverage.
- `Unknown` means any statement is invalid, unsupported or has unknown
  effects. Other fully-modeled statements cannot upgrade that uncertainty.

`SqlScriptAnalysisResult.CatalogChanges` is a read-only, source-order
projection of `Statements[*].Changes`, preserving intermediate changes and
source spans (including tentative/unknown effects). It is **not** a
transaction-commit log or a replacement for `FinalCatalog`.

```csharp
var report = SqlCoreFacade.AnalyzeScript(sql, SqlAgentToolType.Postgres,
    SqlCatalogState.KnownEmpty());
Console.WriteLine(report.EffectCoverage);
foreach (var delta in report.CatalogChanges)
    Console.WriteLine($"{delta.Operation}: {delta.TableName}");
```

## MySQL implicit DDL COMMIT outside a modeled transaction

MySQL ordinary non-temporary DDL can implicitly COMMIT a pending session
transaction even if the Script does not contain `START TRANSACTION`
(for example, if the session disabled autocommit beforehand).
A static `Outside` transaction context is **not** evidence that
autocommit is enabled. Supported MySQL DDL therefore emits
`ChangeSchema | ChangeTransaction` and
`TransactionProvenance.Origin = PossibleImplicitCommit`,
`RollbackScope = Unproven`, with `RuntimeCertainty = Unknown`.
This is a potential effect, **not proof of executed SQL or DML**.

DDL inside an explicit modeled MySQL transaction stays
`Unsupported` with `SQL_TRANSACTION_IMPLICIT_COMMIT_UNPROVEN`.
Temporary table statements are not promoted to ordinary DDL proof,
because the database's commit/rollback rules differ. Static inspection
does not prove the session's autocommit mode or storage engine.

## PostgreSQL ALTER TABLE RENAME [COLUMN] — typed Catalog lineage

PostgreSQL `ALTER TABLE relation RENAME [COLUMN] old TO new` now uses the same
closed F# `RenameColumn` operation as Oracle, with PostgreSQL-specific grammar.
The static analyzer preserves column order, SQL type and nullability, and
atomically rebinds standalone index columns and modeled local primary, unique
and foreign-key column references. Historic immutable Catalog snapshots remain
unchanged. The resulting `RenameColumn` Catalog change and statement effect
describe **static schema evolution only**; the package neither connects to a
database nor executes the DDL.

Proof requires a closed, known Catalog and a proven target table. An absent or
duplicate column is invalid. PostgreSQL **inbound and self-referencing FOREIGN
KEY** constraints now follow a column rename through exact referenced-table
identity and ordered physical-column bindings. All affected tables, including
several child relations and composite FKs, are projected from the SAME
pre-rename immutable snapshot and published as one Catalog state. Child-local
columns, foreign-key names, unaffected constraints and historical snapshots
are preserved; downstream DROP COLUMN checks see the updated relationship.
This is static schema analysis, not a command to execute DDL or a proof that
server-side locks/privileges are available.

PostgreSQL **View dependencies** now follow renamed physical source
columns with the same atomic state transition as table indexes and inbound
FKs. The Catalog retargets only direct identity-qualified
`SqlCatalogViewSourceBinding.Columns`, rebuilding the legacy flattened
`SourceColumns` consistently; existing `View.Columns` output headers,
aliases, types and nullability do **not** change. That distinction is
critical for `SELECT *`, projected aliases and multi-hop Views:
downstream Views continue to reference stable upstream output names.
JOIN/WHERE-only source dependencies retain their exact owner, and
subsequent DROP COLUMN proof sees the newly named physical column.
Historical snapshots and unaffected View instances remain untouched.
This is metadata analysis, not a rewrite or execution of physical SQL.

Unknown/open-world states cannot mint known changes. Oracle now has its
own proven inbound-FK rename lineage, but Oracle dependent-View rename
remains fail-closed pending separate proof.
Compound ALTER clauses, table rename, unmodeled objects, unsupported
annotations and dialects without their own proof remain outside this subset.
PostgreSQL and Oracle share a typed internal operation, **not** blanket dialect
equivalence. Regression coverage includes 24 general real PostgreSQL DDL
scripts with index-column lineage, four real PostgreSQL FK workflows comparing
ordered `pg_constraint.conkey/confkey` bindings, and five additional real
PostgreSQL View workflows. The View oracle examines
`pg_rewrite → pg_depend → pg_attribute` and `information_schema.columns`
after **every** DDL statement: direct, aliased, chained, JOIN/WHERE,
FK+View and wildcard dependencies.

## MySQL ALTER FOREIGN KEY — incremental dependency graph and row-proof boundary

`ALTER TABLE ... DROP FOREIGN KEY fk_symbol` and the existing proven
multi-symbol DROP grammar update the immutable Virtual Catalog in statement
order. The bounded **single-statement atomic** form
`ALTER TABLE child DROP FOREIGN KEY fk_old, ADD INDEX ix_new(cols)`
is also recognized, including multiple preceding named FK drops and
one final named non-UNIQUE `ADD KEY|INDEX` clause. Both actions are
validated against the same original known table, with the index checked
against the *resulting* FK dependency graph and possible residual
physical names before **any** Catalog effect is published. It produces
one immutable Catalog transition with ordered `DropConstraint` and
`CreateIndex` evidence, never a speculative partial-success state.
Reusing a possibly surviving FK-index name, duplicate FK symbols,
missing indexed columns, mixed clause order, multiple new indexes and
mixed DROP COLUMN are rejected unless a separate atomic proof exists.

Every subsequent `CREATE INDEX`, `ALTER ADD INDEX|KEY` or
`DROP INDEX` recomputes FK left-prefix connected components from the
**remaining** named constraints, not from a stale CREATE TABLE graph.
Dropping a shorter or longer member can split or shrink a shared-prefix
group, allowing new independently named support indexes or a proven
backup-before-DROP sequence. Previously removed FK symbols and their
possibly surviving implicit physical index names stay in the bounded
residual-name lineage: DROP FK never proves DROP INDEX. Name reuse is
denied when an implicit index may still occupy that name, even when no
FK constraint by that name remains in the Catalog. All mutations stay
table-local and preserve immutable before/after snapshots.

MySQL 8 `ALTER TABLE ... ADD [CONSTRAINT symbol] FOREIGN KEY (columns)
REFERENCES parent(columns)` now has a dedicated **typed DDL syntax path**.
It binds the table, local columns, parent identity and referenced
columns as one `AddConstraint` value; it is no longer conflated with
unknown/opaque ALTER SQL. Invalid duplicate symbols and invalid known
bindings are checked semantically, whereas an otherwise structurally
valid addition **must still be rejected as unproven** because the
Catalog does not know the existing child rows. Even a CREATE TABLE
earlier in one analyzed script does not prove the table remains empty
under concurrent writes and MySQL's implicit DDL commits. Thus no
`Known` constraint delta is issued for ALTER ADD FK, no physical
auto-index name is guessed, and the subsequent Catalog is tainted
if the unsupported data-dependent DDL might have run.

No implicit name, `ON DELETE`/`ON UPDATE` action, composite ADD/DROP,
or index-name override is upgraded without separate proofs. Other five
dialects retain their existing grammar and safety boundaries. CI runs
positive and negative ordered grammar checks and independent real
MySQL 8 InnoDB differential verification of the exact physical
`information_schema.STATISTICS` indexes and
`REFERENTIAL_CONSTRAINTS` after every modeled DROP transition.
A separate empty-table live ADD confirms that MySQL may accept the
statement while static analysis must remain fail-closed without row
evidence. None of these analyses executes or authorizes production SQL.

## MySQL typed shared-FK left-prefix dependency graph

Named foreign keys are **logical obligations**, not guarantees of separately
existing InnoDB physical support indexes. Two or more child-side FK column
lists can belong to one support group if either is an ordered left prefix
of the other. SqlCore now builds a closed F# dependency graph whose connected
components distinguish a single independent FK from a shared-prefix group.
The graph is calculated from known immutable Catalog constraints, and
**does not** pretend every FK name occupies a separate physical index.

For a shared group, a new secondary index is proved only if it is entirely
unrelated to the group's prefixes, or its ordered columns cover **all**
FKs in the impacted group. A partial group-covering index is rejected
unless a distinct, previously proven explicit index already supports
every member of the group; in that case the partial new index cannot
replace the proven user-created support index. An index covering two
independent groups at once, if representable, is rejected rather than
inventing a multi-group physical replacement.

A full shared-group replacement publishes one known explicit
`CreateIndex` delta and retains the **possible** names of *all* impacted
implicit FK-support indexes. These names are conservative collision
candidates, not assertions of physical presence. Reusing an implicit
FK symbol as the name of a new covering index remains rejected for a
shared group: unlike one independent FK, MySQL may already have reused
or silently replaced the underlying physical key. A separately named
backup covering all group members can be added, then the old explicit
covering index safely dropped. A backup covering only the shorter FK
is not sufficient to remove the last explicit supporter of the longer
FK; auto indexes cannot be counted as proof.

This proof is limited to complete known MySQL catalogs, explicitly named
simple ASCII foreign keys, proven server identifier semantics, bounded
physical index names and table-local ordered prefixes. Unknown FK
names or physical history, partial first-replacement groups, schema
ambiguity and all other five SQL dialects stay fail-closed. The
dependency graph cannot connect to databases or authorize/execute SQL.
Disposable live InnoDB tests check full physical `STATISTICS` index
column order and `REFERENTIAL_CONSTRAINTS` after each DDL, including
two shared FKs, an independent third FK, and full-backup safe DROP.

## MySQL FK alternative-index proof and bounded residual-name lineage

The Virtual Catalog now retains **the exact set of possibly surviving
implicit InnoDB FK-supporting index names**, with immutable per-table
history, rather than using only a blanket Boolean. This set is
*possibly present*, **not** a claim that those physical indexes exist.
The uncertain identity is preserved when a named FK is removed, when
its implicit support index may be replaced by a covering secondary
index, and through further column/index edits or schema-binding
aliases. An unknown/unnamed implicit index is represented by an
unprovable name and retains the fail-closed fence.

The bounded MySQL proof checks that each new index's **physical
case-insensitive name** is distinct from every possibly surviving
implicit FK-support name, all primary/unique indexes and all known
standalone indexes. It requires complete known Catalog, explicit
provider identifier semantics and fully proven physical names.
In MySQL `LOWER_CASE_TABLE_NAMES=0`, tables/columns remain in the
strict lowercase identifier subset, but INDEX names may use any bounded
simple ASCII lettercase: MySQL index names are case-insensitive on all
platforms. Catalog rejects two physical index names equal after
ASCII case folding rather than forcing index names to lowercase.
If a previously proven explicit index already supports the FK,
adding a second distinct explicit left-prefix supporting index
cannot remove that user-declared index. Catalog retains only the
known explicit additions and the original named FK constraint;
no imaginary DROP INDEX effect is emitted.

`DROP INDEX` is proven only when removing that tracked explicit
index leaves a **different modeled physical PK, UNIQUE, or explicit
index** whose ordered first columns cover *every* FK that depends
on the removed index. A possibly retained InnoDB auto-index
is **never** counted as an alternative, even if its name is known.
The check is table-local, independent of current index-name lettercase
and does not silently weaken the other five dialects.
The final supporting explicit index continues to fail-closed.
Following DROP FOREIGN KEY, the old implicit physical name remains
possibly occupied; a different proven name can be added without
pretending that name was deleted.

Real MySQL 8 InnoDB differential tests independently verify the
3-step cover → backup → safe DROP workflow, including same-name
promotion, ordered physical keys from `information_schema.STATISTICS`,
FK survival from `REFERENTIAL_CONSTRAINTS`, and the rejection of
unsafe final-support DROP when the original implicit index disappeared.
Static SqlCore still never executes SQL or grants permissions.

## MySQL same-name FK implicit-index promotion and independent multi-FK ownership

An InnoDB implicit foreign-key support index may be promoted by declaring an
explicit left-prefix covering index **with the very same name**. SqlCore
proves that transition only if the full known closed Catalog, declared MySQL
physical identifier contract, the named FK's exact ordered local prefix,
and absence of competing PK/UNIQUE/secondary support indexes are all proven.
A same-name index that does **not** cover its own FK remains invalid; names
matching a different FK, PRIMARY, UNIQUE, or ordinary index are collisions.
The transition publishes one proven explicit `CreateIndex` Catalog change
without asserting that an implicit physical index was removed, and retains
the persistent uncertain-physical-lineage marker. A later
`DROP INDEX` that might remove the last proven FK support remains
fail-closed; distinct names and independently proven replacement
support may now be analyzed using the bounded residual-name evidence.

Multiple named FKs are independently owned only if their ordered
local prefixes do not overlap. Prefix-related FK obligations now form
typed **shared support groups** as documented above. An unrelated
new index is stable, and a full-group-covering index may replace
possible implicit supporting indexes without asserting how many existed.
Unnamed FKs, opaque server identifier settings, unknown residual
physical names, and all other five database dialects remain
conservatively rejected. A prior replacement with known physical
names does not, by itself, prevent subsequent distinct-name indexes.

Static analysis does not connect to MySQL, authorize a change or execute SQL.
Test-only disposable InnoDB databases compare real physical key columns and
FK metadata from `information_schema.STATISTICS` /
`REFERENTIAL_CONSTRAINTS` across both same-name and independent multiple-FK
workflows. No guessed physical DROP INDEX deltas are minted.

## MySQL FK prefix-covering index — typed uncertain physical replacement

InnoDB allows an explicitly named secondary index whose ordered **left prefix**
matches a single proven FK's local columns. This secondary index can **replace**
the implicit FK-supporting index, but MySQL does not guarantee that the old
physical index is always dropped. The F# SQL analyzer therefore returns a
closed `MySqlPhysicalIndexTransition`: either
`StableImplicitFkSupport` or `MayReplaceImplicitFkSupport`.
The latter proves only the new standalone index and unchanged FK constraint.
It does **not** mint a physical DROP INDEX delta or assert that the implicit
index survived or disappeared.

A possible replacement sets an immutable table-local unknown-physical-history
fence, now refined with a bounded set of names that **may still be
occupied**. Even after removing the FK, attempting to reuse one of those
physical names remains fail-closed, while independently proven distinct
names are allowed. Dropping an index that is potentially the **last**
FK-supporting index is fail-closed (ER_DROP_INDEX_FK, 1553), unless an
independent tracked PK/UNIQUE/explicit index supports every affected FK.
The guarded proof requires complete known Catalog, explicit MySQL
identifier semantics, no opaque physical-name collisions and an ordered
left-prefix supporting index. The later same-name promotion and independent
multi-FK and shared-group extensions are documented above; partial
shared-group replacement without known explicit coverage, unknown
physical index origins, and other dialects remain rejected.

Independent MySQL 8 InnoDB tests verify that the new index's ordered
members and the original FK constraint remain valid; the old implicit
index's presence is observed, not guessed. Same-name replacement is now
accepted only under its typed provenance proof, not by inference from the
SQL spelling alone. No production SQL execution or authorization is introduced.

## MySQL named FK supporting-index stability — original bounded foundation

The first InnoDB index namespace proof was deliberately limited to a
single explicitly named FK and **disjoint** newly declared secondary
indexes. That conservative foundation proved that a newly created index
could not silently replace the FK-supporting auto-index and that the
proposed physical name was distinct from PRIMARY, named UNIQUE, known
standalone indexes, and the possibly present implicit FK-support name.
The MySQL name comparisons are bounded ASCII and case-insensitive.

The later typed FK-prefix and shared-support-group proofs documented
**above** extend this original subset. A full-group covering index may
now be admitted without making a physical-index existence claim; a
separately proven alternative can then justify dropping the old
explicit key. Partial shared-group replacement, unknown generated
physical names, reuse of ambiguous implicit shared-FK names, and
dropping the last proven support remain fail-closed.

The immutable Catalog preserves potentially surviving implicit names
across covering replacements and `DROP FOREIGN KEY`, rather than
mistaking constraint removal for physical index removal. Disposable
MySQL InnoDB parity tests compare real `information_schema.STATISTICS`
and FK metadata after each supported DDL transition. MySQL DDL can
implicitly commit. Static analysis does not execute SQL, grant
permissions, or prove any live server setting.

## MySQL PRIMARY/UNIQUE and explicit secondary index namespace

MySQL physical indexes have a **table-local** index name namespace whether
an index was created explicitly or belongs to a key constraint. Both
`CREATE INDEX` and `ALTER TABLE ADD INDEX|KEY` now share one closed F#
physical-name model: `PrimaryIndex` is always the MySQL physical
`PRIMARY` index, `NamedUniqueIndex` belongs only to a UNIQUE constraint
with an explicitly proven name, and `StandaloneIndex` is a tracked
non-unique physical index. The latter remains the only kind stored in
`SqlCatalogTable.Indexes`; key-backed names are derived from typed
constraint evidence and not fabricated as new Catalog indexes.

For table-local key namespaces, matching uses the provider's
case-insensitive **simple ASCII** identity; collisions against PRIMARY,
named UNIQUE, and existing standalone indexes are rejected. The known,
closed Catalog must have a declared MySQL identifier premise (the
existing Linux `LOWER_CASE_TABLE_NAMES=0` lowercase subset or the
`LOWER_CASE_TABLE_NAMES=1` FoldAscii subset). Unknown server settings,
non-ASCII index identity, unmodeled FK-support indexes, and unnamed
UNIQUE indexes with generated server index names remain fail-closed.
InnoDB can silently create or replace an implicit FK-supporting index
when a new matching index is created, so this change intentionally
**does not** assume a FOREIGN KEY symbol is a physical index name.

The MySQL proof is not inherited by the other five providers. Catalog
history remains immutable, new indexes still require validated real
columns, and static analysis does not authorize or execute SQL.
Independent MySQL 8 InnoDB tests compare names, ordered key columns
and `NON_UNIQUE` flags from `information_schema.STATISTICS` after
every statement in four isolated databases and check real-name
collision failures.

## MySQL standalone DROP PRIMARY KEY — known Catalog only

MySQL 8's `ALTER TABLE owner DROP PRIMARY KEY` is a closed F#
`DropPrimaryKey` operation, separate from `DROP CONSTRAINT` and `DROP
FOREIGN KEY`. It removes precisely one modeled PRIMARY constraint only after
the entire Catalog and target physical identifiers are proven. The Catalog
change records the actual MySQL server key/index identity `PRIMARY`,
regardless of an optional user-written PRIMARY constraint label. Existing
UNIQUE constraints, standalone indexes, table columns, outgoing foreign keys,
unrelated relations, and immutable earlier Catalog snapshots remain unchanged.

Incoming or self-referencing foreign keys on PRIMARY columns block deletion.
Absent PK on a known table is invalid; open-world, unproven casing, unknown
dependencies or other unmodeled keys remain fail-closed. Only standalone
`DROP PRIMARY KEY` is accepted: compound ALTER, `IF EXISTS`, CASCADE,
other SQL dialects, and implicit/automatic index changes are not promoted.
The existing explicit lowercase Linux `LOWER_CASE_TABLE_NAMES=0` static
identifier proof is reused, never inferred from the running OS/server.
Static MySQL implicit-COMMIT effects remain conservative. SqlCore itself
does not connect to or execute any database; the test harness performs
four isolated real MySQL InnoDB scripts and compares ordered primary key
columns, physical PRIMARY index members and column inventories after each
step, plus a real referencing-FK refusal.

## MySQL atomic FOREIGN KEY drop and lowercase Linux identity proof

MySQL 8 native `ALTER TABLE child DROP FOREIGN KEY fk_symbol` is now modeled
using one closed F# `DropForeignKeys(table, symbols, columns)` operation.
The statically proven subset supports one or multiple explicitly named
foreign-key removals, followed by zero or more `DROP [COLUMN] column`
clauses in the **same MySQL ALTER TABLE statement**. All names, FK kinds,
column dependencies, duplicate targets and retained-column cardinality
are validated against a complete **pre-statement** immutable Catalog. Only
after every action passes may SqlCore publish a single resulting Catalog
and per-FK exact `DropConstraint` / per-column exact `DropColumn`
changes. An invalid later clause cannot partially drop an earlier FK.
Foreign keys in other tables, primary/unique constraints, physical
references and earlier Catalog snapshots remain untouched. FK drops
unlock dependent column operations only after their original references
have been eliminated. MySQL DDL still discloses **possible implicit
COMMIT** and never pretends to be rollbackable.

MySQL's table-name casing varies by `lower_case_table_names`. The
existing explicit `1` profile continues its ASCII-fold contract; the
new **explicit `LOWER_CASE_TABLE_NAMES=0` Linux proof** permits only
simple lowercase ASCII table, column, constraint, index and reference
identifiers in the entire known Catalog. That narrow subset avoids
assuming MySQL's case-sensitive table names and case-insensitive column
names have the same unrestricted equality rules. No provider mode is
inferred from the client OS or an unqueried server; callers must supply
the correct profile, and an unknown/mixed-case scope stays fail-closed.
MySQL FK symbols must also be unique across the known schema.

MySQL native `DROP CONSTRAINT`, `DROP PRIMARY KEY`, implicit FK-support
index mutations, mixed `ADD` clauses, `ALGORITHM` hints, `IF EXISTS`,
unknown FK names and other dialect variants are **not** silently promoted.
The feature adds static evidence only; the test harness (not SqlCore)
executes six isolated real MySQL 8 InnoDB DDL scripts and checks ordered
`information_schema.KEY_COLUMN_USAGE` and physical column presence
after **every** statement. Tests use a separate database for each case
because MySQL DDL implicitly commits, with explicit database cleanup.

## PostgreSQL and Oracle RENAME CONSTRAINT — exact Catalog identity

`ALTER TABLE relation RENAME CONSTRAINT old_name TO new_name` is a new
**closed F# `RenameConstraint` DDL operation** for PostgreSQL and Oracle.
A complete known virtual Catalog is mandatory; the analyzer changes only
one explicitly named PRIMARY KEY, UNIQUE or FOREIGN KEY constraint on
the proven owner relation, preserving its kind, ordered local/remote
physical columns and referenced relation identity. A single exact
`SqlCatalogOperation.RenameConstraint` delta records the new constraint
identity. Historical snapshots, children and unrelated constraints
remain unchanged. Repeated renames and later `DROP CONSTRAINT` use the
new name, without fabricating SQL execution or authorizing DDL.

The two databases have deliberately separate namespace contracts:

- **PostgreSQL** permits separate child tables to use the same FK name.
  Renaming PK/UNIQUE also renames the underlying physical index. Before
  proving a rename of either indexed key, SqlCore checks the physical
  relation namespace for matching table, View, standalone index and
  implicit PK/UNIQUE index names across possible schema owners. Unnamed
  key-backed indexes or unproven schema scope fail closed rather than
  allowing an unverified index name collision.
- **Oracle** requires each constraint name to be unique in its schema,
  even across different tables. SqlCore conservatively checks all known
  table constraints; remote FK physical bindings remain valid and are
  not renamed with the parent key. Oracle implicit constraint indexes
  are not misrepresented as standalone Catalog indexes.

Unknown/open-world catalogs, absent or duplicate names, same-name
renames, unsupported composite clauses and all other four dialects
remain fail-closed. This is metadata analysis only. Five live PostgreSQL
DDL scenarios check `pg_constraint`, `conkey/confkey` and the
`conindid → pg_class` physical implicit index name after each DDL,
while four live Oracle Free scenarios check `USER_CONSTRAINTS`,
`USER_CONS_COLUMNS` and `R_CONSTRAINT_NAME` with explicit cleanup
for Oracle's implicitly committed DDL.

## PostgreSQL DROP CONSTRAINT — typed dependency-mode proof

PostgreSQL `ALTER TABLE owner DROP CONSTRAINT [IF EXISTS] name [RESTRICT |
CASCADE]` now records the conditional and dependency mode in one closed F#
`DropConstraint` operation. Ordinary `DROP CONSTRAINT` and explicit
`RESTRICT` continue to reject known inbound foreign keys. `IF EXISTS`
can produce an exact, zero-effect no-op only when the target table and
entire Catalog are known; it never hides a missing table.

`CASCADE` first proves the named parent PRIMARY KEY/UNIQUE constraint and
matches incoming FOREIGN KEY dependencies by **exact referenced relation
identity and ordered key columns**. It atomically removes the chosen
constraint together with precisely its dependent child/self FKs and
publishes a separate exact `DropConstraint` delta/effect for each object.
Unrelated keys, local columns, Views, standalone indexes and immutable
previous snapshots survive. Dropping an FK itself only removes the local
FK. Subsequent `DROP COLUMN` sees the updated graph. If two parent
keys share identical ordered columns and the Catalog cannot prove which
unique index the FK actually references, `CASCADE` stays fail-closed.

No SQL or DDL is executed by SqlCore; the feature is **static schema
analysis**, not an authorization grant. Missing/unknown Catalog facts,
unmodeled dependencies, ambiguous key identity, compound ALTER forms
and PostgreSQL syntax accidentally inherited by another dialect are
never promoted to known evidence. Five independent live PostgreSQL
workflows compare `pg_constraint`, ordered physical `conkey/confkey`
bindings and referenced table identities after **every** statement,
in rollback-only isolated schemas.

## PostgreSQL Self JOIN with per-instance Catalog provenance

PostgreSQL `CREATE VIEW` now distinguishes **FROM relation instances**
from persistent physical relation identities. The same known table may
appear under multiple unique effective aliases within a bounded
2–4-source left-deep JOIN graph. `JOIN ON` is proved by **source instance
index**, so `employees e JOIN employees m ON e.manager_id = m.id`
binds two different sides even though they share the same physical table.
The existing typed AND/OR, NULL-safe and relational ON operator fences
remain in force.

Each stage carries its ordered visible columns, physical lineage and
**instance-specific null-extension** across INNER/LEFT/RIGHT/FULL JOIN.
The `e.*` and `m.*` projections use the correct instance; unqualified
ambiguous names still fail closed. `USING` merges the stage-visible
key while preserving both original physical key references, even when
they refer to the same table. Only proven columns are published to
the View shape.

Persistent `SqlCatalogViewSourceBinding` entries are deduplicated by
**physical relation key**, not by FROM alias. DROP COLUMN therefore
protects a column referenced from either instance, without adding
synthetic tables or duplicate physical owners to the Catalog. Compatible
CREATE OR REPLACE VIEW rebinds dependencies without breaking child
Views. Duplicate aliases, unsupported predicates, unknown source
types, more than four instances and cross-dialect inheritance still
fail closed. PostgreSQL live parity covers unmatched self-join rows,
source-column nullability and actual DROP COLUMN restrictions.
No SQL execution or authorization permission is added.

## PostgreSQL bounded multi-source View JOIN graph and compound ON proof

The static PostgreSQL-only Virtual Catalog now proves a **left-deep graph
of up to four alias-bound relation instances** using the four closed JOIN
families (`INNER`, `LEFT`, `RIGHT`, `FULL`) and a bounded conjunction
of **1–8 direct column equality pairs per ON**. The F# `ViewJoinEdge`
contains typed `ViewJoinKind` and column-reference pairs. Its ordered
position pairs each edge with the newly bound source: a valid ON equality
must connect that source to **an already bound left-subtree source**.
Forward aliases, two prior-only operands, two new-only operands,
unresolved/ambiguous columns, incompatible SQL types, duplicate aliases
and unproven instance bindings cannot publish a Known Catalog change.

Outer-join nullability is folded from left to right over the entire
bound source graph. A LEFT edge null-extends the new input. A RIGHT edge
null-extends **every** relation in the accumulated left subtree. A FULL
edge null-extends both that subtree and the new right relation. Previous
nullable proofs are kept conservative across later INNER predicates;
no unproven WHERE simplification erases nullability. Named projections,
`source.*`, View headers and dependent Views preserve the resulting
typed column shape.

Physical SELECT, ON and WHERE-only source-column dependencies remain
identity-owned via `SourceBindings`; DROP COLUMN and recursive
RESTRICT/CASCADE correctly cover every source. CREATE OR REPLACE VIEW
recomputes all source bindings atomically, detects dependency cycles,
preserves existing name/order/type prefixes, and refuses nullability
changes that would leave descendant View proofs stale.

Resource bounds prevent unbounded parsing/semantic expansion. Five
other SQL dialects retain their prior proof fences. NATURAL, CROSS, unproven USING shapes, arbitrary ON expressions,
subqueries, more than four sources or eight typed ON comparison leaves,
and unproven self-join shapes remain fail-closed.

### PostgreSQL three/four-source left-deep USING View graph proof

The PostgreSQL-only Virtual Catalog now proves **up to four distinct known
relations** in a bounded left-deep JOIN graph containing typed `USING`
edges, including INNER/LEFT/RIGHT/FULL and mixed `ON`/USING edges.
`ON` equality operands may be **physically qualified** or **uniquely
stage-visible unqualified columns**. PostgreSQL resolves an unqualified
name across the prior JOIN result and the newly introduced source;
exactly one matching column is required. A prior `USING` merged key
counts as **one** visible value even when its lineage includes several
physical tables, including FULL JOIN's coalesced key. Every stage owns a closed
ordered visible-column model: a USING edge consumes the prior stage's
**single merged key**, never an ambiguous raw scan over the original
tables. Each edge projects USING keys in clause order, then left
nonkeys, then newly joined right nonkeys.

A stage-visible column carries its SQL type/nullability, one projected
physical owner, and a complete **physical dependency lineage**. RIGHT
takes the new right key, FULL merges prior visible key and right key
with conservative coalesced nullability. Both sides of every JOIN key,
including all earlier USING stages, remain protected from DROP COLUMN.
Qualified physical projections keep per-source null-extension; named
unqualified projections and WHERE bind only one final visible column.
Compatible View replacement rebinds physical dependencies atomically.

Ambiguous keys in a prior ON subtree, unqualified ON operands present
on both sides (or duplicated within a side), unproven ON comparisons,
future aliases, duplicate or type-incompatible keys, unproven self-join shapes, more
than four sources, unknown/open catalogs and other database dialects
remain fail-closed. Unqualified and qualified ON references have the
same strict SQL type and physical dependency proof. No execution/authorization capabilities are added.

### PostgreSQL two-source OUTER USING merged-key proof

In addition to INNER, PostgreSQL `CREATE VIEW` now statically proves
`LEFT [OUTER] JOIN`, `RIGHT [OUTER] JOIN` and `FULL [OUTER] JOIN`
with 1–8 identical-type, unique `USING` keys on two distinct known
sources. The typed `ViewJoinKind` controls the merged key's **value
and nullability**, not the nullable state of an arbitrarily chosen
physical source:

- LEFT USING keeps the left key's value and base nullability.
- RIGHT USING keeps the right key's value and base nullability.
- FULL USING uses PostgreSQL's `COALESCE(left_key,right_key)` value.
  Its key is conservatively nullable if **either** source key can be
  nullable, and nonnullable if both sources prove NOT NULL.

The unqualified merged name follows this choice for named columns
and `SELECT *`, whereas qualified `a.key` and `b.key` retain their
own null-extension under LEFT/RIGHT/FULL. All `USING` key dependencies
still belong to **both** physical sources even when the merged output
is represented by one column. PostgreSQL live parity verifies matched
rows, LEFT-only, RIGHT-only, NULL-key unmatched rows, ordinal field
names and DROP COLUMN restrictions. Cross-dialect, unproven self-join and multiway
USING, implicit coercion, NATURAL and opaque predicates remain
fail-closed; this grants no SQL execution permission.

### PostgreSQL two-source INNER USING static View proof

PostgreSQL `CREATE VIEW ... FROM a JOIN b USING (id, tenant)` now has
a dedicated typed `ViewJoinCriteria.UsingKeys` case, distinct from the
existing `OnEquality` expression pairs. For a **known, two-relation JOIN**, 1–8 unique, same-named,
identical-type keys are bound to both physical source identities.
INNER USING resolves the unqualified merged key to the first input's
matching value, while OUTER USING applies the distinct JOIN-kind rules
described above. **Both** source-column identities remain dependencies
for DROP COLUMN / DROP TABLE and View replacement.

Explicit named columns and qualified `source.*` preserve physical
ownership; unqualified non-USING duplicates remain ambiguous. The
unqualified `SELECT *` expansion follows PostgreSQL's merged projection:
**USING keys in the exact order declared**, then remaining **left columns**
in their physical order, then remaining **right columns** in their
physical order. Each shared key is exposed only once using the proven
JOIN-kind-specific SQL type/nullability, but both physical key dependencies
remain protected. Non-USING duplicate output names are rejected for
`CREATE VIEW`; optional View output-header renaming and compatible
`CREATE OR REPLACE VIEW` retain ordinal shape and owned source columns.

Multiway USING, NATURAL joins, type coercion and non-proven
expressions remain fail-closed. PostgreSQL live parity validates output
column order/types, returned rows and key DROP guards. This is static
Catalog analysis only, not SQL execution or permission authority.

### PostgreSQL bounded same-side JOIN ON predicates

A PostgreSQL `CREATE VIEW JOIN ON` edge may contain **only typed same-side
column comparisons**, or mix local and cross-edge predicates,
including nested `AND`/`OR`, `IS [NOT] DISTINCT FROM` and relational
operators. For example:
`ON a.id = b.id AND a.tenant = a.region AND b.score > b.minimum`.
Every comparison leaf must resolve to two columns visible at that
stage, and must pass the existing SQL type/operator capability proof.
All physical operand dependencies are retained, including branches
of OR and coalesced stage-visible USING keys.

Local conditions **stay in JOIN ON**. In particular, moving a LEFT
JOIN's local condition into WHERE would discard unmatched left rows
and change SQL semantics. The same contracts apply to INNER, LEFT,
RIGHT, multi-source USING joins and alias-scoped Self JOIN. FULL JOIN
additionally requires a mandatory cross-source equality usable as a
merge/hash join key; pure-local or disjunctive-only FULL ON is not
a plannable PostgreSQL join and remains fail-closed.
No cross-edge comparison is required for INNER/LEFT/RIGHT: a completely
local ON predicate is **proven** when all its column operands,
SQL operators and JOIN-stage dependencies pass the same typed contract.
A bounded edge still requires at least one comparison leaf. Its
condition is never rewritten to WHERE, and may match **multiple** right
rows for one left row. Future aliases, ambiguous names, unsupported
types/operators, constants, unbounded grammar, and non-PostgreSQL
source dialects remain fail-closed.
Static analysis does not execute SQL or grant permission.

### PostgreSQL typed relational JOIN ON operators

PostgreSQL `CREATE VIEW` joins now prove a closed F#
`ViewOnComparisonOperator` with `=`, `<>` / `!=`, `<`,
`<=`, `>`, `>=`, `IS NOT DISTINCT FROM` and
`IS DISTINCT FROM`. The latter is **NULL-safe inequality**:
NULL versus a non-NULL value is distinct, but two NULLs are not.
Ordinary `<>` instead yields UNKNOWN if either operand is NULL.
The operator and the AND/OR expression tree are preserved, never
silently rewritten into equality.

Every comparison must link the prior JOIN subtree to the newly
introduced source, with equal proven catalog SQL types and all
physical source-column dependencies tracked. New non-equality
operators require explicitly proven PostgreSQL scalar families;
ordered comparisons are restricted to known numeric, TEXT/CHAR/
VARCHAR and DATE types. Unknown/custom types, implicit coercion,
expressions, literal operands, ninth ON leaf and other dialects
remain fail-closed. This covers 2–4 sources, LEFT/RIGHT/FULL joins,
USING merged-key lineage and safe View replacement. PostgreSQL
live row-parity tests distinguish NULL-safe inequality from `<>`
and ordered comparisons. No SQL execution capability is added.

### PostgreSQL bounded JOIN ON Boolean graph (AND / OR)

The PostgreSQL-only `CREATE VIEW` analyzer now parses a **closed F#
`ViewOnBoolean`** tree with typed `OnCompare` leaves and explicit
`OnAnd` / `OnOr` nodes. Operator precedence matches SQL:
**AND binds tighter than OR**, and parentheses preserve grouping.
Unlike a flat list of join key comparisons, the Boolean tree
distinguishes `a=b OR c=d AND e=f` from
`(a=b OR c=d) AND e=f`. The existing conjunction-only
`OnEquality` path is retained for previously proven views.

Each ON edge permits **1–8** direct same-type column comparisons,
local or cross-edge, with all proven PostgreSQL ON operators; grouping depth
is capped at 16 and parsing nodes at 64. Every Boolean branch is
independently bound with exact source ownership and physical-column
lineage; no branch silently substitutes for another or relaxes
catalog guards. Multi-source `USING`/ON combinations keep the
merged-key lineage and conservative NULL extension. Ordinary SQL
equality still obeys NULL's UNKNOWN comparison outcome, while the
NULL-safe operator matches two NULL values. No SQL expression is
executed during analysis, and no execution permission is created.

Literals, one-sided equality leaves, functions, arbitrary operators,
unsupported casts, missing or future aliases, unproven types, more
than eight leaves, excessive nesting, other database dialects,
and open catalogs remain fail-closed. PostgreSQL live parity
verifies Boolean precedence, grouped outcomes, NULL-safe OR rows,
and physical dependency protection.

### PostgreSQL NULL-safe JOIN ON comparisons

The PostgreSQL-only `CREATE VIEW` JOIN ON analyzer now preserves a
**closed F# `ViewOnComparisonOperator`**: `OrdinaryEquality` for SQL
`=`, and `NullSafeEquality` for SQL `IS NOT DISTINCT FROM`.
These are not interchangeable: PostgreSQL matches two NULL keys under
`IS NOT DISTINCT FROM`, while `NULL = NULL` is UNKNOWN and does not
satisfy INNER JOIN ON. Both variants require two proven direct columns
with identical catalog SQL types and proven JOIN-stage bindings,
which may be local-only, cross-edge or mixed.

An ON edge may mix up to **eight** `=` and `IS NOT DISTINCT FROM`
comparisons using bounded parenthesized AND/OR groups. This works for
INNER/LEFT/RIGHT/FULL JOIN and 3–4-source stage-visible USING graphs.
Unqualified stage-visible merged keys keep all original physical source
column dependencies. Static analysis keeps join/nullability types
conservative and **does not authorize SQL execution**. PostgreSQL live
parity verifies NULL/NULL row behavior versus `=`, as well as both
source DROP COLUMN guards and View replacement dependencies.

Wrong types, literal/arithmetic expressions, unproven coercions,
future aliases, a ninth comparison, unknown catalogs and
non-PostgreSQL dialects remain fail-closed. `IS DISTINCT FROM`
and bounded OR are supported as distinct typed comparison/Boolean
cases, never silently converted into equality or AND.

### Bounded PostgreSQL View ON equality grouping

A View JOIN ON clause may now use parentheses around individual equality
pairs or groups joined by AND, including bounded nesting, such as
`ON ((o.id = l.order_id) AND (o.tenant = l.tenant))`.
Grouping is erased **only after** each equality is parsed into the existing
typed `ViewJoinEdge.Pairs`. Its source identity, type checking, dependency
tracking, and outer-join nullability remain unchanged. Each JOIN edge still
allows only 1–8 column equality pairs; grouping depth is capped at 16 and
work checkpoints bound grouping nodes at 64. OR is modeled by
`ViewOnBoolean`, not flattened to AND. Non-equality comparisons,
unbalanced grouping, arbitrary SQL expressions and other unproven grammar
remain fail-closed. No new executable compiler capability is granted.

Independent PostgreSQL 16 differential tests include real row outcomes
for compound equality AND mixed LEFT/RIGHT, and a four-source
INNER/LEFT/FULL graph. This capability is **structural DDL proof**,
never execution authority.

## PostgreSQL RIGHT and FULL OUTER JOIN View proof

The bounded PostgreSQL-only static View grammar now supports exactly two
distinct relations and one typed, equal-type `ON a.col = b.col` edge for
`RIGHT [OUTER] JOIN` and `FULL [OUTER] JOIN`, alongside the existing INNER
and LEFT families. The closed F# `ViewJoinKind` and
`ViewJoinCondition.RightEquality` / `FullEquality` encode direction
without an unchecked string tag or boolean short-cut.

**RIGHT null-extends the first (left) source**; **FULL null-extends
both**. A projected NOT NULL input is therefore marked nullable on the
corresponding side even when its physical base table is NOT NULL. Named
projections, qualified wildcard expansion, optional View column headers,
and View-of-View shape propagate that conservative output proof. The
physical input's column declarations never change; ON and WHERE dependencies
remain tracked by exact relation identity in `SourceBindings`. WHERE
null checks do not silently weaken the conservative null-extension proof.

`CREATE OR REPLACE VIEW` follows PostgreSQL's names/order/type prefix
contract. If a replacement would change nullable facts needed by any
dependent View, immutable-Catalog analysis refuses the replacement
until descendant reproving is supported; without descendants the
new nullable-side shape is computed atomically.

Unsupported USING/NATURAL joins, non-equality ON expressions, additional
join operands, unproven self-joins, ambiguous aliases/columns and other dialects
remain fail-closed. Independent PostgreSQL 16 differential tests insert
matched and unmatched rows to verify **actual NULL values** for RIGHT and
FULL, rather than inferring nullable contracts solely from
`information_schema.columns`, and verify both source-column dependency
guards and PostgreSQL-compatible View replacement.

## PostgreSQL LEFT JOIN View null-extension proof

The PostgreSQL-only static View grammar accepts exactly two distinct,
known FROM relations joined by `LEFT JOIN` or `LEFT OUTER JOIN` with
one provable equality `ON` condition. The F# closed
`ViewJoinCondition.LeftEquality` distinguishes outer from inner
semantics; no nullable boolean or opaque SQL grants capability.

The right relation is **null-extended** for unmatched left rows. Its
projected columns are modeled `Nullable = true` even if their source
`SqlCatalogColumn` declares NOT NULL. This applies equally to
explicitly named columns, `right.*`, explicit View header overrides,
and downstream Views that project from the View. The left side retains
its already proven input nullability. The original physical Catalog
schema, per-relation `SourceBindings`, ON dependencies and WHERE-only
dependencies remain unchanged and are still checked before DROP COLUMN.

`CREATE OR REPLACE VIEW` must preserve PostgreSQL's output column
names, order and types. A nullable-shape change between INNER and LEFT
is permitted when no descendants would retain stale immutable output
facts. If there are dependent Views, a nullability-changing replacement
is rejected until downstream reproving is implemented. This is a
conservative *static* restriction; PostgreSQL itself can permit the
replacement, which the live parity suite exercises without treating
the parsed View shape as physical NOT NULL DDL metadata.

`WHERE right.col IS NOT NULL` does not automatically override the
conservative null-extended output shape. RIGHT/FULL, USING/NATURAL,
unproven self-join, multi-source, and complex ON expressions
still fail closed in this older two-source subset. Real PostgreSQL 16 tests assert unmatched right-hand
values are NULL (not merely `information_schema.is_nullable`) and
cover replacement and both physical source dependencies.

## PostgreSQL two-source INNER JOIN View proof and owned dependencies

A bounded PostgreSQL-only View DDL grammar now permits precisely two
known physical relations with `JOIN` / `INNER JOIN` and one equality
`ON a.column = b.column`. Aliases are resolved in their local FROM
scope, each ON operand must bind to a **different** source and have
identical Catalog types. A unique unqualified column may resolve
from either input; ambiguous columns, unknown qualifiers, missing
sources, incompatible ON types or duplicate aliases are rejected
without creating Known Catalog facts. Self JOIN instances require distinct aliases and typed instance-level
provenance before any Known View may be published.

The View Catalog retains `Dependencies` and the legacy flattened
`SourceColumns` for compatibility, and now exposes typed immutable
`SourceBindings`: one relation identity plus the exact names read
from that relation, including projection, INNER JOIN key and WHERE-only
columns. **DROP COLUMN checks SourceBindings, not SourceColumns**:
`orders.id` and `lines.id` can no longer be conflated. DROP TABLE
RESTRICT/CASCADE and DROP VIEW transitive dependency closure follow
both physical relations. OR REPLACE atomically rewires both references,
while preserving existing output names, types, order and downstream
View contracts. Repeated single-source projections retain their
previous source-name representation.

Outer joins (with nullable-side semantics), more than two sources,
ON predicates beyond a direct equality, USING/NATURAL, derived
relations, and mixed wildcard projection lists remain outside the
proven subset and fail closed. This is static `AnalyzeScript`
dependency evidence only, not an executable permission. PostgreSQL
16 live differential tests compare real output columns and DROP
dependency behavior.

## PostgreSQL bounded View WHERE predicate proof

PostgreSQL's single-source `CREATE [OR REPLACE] VIEW` static Catalog
grammar now accepts a typed, closed F# `ViewWherePredicate` DU for
direct physical-column comparisons to proven integer/string/boolean
literals, `IS [NOT] NULL`, PostgreSQL `IS [NOT] DISTINCT FROM` against
those same typed scalar literals, and parenthesized `AND`/`OR` with SQL
precedence. NULL-safe comparisons remain separate closed operators:
unlike ordinary `<>`, `IS DISTINCT FROM` treats NULL and a non-NULL
value as distinct (and two NULLs as not distinct). Column names may
use the single effective `FROM` alias.
A bounded 256-node/32-grouping limit protects parser recursion; larger
definitions fall outside the current proven subset.

Every physical predicate column enters the View's `SourceColumns`
dependency facts **even if it is not present in SELECT output**. This
prevents `ALTER TABLE ... DROP COLUMN` from removing filter-only
dependencies. Replacement recalculates source and predicate dependencies
atomically, while the visible View output-prefix and dependent View
contracts stay intact. Repeated SELECT projections retain their prior
source-column representation.

An unknown physical column or unbound qualifier is Invalid with no
Catalog change. IN/BETWEEN/LIKE, functions, arithmetic, correlated
subqueries, col-to-col comparisons, unknown literal/operator type
compatibility and other non-proven expressions cannot publish Known
Catalog evidence. In the NULL-safe literal subset, `IS DISTINCT
FROM NULL` is still outside the proven grammar; use the independently
typed `IS [NOT] NULL` form. No parser acceptance is execution or
permission authorization; `AnalyzeScript` remains strictly static.
PostgreSQL live differential tests verify NULL versus ordinary SQL
three-valued logic, filter-only DROP protection and `CREATE OR REPLACE
VIEW` dependency rebinding.

## PostgreSQL single-source qualified View projections

The bounded PostgreSQL Virtual Catalog grammar now supports direct
single-relation projections such as `SELECT o.id, o.label FROM app.orders
AS o` and `SELECT o.* FROM app.orders o`, plus ordinary unqualified
columns/`*`. The existing typed projection is a **closed F# DU**
(`Wildcard` or `NamedColumns`), with an optional qualifier on each
column. The optional `FROM` alias is resolved as a local binding;
without one, the source relation's final identifier is its binding.
A declared alias **hides** the unaliased relation name.

Proof requires every explicit qualifier to match the one effective
`FROM` binding. An unbound/shadowed qualifier is Invalid, and its
Catalog remains unchanged. Three-part projections, joins, predicates,
multiple FROM relations, source column-alias lists and arbitrary
expressions remain outside this proof. No other dialect gains Known DDL.

Visible output names still follow projection `AS` aliases and
View header-prefix overrides, while `SourceColumns` preserve the
actual names read from the direct source. `CREATE OR REPLACE VIEW`
retains output-prefix compatibility and rewires source dependencies
without affecting dependent View contracts. Live PostgreSQL parity
verifies the output-column shape and physical DROP-column dependencies.

This is static `AnalyzeScript` Catalog reasoning, not executable
SQL compilation, authentication/authorization or proof of actual DDL
execution.

## PostgreSQL explicit View output headers

`CREATE [OR REPLACE] VIEW schema.view (public_id, public_label) AS
SELECT id, label FROM schema.table` accepts a PostgreSQL-specific,
optional typed `ViewOutputHeader` DU (`Inferred` / `Explicit`).
Header identifiers override SELECT output names **by position**.
The header length may be shorter than the derived SELECT width:
provided names override the **leading** columns and remaining SELECT
names are retained. A header longer than the SELECT width is invalid,
including with `SELECT *`. The resulting complete list of output
names must be unique, even when the physical source projection repeats
a column or uses duplicate intermediate SELECT aliases.

Unquoted PostgreSQL reserved words are not valid View column names.
The typed Catalog does not yet retain identifier-quoting provenance,
so all 78 PostgreSQL 16-18 reserved keywords are conservatively
outside this explicit-header proof, even when quoted. This prevents
false Known proof where follow-up source binding would be ambiguous.

The immutable Virtual Catalog retains **independent** direct source
column names and visible output names. Downstream View resolution uses
the latter; `ALTER TABLE ... DROP COLUMN` dependency checking uses the
former, never the header alias. Output column types and modeled
nullability continue to come from source bindings. Existing
`CREATE OR REPLACE VIEW` output-prefix compatibility checks run
**after** the optional header has been applied. Overlong header,
duplicate resolved output names, unbound source columns, incompatible
replacement or unprovable schema identities cannot emit a Known Catalog delta.

Only narrow single-source, physical-column or wildcard SELECT forms
are proven for PostgreSQL. This remains static `AnalyzeScript` data
modeling, not database execution, permissions or full View grammar
support. Live PostgreSQL CI differential tests compare real header
names/types and dependency behavior.

## PostgreSQL CREATE OR REPLACE VIEW with fixed output contracts

PostgreSQL `CREATE OR REPLACE VIEW app.v AS SELECT ... FROM app.source`
can statically replace an existing proven View, or create it if absent.
The closed F# `ViewCreateMode` distinguishes `CreateOnly` from
`CreateOrReplace`. An existing View replacement preserves **every
original output column's name, position and type** (and conservatively
its modeled nullability), but permits trailing output columns.
Incompatible replacement is rejected without changing the Catalog.

A replacement may change its direct source relation and source-column
bindings. After the full proof succeeds, one immutable Virtual Catalog
update replaces the View definition and its physical dependencies.
A View cannot be replaced to reference itself or a transitive dependent
View. Downstream Views keep their existing proven output contracts;
subsequent `ALTER TABLE ... DROP COLUMN` checks follow the **new**
physical dependency, not a stale original source or the View's alias.

An existing View produces typed `SqlCatalogOperation.ReplaceView` and
`SqlStatementEffectOperation.ReplaceView` entries; a missing target
still produces `CreateView`. Other SQL dialects, arbitrary SELECT
expressions, open-world Catalogs and uncertain relation identities do
not gain Known proof. `AnalyzeScript` performs **static analysis only**:
it does not check ownership/permissions, prove the real server accepted
the command, compile it for execution or issue SQL.

## PostgreSQL atomic multi-view DROP and graph-aware RESTRICT

A dedicated closed F# `DropViewBatch` models
`DROP VIEW [IF EXISTS] view_a, view_b [RESTRICT|CASCADE]`.
Every requested physical View identity and relation kind is validated
against the **original** closed-world Virtual Catalog before a single
`DropView` change is published. Duplicate identities, missing
non-conditional names, wrong-kind Table/Index relations, ambiguous
schema resolution and potentially implicit key-backed indexes remain
invalid or unproven rather than partially succeeding.

Under `RESTRICT`, a dependent View is permitted when that dependent
View is **also** in the batch; any dependent outside the batch blocks
the whole statement. Under `CASCADE`, a cycle-safe transitive closure
is computed once and de-duplicated against explicitly named Views.
The final immutable Catalog and one exact Typed Effect per removed View
are published only after the complete proof succeeds. `IF EXISTS`
skips only demonstrated absence. Normal single-view behavior and the
other five provider grammars are unchanged.

Static Catalog atomicity does not prove SQL execution, ownership
authorization, database rollback or unmodeled physical dependencies.

## PostgreSQL typed View dependencies and transitive CASCADE

A distinct immutable `SqlCatalogView` holds structured View relation
identity, directly proven source dependencies, and source-derived output
columns. PostgreSQL `CREATE VIEW name AS SELECT * FROM source` and
direct named-column single-source SELECT forms (including explicit `AS`
aliases) are proven against a closed-world Catalog only. View output
names and actual physical source-column dependencies are tracked separately;
renaming the output cannot bypass source-column DROP dependency checks.
Joins, WHERE, functions, implicit aliases, aggregates and materialized
views remain outside the proven View definition subset. Unsupported
definitions must never mint known dependencies.

`DROP VIEW [IF EXISTS] name [RESTRICT|CASCADE]` and PostgreSQL
`DROP TABLE ... CASCADE` compute a cycle-safe transitive View dependency
closure from the original Catalog before publishing any changes.
RESTRICT fails on dependent Views. CASCADE removes their **entire View
relations**, unlike an external Foreign Key where only the named
constraint is removed. Typed CreateView/DropView changes are kept
separate from base-table changes and do not assert that a View is an
ordinary table or that SELECT through Views has been type-verified.
Table/View names share a PostgreSQL relation namespace; wrong-kind
DROP TABLE and CREATE TABLE collisions cannot silently succeed.
Named PK/UNIQUE backing indexes also block colliding View relation names.
If an implicit key index's physical name is not established, a missing
`DROP VIEW IF EXISTS` target is unproven rather than a false no-op.
Dropping a source column used in a proven View remains blocked.

This is static Virtual Catalog modeling, not an attestation of
PostgreSQL server dependencies, execution, ownership or transaction
rollback. Unsupported View definitions taint the Catalog rather than
publishing incomplete Known graph facts.

## PostgreSQL FK-aware DROP TABLE CASCADE

The PostgreSQL `DropTableBatch` closed F# DU carries typed
`DropDependencyMode` (`Restrict` or `Cascade`). Both single-table
and multi-table `DROP TABLE ... CASCADE` validate against the same
original immutable Virtual Catalog, before emitting any changes.

Within a proven complete Catalog, CASCADE removes requested tables and
**named inbound Foreign Key constraints on surviving tables**, while
preserving the referencing tables, their columns, other constraints and
unrelated dependencies. The typed `DropConstraint` Catalog Changes and
Effects expose `ConstraintName` to identify the exact modeled FK;
legacy constructors remain compatible. Unnamed FKs cannot be identified
by their physical server name and must remain unproven. The RESTRICT
mode still blocks external inbound FKs.

This is static virtual-Catalog proof, not actual DDL execution,
authorization or a guarantee of server transaction atomicity. The
Virtual Catalog does not attest live views, triggers, routines, or
unmodeled dependencies, and opaque DDL continues to invalidate proof.
Open-world states and unsafe schema resolution remain fail-closed.

## PostgreSQL multi-table DROP and intra-batch foreign keys

PostgreSQL `DROP TABLE table_a, table_b [RESTRICT]` (and
`IF EXISTS`) now uses a dedicated closed `DropTableBatch` F# union
case. The coordinator validates every table identity and all inbound
foreign keys against the same original immutable Virtual Catalog
before publishing any changes. Foreign keys from tables **inside**
the proven drop set are removed with those tables, regardless of
their ordering; a known referencing table **outside** the set blocks
the entire batch under PostgreSQL's default RESTRICT behavior.

The whole batch must have a closed-world known Catalog and unambiguous
qualified identities (unqualified members require a proven single
ordinary `search_path`). Duplicates, missing non-conditional names,
index-versus-table name collisions and unresolved catalog ownership
cannot become partial Known changes. Only after the full proof
succeeds does one local immutable fold remove the proven tables,
emitting one exact `DropTable` Catalog change and typed effect per
removed table. `IF EXISTS` skips only demonstrably absent members.
Other five dialects' existing grammar is unchanged; `CASCADE`
remains unsupported because external dependent-object rewrites are
not modeled. This is static Catalog-change publication atomicity,
not runtime SQL transaction, DDL execution or authorization.

## PostgreSQL atomic multi-index DROP Catalog validation

PostgreSQL `DROP INDEX name, name [RESTRICT]` and its `IF EXISTS`
variant now enter the closed `DropIndexBatch` F# union case with a
non-executable list of structured index identities. The compiler
checks all names against the **original immutable Virtual Catalog**,
including schema resolution, duplicate physical identities,
missing names, wrong-kind table relations, possible constraint-backed
indexes and open-world uncertainty. A mixed qualified/unqualified list
requires a proven single-schema `search_path` for its unqualified names.

Only after all members are proven does a local fold produce the
updated immutable Catalog and one exact `DropIndex` delta per removed
standalone index. Multiple indexes on the same table are handled in
one statement. `IF EXISTS` skips only demonstrably absent members.
A failed or unproven member never publishes partial Known changes
or structured Known effects, even when earlier members were valid.
This is atomicity of **static analysis / Catalog delta publication**,
not execution, database transaction rollback, index ownership
authorization or a guarantee of actual server-side atomicity.
`CONCURRENTLY`, `CASCADE`, `ON table` and other-provider
multi-index grammar remain outside the proven capability subset.

## PostgreSQL unqualified DROP INDEX IF EXISTS and explicit RESTRICT

The closed `DropIndex` DDL operation now carries a typed `ifExists`
flag for PostgreSQL unqualified index names. `DROP INDEX IF EXISTS name`
requires a proven **single schema** `search_path` and a fully known
closed-world Virtual Catalog; only then may a matching standalone index
be removed, or a proven-absent name become an exact no-op. A table
relation of the same name is a semantic rejection, while implicit
constraint-backed index names and caller-supplied unqualified Catalog
owners remain unproven and fail-closed.

PostgreSQL also accepts optional `RESTRICT` on both unqualified and
schema-qualified `DROP INDEX`. Because RESTRICT is already PostgreSQL's
default dependency policy, this is a syntax capability expansion only:
it adds no cascade, deletion authority, runtime attestation, or bypass
of the existing constraint/dependency proof. Other five providers
retain their dialect-specific drop syntax. `CASCADE`, `CONCURRENTLY`,
and multi-index drops remain unsupported.

## PostgreSQL schema-qualified DROP INDEX

The closed DDL parser now recognizes `DROP INDEX schema.index` and
`DROP INDEX IF EXISTS schema.index` for PostgreSQL, including separately
quoted schema and index identifier parts. It resolves the **index relation
identity** against the known owner tables in exactly that schema, without
consulting or changing the connection's current `search_path`. Two
different schemas may contain the same standalone index name; a qualified
drop removes the requested owner's index only. The existing unqualified
`DROP INDEX index` rules and other five SQL provider grammars remain
unchanged.

An exact index removal requires a closed-world Catalog, unambiguous
qualified owner and no unproven implicit constraint-index collision.
`IF EXISTS` is a no-op only when absence is proven and the requested
name does not identify a tracked table or potentially implicit index.
Unknown/open-world Catalogs, unqualified owners with ambiguous schemas,
unknown backing index names, `CONCURRENTLY`, `CASCADE`, multi-index
drops and other unsupported modifiers remain fail-closed. This is
static Virtual Catalog analysis, not proof of execution or ownership.

## PostgreSQL schema-local index and relation namespace

For a fully schema-qualified PostgreSQL target table, `CREATE INDEX`
checks the standalone indexes and possible implicit constraint indexes
of tables in the **same proven schema**. Known tables in a different
explicit schema no longer cause false duplicate-name rejection or
unrelated implicit-index uncertainty. The model also rejects a new
index whose name matches a tracked table in that schema, and rejects
`CREATE TABLE` when its proposed relation name matches a tracked
index in the same schema. Index names are not independently qualified:
they inherit the indexed table's schema.

Unqualified Catalog identities may resolve into any schema; they are
retained as potential collisions, never discarded by a target schema
filter. An unqualified `DROP INDEX` referring to a qualified index owner
requires a proven single `search_path`, even if the Virtual Catalog
happens to contain only one matching qualified index.

PostgreSQL `CREATE TABLE IF NOT EXISTS` is a static no-op when an index
relation with the same name already exists in the proven target schema;
it must not publish an invented table. The same name without
`IF NOT EXISTS` is rejected. Fully schema-qualified `DROP INDEX` SQL
is not yet supported by the current narrow DDL grammar and remains
fail-closed.
Only closed-world, fully known virtual Catalogs can mint exact index
changes. This is static SQL analysis, not server-side schema attestation.

## MySQL ALTER TABLE secondary-index lifecycle

MySQL now supports `ALTER TABLE table ADD INDEX name(columns)`,
`ADD KEY name(columns)`, `DROP INDEX name`, and `DROP KEY name`
through the **same closed F#** `CreateIndex` and `DropIndex` DDL union
cases used by standalone `CREATE INDEX` and `DROP INDEX ... ON table`.
The source dialect is checked before recognition; no new runtime
executor or Query/Mutation compiler route is introduced.

A physical-column-only, named index on a known table can produce an
exact Catalog delta; dropping an existing tracked standalone index
removes only the index on that target table. Index identities are
table-local in MySQL, so different tables may carry the same name.
Immutable Catalog snapshots preserve earlier states, and the existing
column-dependency proof prevents removing an indexed column before its
index is dropped.

Unique, unnamed, expression, prefix-length, descending and
index-option syntax, and multi-action ALTER statements are not
silently normalized. Missing target tables or index identities and
unknown Catalogs do not publish a false Known delta; implicit
PRIMARY/UNIQUE-backed index identity is not claimed as standalone.
The real MySQL DDL differential corpus checks all **25** scripts after
every statement; the analyzer itself never executes SQL.

## Oracle RENAME COLUMN typed Catalog lineage proof

Oracle `ALTER TABLE table RENAME COLUMN old_name TO new_name` is a
closed F# `RenameColumn` operation with one exact `RenameColumn`
Catalog/effect tag, not an opaque string transform. Against a
closed-world, known Catalog, the analyzer preserves physical column
position, type and nullability. It also retargets every modeled
standalone index's ordered columns and local PRIMARY KEY / UNIQUE /
FOREIGN KEY bindings. Oracle now shares the proven immutable
cross-object transition with PostgreSQL: **all inbound and self-FK
referenced columns follow the renamed parent's exact structured
identity**, including ordered composite keys, multiple child tables,
and repeated renames. Local child columns, unrelated FKs, constraint
names, and all old Catalog snapshots remain unchanged. The entire
rename publishes **one** Known Catalog state and `RenameColumn` delta.

Oracle does **not** inherit PostgreSQL's View rewrite proof. The
database invalidates dependent Views on column rename, so known
dependent Views remain fail-closed. Unknown/open-world Catalogs,
unmodeled objects, implicit reference inference, invalid/duplicate
column names, composite ALTER forms and unsupported dialect grammar
never mint Known proof. Analysis is metadata-only, not DDL execution
or permission authorization.

The original real Oracle DDL differential corpus includes **26**
unique scripts comparing physical columns and `USER_IND_COLUMNS`
index lineage after each step. Five additional live Oracle Free FK
scripts now verify the *actual* physical `USER_CONSTRAINTS` and
`USER_CONS_COLUMNS` referenced-column lineage, matched by
`R_CONSTRAINT_NAME` and ordered `POSITION`, after every statement.
Unlike PostgreSQL, Oracle DDL implicitly commits: test isolation uses
unique identifiers and explicit `CASCADE CONSTRAINTS PURGE` cleanup,
never rollback. SqlCore itself does not execute SQL.

## Firebird RECREATE TABLE atomic static Catalog proof

Firebird 5 supports `RECREATE TABLE table (column definitions...)` as
one DDL statement. Unlike a two-statement `DROP TABLE; CREATE TABLE`
sequence, the typed F# `Recreate` operation validates the entire
replacement first and publishes **one immutable Catalog transition**.
A proven existing table produces both exact DropTable and CreateTable
effect deltas; a proven absent table produces only a CreateTable delta.
Existing indexed-column lineage, standalone indexes and constraints are
removed before the new columns and constraints are modeled.

Known inbound foreign keys and dependent Views block recreation. Unknown
or incomplete Catalogs, unsupported table attributes, invalid columns
and unprovable new constraints never publish partial successful
recreation evidence. The parser accepts this syntax only for Firebird;
it does not generalize RECREATE to the other five dialects or expose
DDL execution. The Firebird 5 live integration corpus now has **22**
multi-statement scenarios comparing each resulting Catalog against
independent RDB$ metadata.

## SQL Server 2016+ conditional DROP INDEX proof

The SQL Server source analyzer supports `DROP INDEX IF EXISTS index_name
ON table_name` only with an explicit SQL Server version profile of
**13.0 or later** (SQL Server 2016+). It reuses the closed typed
`DropIndex(name, target, ifExists)` operation, preserves the target
table's identity, and does not infer cross-table index ownership.

When the target is a known existing table in a complete Virtual Catalog,
a matching tracked standalone index yields one exact DropIndex delta; if
no index matches on that table, the statement yields an exact no-op even
when another table has an index of the same name. An unknown or missing
target, unknown Catalog, or a target with possibly implicit
constraint-backed indexes cannot provide certainty about absence and
remains fail-closed. Non-conditional DROP INDEX behavior is unchanged.

Unsupported T-SQL options, multi-index forms and other dialect grammar
are not generalized. The SQL Server 2022 in-container differential
corpus verifies real sys.indexes metadata after every statement. The
SqlCore analyzer never connects to the server or executes any SQL.

## SQLite conditional CREATE INDEX proof

The SQLite-only static Catalog analyzer supports `CREATE INDEX IF NOT
EXISTS name ON table(column[, ...])` in a proven unqualified database
namespace. The target must be a known table. If a standalone index with
the name exists on **any** known table, SQLite performs an exact no-op,
even when the supplied indexed columns or target table differ. Otherwise,
only proven physical-column indexes produce a Known CreateIndex delta.

An existing **table** (or known View) with the index name is a wrong-kind
collision, never a conditional no-op. Unknown Catalogs, implicit indexes
from constraints, attached/qualified database namespaces, partial/index
expressions, unique-index syntax and unresolved targets cannot mint Known
Catalog changes. Other five dialects retain their own capability gates.
SQLite in-memory differential parity checks actual index ownership after
each statement; `AnalyzeScript` never runs the SQL.

## Table-local index namespace proof

`CREATE INDEX` compares existing standalone indexes and the possible
names of constraint-backed implicit indexes within the **owning table**
for MySQL and SQL Server. Therefore two different known tables can have
the same standalone index name; `DROP INDEX name ON table` still removes
only the specified owner's index. A PRIMARY KEY on an unrelated table
cannot block an index on the target table.

PostgreSQL, SQLite, Oracle, and Firebird keep their conservative
Catalog-wide collision and implicit-index-name checks; narrower
schema-specific namespaces have not been established by this change.
Every dialect still requires a closed-world known Catalog and proven
target-table shape before an index creates an exact `Known` Catalog
delta. Open-world states, unmodeled implicit indexes on the target and
unsupported index grammar remain fail-closed.

## Open-world Catalog and rejected DDL semantic certainty

A rejected DDL statement (`Status = Invalid`) never mutates the Virtual
Catalog or emits a proven delta. Duplicate column names and other modeled
local semantic errors can be rejected independently of whether the database
is fully known, but this does **not** prove the surrounding Catalog is
complete. Accordingly, the DDL rejection branch emits
`Statement.Certainty = Unknown` whenever the input Catalog is open-world
or partially known; only a complete `SqlCatalogKnowledge.Known` input
retains `Known` on an invalid statement. Six-provider tests contrast
`SqlCatalogState.Unknown()` with caller-supplied `KnownEmpty()`,
including partial and unsupported DDL followed by additional statements.

DDL parsing, semantic invalidation, source diagnostics, dependency checks
and fully proven closed-world Catalog deltas are unchanged.

## Unknown transaction cannot mint Known Catalog or typed-effect certainty

The Script coordinator uses its final, fail-closed `runtimeCertainty`
when creating `Statement.Certainty` and structured `DetailedEffects`.
A statement starting with `TransactionBefore = Unknown` cannot regain
`SqlCatalogKnowledge.Known` solely because its syntax is recognized,
including Oracle `COMMIT / ROLLBACK` and semantically invalid DDL.
Transaction-control detailed effects now inherit the coordinator's
certainty, rather than inferring Known from `Status = Analyzed`.

The operation, syntax status, original diagnostics and typed control
provenance remain unchanged. This fence does not assert execution,
change the six-dialect SQL grammar, or invalidate independently proven
static rollback snapshots in a modeled transaction.

## Unknown transaction cannot re-establish proven Session state

A later recognized Session statement does not repair a lost transaction
premise. If `TransactionBefore = Unknown`, supported `SET search_path`,
SQL Server `SET NOCOUNT`, and MySQL/SQL Server `USE` retain their
typed `SessionOperation` in `DetailedEffects`, but the Script analysis
reports `PartiallyAnalyzed`, `Certainty = Unknown` and partial effect
coverage. The next Session state remains unproven (no known search path,
database context or NOCOUNT), and the Catalog stays tainted.

The ordinary `Outside` and modeled `Active` Session paths are unchanged.
This does not claim the command failed or executed, and does not invent
another diagnostic; it prevents syntax acceptance from manufacturing
runtime Session or rollback evidence.

## Outside transaction provenance after opaque Script effects

The static `Outside` state means no earlier modeled transaction command
has opened a transaction in the analyzed Script. It **does not** attest to
the real connection's transaction state. Unsupported Session/DDL/DCL or
unclassified SQL, procedure invocations and executing Utility commands can
invalidate the prior boundary even without a preceding modeled `BEGIN`.
The coordinator moves `Outside -> Unknown`, invalidates stale Catalog and
Session proofs, and reports `UnprovenRuntimeEffects` / `Unproven` in
`TransactionProvenance`, not `OutsideTransaction`.

Supported SELECT, modeled Session settings/DDL and planning-only
`EXPLAIN` retain their static `Outside` state. Invalid syntax is not
treated as an executed transaction mutation. The existing transaction
analyzer handles unsupported explicit transaction commands without adding
duplicate context diagnostics. Oracle COMMIT/ROLLBACK grammar remains
recognized even when the input state is Unknown, but does not reconstruct
any lost static transaction or Catalog proof. Once `Unknown`, later transaction text is
not proof of restoration. None of these results are execution evidence.

## Fail-closed Session proof after opaque Script effects

The static Script coordinator now clears prior Session facts after
`Unsupported` statements (including unclassified commands), recognized
procedure invocations and utilities that may execute SQL or perform
maintenance. These commands may alter `search_path`, current database,
`NOCOUNT` or other unmodeled session settings. Keeping the previous
Session snapshot would incorrectly imply the same resolution premises
still apply after such a statement.

This invalidation is **not proof** that a database command executed or
that a particular setting changed. Explicitly supported Session commands,
planning-only `EXPLAIN`, and invalid syntax that was not recognized as
a possible executed command retain their prior Session analysis behavior.
The existing Transaction/Catalog fail-closed boundaries still apply.

## Unknown effect propagation for unproven Script statements

`SqlStatementAnalysis.Status = Unsupported` or `Invalid` now always
includes `SqlEffectKind.UnknownEffects` in its public `Effects`, and in
`TransactionProvenance.PotentialEffects`. This includes unsupported
Session, DDL, DCL and TCL, unclassified SQL and rejected syntax. A statement
family label alone cannot prove the absence of other side effects.

For Unsupported statements, `DetailedEffects` preserves any already-known
typed *potential* effects and includes an opaque `UnknownEffects` entry
if necessary, without duplicating existing unknown entries. Invalid syntax
continues to expose no detailed object effects; its status, `Effects` and
`EffectCoverage = Unknown` preserve uncertainty instead.

Fully modeled statements and deliberately partial query/security analysis
retain their existing flags. No catalog state, session state, transaction
boundary, authorization, runtime DML, or server execution proof is minted.

## Structured effect parity for implicit DDL commits

For supported Oracle/MySQL DDL, `Statements[*].DetailedEffects` and the
script-wide `DetailedEffects` now include a typed
`SqlStatementEffectOperation.PossibleImplicitCommit` with
`SqlEffectKind.ChangeTransaction` whenever the transaction provenance
identifies a possible implicit commit. This is distinct from an explicit
`TransactionControl` statement. The effect has `Certainty = Unknown`,
no object identity or dependencies, and the DDL statement's source span.

A conditional DDL statement can have **no Catalog delta** and still
possibly COMMIT a pending transaction. Unsupported MySQL DDL within a
modeled transaction also retains the potential commit effect without
inventing a proven Catalog change. No server execution, data mutation,
authorization, or rollback proof is implied.

## Oracle DDL implicit COMMIT and independent TCL boundaries

Oracle SQL DDL implicitly COMMITs **before and after** each DDL statement.
Analyzed Oracle DDL therefore carries both `ChangeSchema` and
`ChangeTransaction`, with
`TransactionProvenance.Origin = PossibleImplicitCommit` and
`RollbackScope = Unproven`. This is a **possible runtime side effect**
of the script, not evidence that any DML or COMMIT actually ran.

Oracle's independent `COMMIT`, `COMMIT WORK`, `ROLLBACK` and
`ROLLBACK WORK` are recognized, leave the already-modeled final
Virtual Catalog unchanged, and preserve the `Outside` static schema
transaction state. A later `ROLLBACK` does not undo an Oracle
`CREATE TABLE`. The `RollbackScope` for this case is
`NotApplicable`, not a claim of Catalog snapshot rollback.
Unknown virtual Catalog state is **never** restored by a later COMMIT.
Oracle `SAVEPOINT`, `ROLLBACK TO SAVEPOINT`, `SET TRANSACTION`
and distributed/force variants remain unsupported without a proven
transactional state machine.

No Oracle DML execution, row-level rollback, trigger behavior,
transaction isolation, or server-side authorization is inferred.
Provider-specific capabilities must remain fail-closed.

## Final Virtual Catalog symbol projection

`SqlScriptAnalysisResult.Symbols` is an immutable, static **final state**
projection of Table and Column declarations from `FinalCatalog`. Every
`SqlScriptSymbol` includes `Kind`, normalized `Name`, structured
`TableIdentity` (including original identifier part boundaries), and
`Certainty = Known`. Column entries also expose `TypeName` and
`Nullable`; table entries have null type and no nullable value.

This does **not** reconstruct transient declarations that were dropped or
rolled back, and it does not include query aliases, CTEs, variables,
procedural/local symbols, or the effective set of live server objects.
Tables whose Virtual Catalog state is not Known do **not** mint proven
Symbols. `HasCompleteSymbolCatalog` is only true if the **virtual**
FinalCatalog is closed-world and every table shape is Known; an empty list
with `HasCompleteSymbolCatalog = false` **does not** mean the database
has no objects. A confirmed virtual declaration is still never execution
authorization or attestation of server-side existence.

```csharp
var analysis = SqlCoreFacade.AnalyzeScript(
    "CREATE TABLE orders(id INTEGER);",
    new SqlScriptAnalysisOptions(
        SqlAgentToolType.Postgres, SqlCatalogState.KnownEmpty()));
if (analysis.HasCompleteSymbolCatalog)
    foreach (var symbol in analysis.Symbols)
        Console.WriteLine($"{symbol.Kind}: {symbol.TableIdentity.Key} / {symbol.Name}");
```

## Script-wide static object dependencies

`SqlScriptAnalysisResult.Dependencies` projects immutable, source-ordered,
**potential object references** from existing `DetailedEffects`. Each
`SqlScriptDependencyReference` records `StatementIndex` (zero-based),
`Kind` (`ReadTable`, `MutationTarget`, `PermissionTarget`, or
`RoutineInvocation`), diagnostic `ObjectName` and `Span`.
`Certainty` is **always `Unknown`**. A span may cover the whole
statement, rather than the exact identifier token.

These are **not** resolved object identities, complete query lineage,
proven foreign-key/schema dependencies, effective SQL privileges, or
authorization to execute anything. Unsupported and invalid statements
never create fabricated references. DDL declarations belong to
`CatalogChanges`, not `Dependencies`. The same object can appear in
different statements or different reference kinds; within one statement,
duplicate same-kind and exact-name references are collapsed. Quoted names
are not silently case-folded.

```csharp
var report = SqlCoreFacade.AnalyzeScript(
    "SELECT id FROM users; DELETE FROM orders WHERE id = 1;",
    new SqlScriptAnalysisOptions(SqlAgentToolType.Postgres));
foreach (var reference in report.Dependencies)
    Console.WriteLine($"{reference.StatementIndex}: {reference.Kind} {reference.ObjectName}");
```

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

## Oracle MINUS set-operation capability

The Oracle source parser now recognizes the traditional unquoted `MINUS`
set operator and represents its duplicate-eliminating semantics using
the closed `SetOperator.Except` AST. Oracle targets render native `MINUS`;
supported non-Oracle targets render `EXCEPT`. The keyword is recognized
contextually only at Oracle set boundaries, preserving existing identifier
lexing in the other five providers.

The source grammar also admits Oracle left-to-right-compatible
`INTERSECT` prefixes followed by `MINUS`, `EXCEPT`, or `UNION`.
Explicitly parenthesized right-side compound set operands are preserved
as nested closed Query AST branches. Oracle and PostgreSQL render the
required parentheses around these nested branches; no precedence is
silently reassociated when lowering `MINUS` to `EXCEPT`.
Unparenthesized right-side `INTERSECT` after `MINUS` / `UNION` /
`EXCEPT` remains rejected. This is a bounded, provenance-preserving
expansion rather than full Oracle mixed-set associativity emulation.

**Version-gated Oracle EXCEPT (21c+):** Oracle 18c/19c source grammar
uses `MINUS`; the `EXCEPT` synonym entered Oracle's source grammar
in 21c. A plain Oracle `EXCEPT` now requires an explicit matching
Oracle **source** capability profile with `ServerVersion >= 21.0`.
It does not need `NLS_SORT=BINARY`, because this is the ordinary
distinct set operation; the stronger duplicate-preserving ALL proof
continues to apply below. Oracle targets still render canonical
`SetOperator.Except` as native `MINUS` even for 19c targets.
Target-side profile evidence is never accepted in place of missing
source-version proof; other source dialects keep their own EXCEPT
grammar unchanged.

**Version-gated Set ALL (Oracle 21c+):** With an explicitly declared
Oracle source capability profile (`ServerVersion >= 21.0` and session
`NLS_SORT=BINARY`), raw Oracle `MINUS ALL`, `EXCEPT ALL`, and
`INTERSECT ALL` enter the corresponding existing closed
`SetOperator.ExceptAll` / `IntersectAll` cases. The target validates
its **own** profile independently: 21c+ Oracle renders native
`MINUS ALL` and `INTERSECT ALL`; PostgreSQL retains native
`EXCEPT ALL` and `INTERSECT ALL`. Missing/older versions and
case-insensitive/accent-insensitive or unknown `NLS_SORT` fail closed
because Oracle may substitute equivalent character values in Set ALL.
Unparenthesized unsafe mixed precedence is still rejected; explicit
parenthesized grouping remains available. This is a type-and-runtime
capability boundary, not execution or SQL text substitution.


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

Proven family mismatches now include non-sensitive evidence origins in
the existing diagnostic message: a resolved column identifier with its
lexical scope depth, the primitive literal's family (never its actual
value), or the target field for DML assignments. This is only attached
after a known incompatibility has been found. Unknown, ambiguous or
missing schema facts do **not** create a new mismatch or speculative
diagnostic; name binding still rejects invalid/ambiguous identifiers
before typed compilation. Compiler results and diagnostic codes remain
backwards compatible.

For proven type-family mismatches, diagnostics retain the established
`SQL_SCHEMA_TYPE_MISMATCH` code and SemanticValidation stage. Scalar
Query operator/operand spans continue through the closed AST; for
`INSERT VALUES` and `UPDATE SET`, the diagnostic now uses the original
failing assignment expression span when available, falling back to the
statement span only if the AST has no known offset. This lets an editor
highlight the offending SQL value without implying a new runtime or
authorization proof. Missing/untyped schema families never trigger a
speculative type mismatch.

Scoped typed-schema column lookups now preserve a closed, internal
`ScopedFamilyEvidence` distinction: missing name, known name with
unknown family, ambiguous source names, or proven family together with
its lexical scope depth. This information is not executable compiler
proof and is not exposed as a server type. When a nearer scope defines
a column with unknown family, an outer scope's known type is **never**
substituted; a genuine correlated outer reference still receives the
usual family check. Name validation remains the mandatory preceding
gate, so ambiguous and missing SQL identifiers still fail closed before
type evaluation. Regression coverage exercises six provider dialects,
correlated subqueries, derived projections and CTEs.

Literal classification now goes through the internal
`RewriteScalarTypeEvidence` DU (`Unproven`, `FamilyOnly`, `ExactPrimitive`).
The six-provider schema validator consumes only its conservative family
projection; Function return evidence consumes only an exact modeled
primitive type. A family-only Numeric fact (such as a decimal/float
literal or schema snapshot column) cannot silently become `integer` or
`bigint`; `NULL` remains unproven. Neither projection implies a server
CAST, runtime result or authorization. The six-dialect typed-schema
regression matrix and the Function evidence matrix guard this boundary.

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
and `SQL_PROCEDURAL_BODY_UNSUPPORTED`. Known incompatible primitive
result shapes now report `SQL_PROCEDURAL_RETURN_TYPE_MISMATCH`; unknown
operators, ambiguous names, NULL and unmodeled coercions continue to
report unproven/unsupported instead. Return failures carry an **absolute
body-local SourceSpan** rather than just the encompassing CREATE statement.
A mismatch is limited to the modeled static primitive evidence: it does
not assert that PostgreSQL rejects all implicit assignment casts at runtime.

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
`WHERE 1 = 1 OR id = 5`, `WHERE 1 < 2`, `WHERE 5 >= 5`, and
`WHERE NOT (3 > 4)`. It also proves integer/decimal constant order comparisons
inside AND/OR/NOT, without treating unknown column-dependent predicates as true.
Only same-kind exact integer/decimal literals may establish an ordering proof;
mixed numeric types, floating-point, NULL, implicit conversions, arbitrary
functions and collations remain unproven.
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

## MySQL native CTE queries and INSERT SELECT — version provenance

MySQL's native nonrecursive CTE syntax is `WITH ... SELECT` for DQL,
and `INSERT INTO target(columns) WITH ... SELECT ...` for DML.
The PostgreSQL-style statement-leading `WITH ... INSERT` is **not**
accepted for MySQL. The existing closed F# QuerySource AST, name binder,
Mutation target allowlist, physical source read permissions and parameter
renderer are shared; no SQL text is spliced into executable output.

A declared MySQL source or target `ServerVersion` earlier than **8.0.1**
cannot claim ordinary CTE support. The source parser and compilation stage
independently enforce this, including when the CTE appears inside INSERT
SELECT; versionless profiles retain the existing compatibility contract.
CTE names are not physical tables, and a read-only CTE cannot bypass
mutation approval, conflict policies, or source table permissions.

Real MySQL integration tests compare native and compiled INSERT CTE
results and parameter bindings. Cross-provider CTE semantics are not
inferred from the MySQL-specific version proof.

## SQL Server nonrecursive CTE UPDATE and DELETE (native mutation)

SQL Server's `WITH picked AS (SELECT ...) UPDATE ... WHERE ...` and
`WITH picked AS (SELECT ...) DELETE FROM ... WHERE ...` are typed, **native
SQL Server-only** Mutation statements. Multiple read-only CTEs and CTE
projection aliases are preserved. A CTE name may not be used directly
as the UPDATE/DELETE write target, because SQL Server may resolve that
name to an updatable underlying table whose lineage has not been proven
by the physical write allowlist. A declared source or target
`ServerVersion` earlier than SQL Server **2005 (9.0)** is rejected;
undeclared profiles retain the current compatibility behavior.

Both statements reuse the existing CTE binder, physical-table read/write
allowlists, selective-WHERE Mutation Policy, source spans, static effects,
parameterization and OUTPUT contracts; the CTE alias never becomes an
implicitly authorized physical table. CTE-prefixed joined UPDATE FROM /
DELETE FROM, recursive and data-modifying CTE definitions, and
cross-provider lowering remain fail-closed until independently proved.
`ParseScript` and `AnalyzeScript` do not connect to any database.
The SQL Server service CI runs native-vs-compiled row parity for UPDATE
and DELETE; no driver is added to SqlCore itself.

## Native SQL Server read-only WITH INSERT SELECT

SQL Server (2005 / ServerVersion 9.0+) now accepts native top-level
`WITH cte AS (SELECT ...) INSERT INTO table(columns) SELECT ... FROM cte`
using the existing closed `Insert(QuerySource)` AST and mutation compiler.
Multiple nonrecursive CTEs and explicit CTE output aliases reuse
the proven INSERT renderer. `OUTPUT INSERTED.column` additionally
requires the existing target-table- and operation-specific
`DmlResultRowAssurance.NoEnabledTriggers` proof; absent or mismatched
assurance is rejected before rendering. The CTE is
emitted before `INSERT INTO`; this is *not* MySQL's `INSERT INTO ...
WITH ... SELECT` source grammar.

The SQL Server source profile rejects explicitly declared versions
older than 9.0. When SQL Server is the compilation target, its target
profile also rejects explicitly declared versions below 9.0, even for
a different source dialect. Missing versions retain existing compatibility
behavior. SQL Server target compilation also rejects an unqualified
physical INSERT target that has the same name as a statement-level CTE;
CTE-updatable write-through lineage has not been separately proved.

Physical reads within CTE definitions and the physical INSERT target
remain independently allowlisted, with source spans, typed semantic
validation, parameterized literals and static effects preserved.
Statement-level `WITH INSERT ... VALUES` / `DEFAULT VALUES`, recursive
and data-modifying CTEs, nested input CTEs, and any unproved cross-dialect
semantics remain fail-closed. Real SQL Server 2022 native/compiled row
parity and OUTPUT parity are gated in the dedicated SQL Server CI job.

## Native SQL Server CTE joined mutation singleton proof

Native SQL Server 2005+ `WITH ... UPDATE target SET ... FROM cte WHERE ...`
and `WITH ... DELETE FROM target FROM cte WHERE ...` are admitted through
the closed F# Mutation AST **only if the bound CTE source graph is
statically proven to yield at most one row**. An initial singleton
`SELECT` without `FROM` may be followed by a chain of CTE-only SELECT
statements with optional WHERE filters; one bound CTE reference must be
the sole joined source. CTE column aliases are preserved via Binder.

SET operations, JOINs, grouped/aggregated CTEs, nested CTEs, physical
table scans within the CTE, multiple FROM sources and arbitrary derived
relations have no singleton proof and are rejected at TargetCapability.
This deliberately does *not* infer uniqueness from a plain WHERE equality
or assume arbitrary SQL Server UPDATE FROM duplicate matches are
deterministic. Existing non-CTE joined UPDATE/DELETE, SELECT, INSERT,
cross-provider fail-closed boundaries, profile gates and DML WHERE /
write-target allowlists remain unchanged. Native SQL Server 2022 DML row
counts and table snapshots are compared against compiled SQL in CI.

## SQL Server CTE Unique-Key Lineage in Static Mutation Analysis

For native SQL Server `WITH ... UPDATE ... FROM cte` and
`WITH ... DELETE ... FROM cte`, `AnalyzeScript` can now report
`SourceMatchCardinality.ProvenAtMostOne` when a **closed Virtual Catalog**
and a verified, explicitly declared ASCII-safe SQL Server collation establish
an exact `INT` primary/unique key, and the read-only CTE preserves that key
by direct projection (including field aliases or chained CTEs).
The final target-to-CTE predicate must contain a mandatory, type-proven
equality anchored to every projected unique-key column.

The analysis never derives cardinality from a CTE name, unverified schema,
a `WHERE` clause alone, a computed key expression, a non-unique source,
an incompatible/wide/nonnumeric key, a set operation or a multiplying join.
Any missing proof returns `Unverified`. No new SQL execution path, driver
or public tool is introduced.

**This is analysis evidence, not permission to execute SQL.** A user-supplied
Virtual Catalog is not an attestation of *live* physical uniqueness. The
Mutation compiler's existing singleton-CTE gate remains fail-closed for
CTE scans of physical tables; accepting those for execution requires an
independent metadata-backed live schema / freshness assurance at the
execution boundary. The six-dialect existing source and target capability
contracts remain unchanged.


## SQL Server live unfiltered INT unique-key assurance (metadata foundation)

`SqlServerCteUniqueKeyAssurance.VerifyLive(DbConnection, SqlSchemaSnapshot,
targetTable, sourceTable, sourceKeyColumn)` is a provider-driver-free public
contract. It only mints evidence from an *open actual SQL Server connection*,
using parameterized `sys.tables`, `sys.columns`, `sys.indexes` and
`sys.index_columns` reads. The source requires an enabled, unfiltered,
non-hypothetical, non-computed, nonnullable single-column `INT`
primary/UNIQUE index (clustered or nonclustered). Both physical
`schema.table` identities and typed source key must be present in the
immutable SQL Server snapshot. The proof binds the database and object IDs,
physical index, exact schema snapshot SHA256 and a deterministic digest
of both tables' column/index metadata. Inputs are restricted to simple
ASCII schema/table/key identifiers in this initial attestation contract.

`IsCurrent(DbConnection, DbTransaction)` must run on the **actual DML
transaction and connection**, not a detached connection. It acquires SQL
Server `TABLOCK, HOLDLOCK` on both physical tables, preventing DDL's
Sch-M lock while the write transaction is in progress, then re-reads
physical metadata and rejects any mismatch, dropped uniqueness,
removed index, renamed/replaced table or wrong transaction. This can
block concurrent DML and is intended only for explicit strict mode.

**This foundation does not authorize CTE joined mutations by itself.**
`CompileDml` intentionally retains its current conservative singleton
CTE restriction. A follow-up must bind the live proof to typed CTE key
lineage, compile evidence and mandatory commit-time `IsCurrent` checking
before physical-table CTE joined UPDATE/DELETE can be enabled in runtime
hosts. A caller-supplied Virtual Catalog, snapshot digest or
unverified unique index name is never a live execution guarantee.


## Statement-level CTE mutation CLR and host plan parity

`CoreSqlTextParser.ParseDml` now preserves read-only top-level
`WITH ... UPDATE` and `WITH ... DELETE` CTE definitions through the
existing public CLR `UpdateStatement.Ctes` / `DeleteStatement.Ctes`
collections. These default to empty for existing ordinary mutations.
The legacy-to-F# adapter roundtrips the same closed typed CTE queries;
no raw SQL string is substituted for a lost CTE.

`DmlPlanFactory` carries CTEs through physical target resolution and
places the identical lexical CTE scope on the distinct row-identity
`SELECT` used for preview/commit row-set revalidation. Thus a
CTE-prefixed mutation does not use a different source scope when
its approval match SQL is generated. Positive/negative parser, F#
compilation parity and host approval-plan tests cover this boundary.

This is a **representation and approval-match correctness** change:
SQL Server joined UPDATE/DELETE with physical-table CTE sources still
requires the independent runtime uniqueness assurance of #462, and
remains denied by `CompileDml` until the separate typed
predicate/evidence/commit-gate integration is complete. Native SQL
Server 2005+, CTE target guard, maximum-row policy and six-dialect
capability restrictions remain intact.


## SQL Server CTE source-key approval and transaction-held host gate (#466, stage 1)

A live `SqlServerCteUniqueKeyAssurance.VerifyLive` proof can now be attached
to a host `ValidatedDmlPlan.SqlServerCteUniqueKeyAssurance`. It is a
provider-minted, immutable physical **source** INT unique-key attestation;
a caller-supplied Catalog or SQL string is not equivalent. The approval
binding commits to the proof's database, exact source/target identity, index
and metadata digest, while all plans without this opt-in proof retain their
existing approval binding hash.

Host single- and atomic-DML commits require a strict, row-set-identified,
bounded SQL Server UPDATE/DELETE plan with the same exact physical target.
Before any row-set revalidation or mutation, the coordinator calls
`proof.IsCurrent(connection, transaction)` on the **actual mutation
connection and open write transaction**, after schema freshness checks.
Both tables are locked through commit/rollback; revocation, schema drift,
wrong connection/transaction and unverified physical metadata abort the
entire write without silently dropping the proof. The ordinary approval,
fencing, isolation and rollback rules remain mandatory.

**This is a host-side safety gate, not SQL Server physical-CTE execution
authorization.** The closed F# compiler still rejects unproven CTE sources
and a proof attached to a `ValidatedDmlPlan` does not change
`SqlCoreFacade.CompileDml` or authorize an arbitrary compiled command.
A subsequent stage must first prove exact closed-AST CTE key lineage and
bind execution authority to this same transactional gate before lifting any
physical-source CTE compiler restriction.

## SQL Server physical-source CTE DML, exact INT proof (#466, stage 2)

A new opt-in `SqlServerCteUniqueKeyAssurance.VerifyLiveForJoinedMutation`
factory attests **both** the source's enabled, unfiltered, unique single-column
INT index **and** the target's noncomputed exact INT comparison column from
the live SQL Server `sys.*` catalog. The immutable proof includes the target
key in its identity and performs the same check under commit-time transaction
locks. The older `VerifyLive` proof (without a target INT key) is not
sufficient to unlock compilation.

Attach the proof with
`SqlPlanValidationContext.WithSqlServerCteUniqueKeyAssurance(proof)` and
compile through the F# `SqlCoreFacade.CompileDml` API with explicit
SQL Server source/target profiles. The first admitted shape is exactly one
nonrecursive, read-only CTE selecting a single physical source INT key (an
output alias is permitted), with one bound CTE FROM source and an ordinary
mandatory target INT = CTE INT equality. SQL Server physical target name,
physical source table, source/target key and the compiler's proof fingerprint
must agree. No SQL-text substitution or second legacy parser authorizes it.

The strict Host `DmlPlanFactory` carries this same proof into the
`ValidatedDmlPlan`. `DmlCoordinator` validates the proof-bound compiler
fingerprint, requires strict bounded row-set approval, and calls
`IsCurrent` on the **actual write connection/transaction** before matching
or executing. The same constraint applies to atomic DML batches.
Other joined CTE graphs, arbitrary CAST comparisons, unproven uniqueness,
cross-provider lowering and unbound proof use remain fail-closed.

The proof never grants permission to execute without the Host transaction
gate. Manually executing generated SQL outside the approved transaction does
not satisfy the host security contract. Live SQL Server tests compare actual
UPDATE/DELETE row effects, while negative dialect/shape matrices preserve
existing refusal behavior.


## Automatic Host discovery of SQL Server CTE source-key proof (#466 stage 3)

The default `TypedDmlRuntime` already opens a runtime-version-verified
provider connection before planning an UPDATE/DELETE and supplies it to
`DmlPlanFactory.CreateWithMetadataConnectionAsync`. For native SQL Server
2005+ only, the Host can now *automatically* identify the previously supported
physical-read CTE proof subset **without asking the caller to manufacture a
`SqlServerCteUniqueKeyAssurance`**.

The Host first inspects the parsed, physically resolved CLR mutation: exactly
one nonrecursive read-only CTE selecting a single physical source key, with
one CTE source and a mandatory ordinary equality between that projected key
and the fully qualified target key. Unsupported projections, unions, joins,
non-INT columns, OR, casts, missing versions, cross-provider mutations,
multiple sources and ambiguous names are never automatically attested.

Automatic discovery requires the existing open metadata connection, strict
row-set identity and a positive per-statement `maxAffectedRows`. It uses
`VerifyLiveForJoinedMutation` to read real SQL Server metadata and mint
an opaque source-unique-INT/target-INT proof; it cannot use caller-provided
Virtual Catalog entries as authority. If the Host also has an immutable
schema snapshot, the proof must be compatible with it. Otherwise, the
discovery helper constructs only a **narrow proof-local two-table key witness**,
not a fake full-table schema snapshot: no ordinary schema-freshness
assurance is bypassed or claimed.

The F# compiler *independently* validates the CTE and refuses non-modeled
graphs; the existing compiled-command and approval fingerprints bind the
proof identity. Before executing the mutation, `DmlCoordinator` and the
atomic-batch coordinator still call `IsCurrent` with the **actual write
connection and transaction**, holding physical locks until commit or rollback.
No extra MCP tools, SQL execution drivers in SqlCore, relaxed DML defaults,
or new SQL capability for the other five databases are introduced.

The manual `WithSqlServerCteUniqueKeyAssurance` path remains available for
custom hosts; the overload without an already-open metadata connection
continues to fail closed on physical-source CTE DML. SQL Server 2022 live
tests cover UPDATE/DELETE, direct/aliased projections, Host approval and
commit, absent connection/row cap, and unsafe CTE grammar.

## SQL Server CTE physical database and DDL transaction fence (#466 stage 4)

SQL Server `DB_ID()`, `object_id`, and index IDs are **local
integers**, and can coincide on different server instances or in
independently created databases with identical schemas. The immutable
`SqlServerCteUniqueKeyAssurance` now also records the actual
`SERVERPROPERTY('ServerName')`, `DB_NAME()` and
`sys.database_recovery_status.database_guid` values from the open
SQL Server connection. Its versioned proof identity binds all three
together with the existing schema digest, physical table/index identity
and source/target INT key names.

Both initial `VerifyLive` / `VerifyLiveForJoinedMutation` and
transaction-bound `IsCurrent` require the real physical identity.
A missing/unreadable/null GUID or instance identity **fails closed**:
the Host cannot use a caller-supplied Catalog as replacement evidence.
Host planning, compiled mutation and approval fingerprints thereby
bind the same physical database witness; the commit rechecks it on
the actual write transaction.

The commit-time proof first acquires explicit source and target
`TABLOCK, HOLDLOCK` locks in canonical order, then rechecks the
unique physical INT index and database identity. SQL Server live
tests require a concurrent `DROP INDEX` from an independent
connection to time out until transaction rollback, for **both
populated and empty source tables**. After the index is dropped,
the old proof must fail. A second physical SQL Server database with
the same source/target DDL must also produce a different proof
identity and reject a plan minted for the first database.

This is a practical physical-instance/database identity fence, **not**
a claim that a displayable server name is a globally collision-proof
host identifier: cloning a whole SQL Server installation or restoring
its master/database files with retained GUIDs requires separate
operational identity controls. Operators must grant the SQL Server
metadata visibility required for `sys.database_recovery_status`;
without that visibility this feature deliberately rejects compilation
rather than falling back to unverified names.


## SQL Server chained read-only CTE INT-key lineage (#466 stage 5)

The F# DML compiler now recognizes an **ordered, linear 1–3 CTE** key
lineage rooted in one physical SQL Server table with a live-attested
enabled, unfiltered unique `INT` source key. Each additional CTE must
read **exactly the preceding CTE** and project the same direct key
column (optionally renaming it using `AS` or the CTE's column alias
list). The mutation must use the final CTE as its sole joined source
and an ordinary mandatory `physical_target.INT = final_cte.INT`
predicate. Physical target identity, verified SQL Server 2005+ source
and target profiles, strict target row identity, positive bounded
affected row count and a real metadata connection remain mandatory.

The closed F# AST compiler verifies the whole chain *independently* of
the Host's typed CLR AST discovery, which mints the existing
`SqlServerCteUniqueKeyAssurance` **only after live source/target
INT/index metadata inspection**. A chain does not mint a new SQL
index proof: its multiplicity bound is inherited from the one physical
unique key, and each CTE hop must be a nonmultiplying direct projection.
The existing physical server/database GUID proof identity, immutable
command/approval fingerprints and transaction-held `IsCurrent`
checks remain unchanged. No new MCP tools or compiler-owned drivers.

Recursive CTEs, a fourth hop, duplicate or skipped CTE names,
joins, other read tables, self-references, DISTINCT, aggregates,
GROUP BY, set operations, derived tables, computed/cast key
expressions, ambiguous qualifiers, OR-bound key equality and
CTE write targets **still fail closed**. Conditions inside a
single-source SELECT may filter rows but cannot increase cardinality.
This feature does not authorize composite unique keys or any joined
CTE capability for the other five SQL dialects.

SQL Server 2022 live tests exercise two-/three-hop projection with
direct and column-list aliases, `UPDATE` and `DELETE`, automatic
Host proof creation, CTE-preserving match preview, approved commit
and physical row parity. Explicit negative cases cover computed,
multiplying, wrong-scope and non-equality lineage.

## G1 — Strict additive DDL Schema Compiler (compile only)

`SqlCoreFacade.CompileDdl(sql, new DdlCompilationOptions(sourceDialect, targetDialect))`
returns a separate immutable `CompiledDdlPlan`, NOT a Query/DML
`CompiledSqlCommand`. `TryCompileDdl` returns typed diagnostic failure
without minting a plan. Native SQL is generated from the existing closed F#
`RewriteSchemaAnalyzer.DdlOperation`; no raw SQL string is handed to a driver.

The first bounded, single-statement, source==target provider subset works
for PostgreSQL, SQLite, SQL Server, MySQL, Oracle and Firebird:
- `CREATE TABLE widgets (id INTEGER NOT NULL, label VARCHAR(40))`
  (SQL Server uses `INT`, Oracle `VARCHAR2(40)`).
- `ALTER TABLE widgets ADD COLUMN label VARCHAR(40)` with each provider's
  native syntax (Oracle parenthesized, SQL Server/Firebird bare).
  Only nullable column addition is eligible.
- `CREATE INDEX ix_label ON widgets (label)`, including simple composite
  physical-column keys, nonunique and nonfunctional only.

It rejects conditional/foreign-key/constraint/destructive DDL, defaults,
computed columns, unique/partial/expression indexes, `AS SELECT`,
multi-statements, cross-provider translation and other unsupported syntax.
DCL, transaction control and procedural statements cannot render.

`Changes` are expected effects with required physical preconditions, not a
live database snapshot. `Certainty=Unknown`, `RequiresLiveValidation=true`
and `AuthorizesExecution=false` always hold. Oracle/MySQL are classified
as `ImplicitCommit`; others stay `Unknown` without runtime proof.
The immutable fingerprint covers canonical SQL, provider/profile version,
typed identity, operation, columns, preconditions and risk.

```csharp
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Ddl;
using HsSqlAgent.SqlCore.Enums;
var options = new DdlCompilationOptions(
    SqlAgentToolType.Postgres, SqlAgentToolType.Postgres);
var plan = SqlCoreFacade.CompileDdl(
    "CREATE TABLE audit_events (id INTEGER NOT NULL)", options);
// Display plan.Command.Sql, Changes, Impact and Preconditions for review.
// Do NOT execute without separate Host live identity/schema proof,
// DDL permission, human approval and provider-aware transaction strategy.
```

This is G1 compilation, not G2 live preview or G3 DDL execution.
SqlCore remains driver-free; `execute_ddl_sql` is NOT added. Six-provider
syntax unit tests do not substitute for later provider-live transaction and
schema outcome parity.

## G2 — Read-only DDL Schema Diff & Impact Preview

`SqlCoreFacade.PreviewDdl(sql, options)` compiles using **the same** G1
`DdlOperation` and native renderer, then projects a typed Before and
**hypothetical** ExpectedAfter without executing or connecting to a database.
`TryPreviewDdl` uses the existing typed-error return contract.

Optionally pass a caller-declared virtual `SqlCatalogState` as a third
argument to show matched known table columns, indexes, dependency graph
(view and foreign-key references), and DDL preconditions; the default is
`SqlCatalogState.Unknown()` and does not assume a database is empty.

- `preview.Before.Presence` is `Unknown`, `Absent` or `Present` under
  the **caller-declared** Catalog, never live physical metadata.
- `preview.ExpectedAfter` shows typed columns and indexes when derivable
  from the closed plan plus modeled Catalog; `IsConditional` is always true.
  An unresolved ALTER target never fabricates a complete resulting table.
- `preview.PreconditionStatus` distinguishes `Unverified`,
  `ConsistentWithCallerCatalog`, and
  `ContradictedByCallerCatalog`. A contradiction is not silent success,
  while consistency is **not** sufficient to execute.
- `preview.Dependencies` lists modeled View/ForeignKey dependents;
  `DependenciesAreLiveVerified` is always false. Wider index name scope,
  open-world Catalogs and unqualified session resolution remain unproven.
- `preview.Warnings` disclose unknown lock durations and row impact,
  implicit-commit providers, physical dependency completeness and actual
  Host permission/approval requirements.
- `preview.Plan.Command.Sql` contains fully compiled native DDL.
  `RequiresLiveValidation` and `RequiresDdlApproval` are true,
  `AuthorizesExecution` and `IsLiveVerified` are false.

```csharp
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Ddl;
using HsSqlAgent.SqlCore.Enums;

var options = new DdlCompilationOptions(
    SqlAgentToolType.Postgres, SqlAgentToolType.Postgres);
var preview = SqlCoreFacade.PreviewDdl(
    "CREATE TABLE audit_events (id INTEGER NOT NULL)", options);
Console.WriteLine(preview.Plan.Command.Sql);
Console.WriteLine(preview.ExpectedAfter.Columns[0].TypeName);
// Never execute just because a static precondition appeared consistent.
```

A virtual Catalog (even `KnownEmpty()` or a catalog produced by
`AnalyzeScript`) is **not** server-identity-bound physical metadata. This
G2 static/read-only preview deliberately does not attempt a transaction,
savepoint, rollback, DDL execution, approval, or live schema verification.
G3 Host must capture actual DB identity and schema state, compare
preconditions at approval and execution time, handle transactional behavior,
audit, and independently authorize any `execute_ddl_sql` call.
