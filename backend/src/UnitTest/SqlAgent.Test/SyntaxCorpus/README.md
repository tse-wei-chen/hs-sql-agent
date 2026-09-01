# SQL syntax corpus contracts

These corpora are merge contracts for the SQL compiler rewrite. They are not a list of syntax that the compiler is permanently limited to.

## Compatibility floors

- `sql-main-compatibility-floor.json` is the curated historical compatibility floor.
- `sql-generated-compatibility-floor.json` is the generated six-dialect query grammar floor.
- `sql-generated-dml-compatibility-floor.json` is the generated six-dialect common DML floor.
- `sql-generated-dml-predicate-compatibility-floor.json` is the six-dialect Cartesian UPDATE/DELETE predicate-assignment plus INSERT...SELECT projection/source-predicate floor.
- `sql-generated-recursive-compatibility-floor.json` is the version-aware recursive CTE grammar floor for PostgreSQL, MySQL, SQLite, and Firebird.
- `sql-generated-postgres-native-compatibility-floor.json` is the PostgreSQL 13 native modifier floor for DISTINCT ON, LATERAL, explicit NULL ordering, and FETCH WITH TIES.
- `sql-generated-postgres-quoted-function-compatibility-floor.json` is the PostgreSQL quote-sensitive function identifier expansion floor across projection, predicate, CTE-body, and scalar-subquery contexts.
- `sql-generated-oracle-native-compatibility-floor.json` is the Oracle 12.1 native row-limiting floor for explicit NULL ordering, row-count/percentage FETCH, and WITH TIES.
- `sql-generated-profile-sensitive-compatibility-floor.json` is the profile-sensitive grammar floor: MySQL 8.4 `ANSI_QUOTES`, `ANSI`, and `PIPES_AS_CONCAT` session semantics plus SQL Server 14.0 / compatibility 110 ordered `STRING_AGG`.
- The differential runner compares the base `main` assembly with the PR assembly one-way:
  - `main=success, PR=failure` is a regression.
  - `main=failure, PR=success` is a capability expansion.
- Intentional-breaking allowlists are explicit, reviewed exceptions. They default to empty and stale entries fail CI.

The curated and generated floors are independent. Do not replace the curated corpus with generated cases, and do not shrink a generated floor to make a regression disappear.

## Positive grammar bombardment

The service tests build deterministic Cartesian matrices rather than relying only on hand-authored happy paths.

Current query matrix floor:

| Dialect | Generated cases |
| --- | ---: |
| PostgreSQL | 432 |
| MySQL | 900 |
| SQL Server | 528 |
| SQLite | 825 |
| Oracle | 900 |
| Firebird | 900 |
| **Non-recursive subtotal** | **4485** |
| Version-aware recursive CTE matrix | 128 |
| PostgreSQL native modifier matrix | 72 |
| PostgreSQL quoted-function matrix | 20 |
| Oracle 12.1 native row-limiting matrix | 72 |
| MySQL session-mode matrix | 48 |
| SQL Server version/compatibility matrix | 36 |
| **Total positive query floor** | **4861** |

The recursive matrix additionally cross-products four proven native providers, four legal recursive-member shapes, four root consumers, and root ordering, with explicit source/target server-version proofs. The PostgreSQL native modifier matrix cross-products root/CTE placement, DISTINCT ON, CROSS/LEFT LATERAL, NULLS FIRST/LAST, and FETCH WITH TIES under an explicit PostgreSQL 13 source/target profile. The PostgreSQL quoted-function matrix cross-products five identifier shapes (quoted function, quoted schema, both quoted, quoted unqualified name, and a quoted canonical-looking `CORE_*` name) with projection, predicate, CTE-body, and scalar-subquery placement. Quoted names stay opaque on PostgreSQL so case-sensitive/native identity cannot be reinterpreted as a built-in; unsupported source dialects and cross-provider targets must fail at typed capability boundaries rather than losing quote intent during normalization. The Oracle native matrix cross-products root/CTE placement, simple/join/correlated sources, explicit NULL ordering, row-count versus percentage FETCH, and ONLY versus WITH TIES under an explicit Oracle 12.1 source/target profile. The profile-sensitive matrix makes source/target session modes and compatibility levels first-class parity inputs. MySQL cross-products concat/ANSI-quote modes with root, CTE, scalar-subquery, predicate, and ordering contexts; SQL Server cross-products ordered `STRING_AGG` across root, CTE, scalar-subquery, and grouped contexts under explicit ServerVersion 14.0 and CompatibilityLevel 110 proofs. The matrices exercise parsing, query facts/binding, validation, compilation, and native rendering. Assertions are semantic and renderer-aware: source spelling is not required to survive when a target renderer intentionally lowers it to an equivalent native form.

The DML positive floor contains 42 common six-dialect cases, 17 explicit native/profile/assurance-gated capability cases, 180 Cartesian UPDATE/DELETE predicate/assignment cases, and 180 Cartesian INSERT...SELECT projection/source-predicate cases, for 419 positive DML cases total.

## Server-boundary grammar bombardment

Compiler-level syntax acceptance is not sufficient by itself. Server tests keep a separate boundary floor that exercises the production runtime entry points and the F# compiler/native renderer path:

| Boundary | Cases |
| --- | ---: |
| Six-dialect query runtime | 18 |
| Six-dialect INSERT VALUES approval runtime | 18 |
| Six-dialect UPDATE/DELETE row-set preview runtime | 12 |
| Six-dialect INSERT ... SELECT fail-closed runtime | 6 |
| Six-dialect negative query diagnostic boundary | 12 |
| Six-dialect DML policy diagnostic boundary | 12 |
| version-gated native DML RETURNING boundary | 4 |
| **Total** | **82** |

The INSERT VALUES cases verify runtime server-profile capture, metadata target resolution, F# DML compilation, native rendering, immutable payload preview, plan fingerprinting, and approval challenges. UPDATE/DELETE cases additionally execute the generated match query against a real SQLite rowset harness, retain strict primary-key identity, and verify row-set fingerprints. The harness substitutes only the provider-specific read-only transaction bootstrap; it does not bypass DML planning, compilation, matching, or approval semantics.

## Real-provider execution floor

The final syntax gate executes rendered SQL against the repository's real provider integration fixtures rather than stopping at string assertions.

Current CTE execution floor:

| Shape | Provider executions |
| --- | ---: |
| CTE + WHERE + ORDER | 6 |
| CTE body UNION ALL | 6 |
| CTE body JOIN | 6 |
| CTE body GROUP BY / HAVING | 6 |
| CTE + correlated EXISTS | 6 |
| CTE + window function | 6 |
| chained multi-CTE dependency | 6 |
| dialect-native CTE syntax | 6 |
| nested inner-WITH CTE (PostgreSQL / MySQL) | 2 |
| dialect-native CTE paging | 6 |
| CTE referenced inside subquery | 6 |
| CTE joined with physical table | 6 |
| root UNION ALL over CTE | 6 |
| version-gated recursive CTE (MySQL / SQLite / Firebird) | 3 |
| **Total** | **77** |

These tests use each provider's existing integration fixture and the raw SQL path through `SqlCoreFacade.CompileQuery` and `CompiledSqlCommandExecutor`. The harness captures the open connection's verified runtime server profile before compilation. When the declared source dialect is the same as the connected target provider, that verified profile is reused as both source and target capability proof; cross-dialect input receives target proof only and cannot borrow target runtime facts to authorize source semantics.

## Negative grammar bombardment

`sql-negative-syntax-contract.json` is the curated fail-closed corpus. Every case declares and verifies:

- failure stage,
- exception boundary,
- typed diagnostic code,
- typed diagnostic stage,
- typed diagnostic category,
- concrete source span.

Generated mutation tests additionally place malformed or wrong-dialect syntax in nested positions such as CTE bodies, scalar subqueries, and set branches. Quoted-function negative matrices add five non-PostgreSQL source-dialect capability rejections plus twenty cross-provider target-capability rejections across the positive identifier/context matrix. Recursive CTE mutations add 23 typed-binding cases covering anchor self-reference, duplicate/nested-only self-reference, invalid recursive set shape, non-PostgreSQL portable-subset violations, and Firebird's UNION ALL requirement. DML mutation tests cover parser failures, source-capability failures, and policy denial.

A negative case must fail at the intended compiler boundary. A later failure is not equivalent to an earlier, more precise rejection.

## Maintenance rules

1. Legal syntax that the compiler can represent and prove safely should be added, not permanently denied because an older version lacked it.
2. If a generated legal case fails, fix the compiler/lowering or fix an invalid test construction. Do not delete the case merely to restore green CI.
3. Renderer assertions should verify semantics, identifiers, capability-specific lowering, and parameters rather than brittle source-text preservation.
4. New dialect/version/session capabilities should add positive cases and matching negative boundary cases where appropriate.
5. Generated case-count tests are minimum floors. Increasing them is expected; decreasing them requires explicit review.
6. Keep source-dialect legality, semantic validation, target capability rejection, and policy denial distinct in typed diagnostics.
