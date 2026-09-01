# SQL syntax corpus contracts

These corpora are merge contracts for the SQL compiler rewrite. They are not a list of syntax that the compiler is permanently limited to.

## Compatibility floors

- `sql-main-compatibility-floor.json` is the curated historical compatibility floor.
- `sql-generated-compatibility-floor.json` is the generated six-dialect query grammar floor.
- `sql-generated-dml-compatibility-floor.json` is the generated six-dialect common DML floor.
- `sql-generated-dml-predicate-compatibility-floor.json` is the six-dialect Cartesian UPDATE/DELETE predicate and assignment floor.
- `sql-generated-recursive-compatibility-floor.json` is the version-aware recursive CTE grammar floor for PostgreSQL, MySQL, SQLite, and Firebird.
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
| **Total positive query floor** | **4613** |

The recursive matrix additionally cross-products four proven native providers, four legal recursive-member shapes, four root consumers, and root ordering, with explicit source/target server-version proofs. The matrices exercise parsing, query facts/binding, validation, compilation, and native rendering. Assertions are semantic and renderer-aware: source spelling is not required to survive when a target renderer intentionally lowers it to an equivalent native form.

The DML positive floor contains 42 common six-dialect cases, 17 explicit native/profile/assurance-gated capability cases, and 180 Cartesian UPDATE/DELETE predicate/assignment cases, for 239 positive DML cases total.

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

Generated mutation tests additionally place malformed or wrong-dialect syntax in nested positions such as CTE bodies, scalar subqueries, and set branches. Recursive CTE mutations add 23 typed-binding cases covering anchor self-reference, duplicate/nested-only self-reference, invalid recursive set shape, non-PostgreSQL portable-subset violations, and Firebird's UNION ALL requirement. DML mutation tests cover parser failures, source-capability failures, and policy denial.

A negative case must fail at the intended compiler boundary. A later failure is not equivalent to an earlier, more precise rejection.

## Maintenance rules

1. Legal syntax that the compiler can represent and prove safely should be added, not permanently denied because an older version lacked it.
2. If a generated legal case fails, fix the compiler/lowering or fix an invalid test construction. Do not delete the case merely to restore green CI.
3. Renderer assertions should verify semantics, identifiers, capability-specific lowering, and parameters rather than brittle source-text preservation.
4. New dialect/version/session capabilities should add positive cases and matching negative boundary cases where appropriate.
5. Generated case-count tests are minimum floors. Increasing them is expected; decreasing them requires explicit review.
6. Keep source-dialect legality, semantic validation, target capability rejection, and policy denial distinct in typed diagnostics.
