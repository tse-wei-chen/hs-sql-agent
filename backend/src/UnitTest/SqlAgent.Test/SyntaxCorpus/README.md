# SQL syntax corpus contracts

These corpora are merge contracts for the SQL compiler rewrite. They are not a list of syntax that the compiler is permanently limited to.

## Compatibility floors

- `sql-main-compatibility-floor.json` is the curated historical compatibility floor.
- `sql-generated-compatibility-floor.json` is the generated six-dialect query grammar floor.
- `sql-generated-dml-compatibility-floor.json` is the generated six-dialect common DML floor.
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
| **Total** | **4485** |

The matrices exercise parsing, query facts/binding, validation, compilation, and native rendering. Assertions are semantic and renderer-aware: source spelling is not required to survive when a target renderer intentionally lowers it to an equivalent native form.

The DML positive floor contains 42 common six-dialect cases plus 17 explicit native/profile/assurance-gated capability cases.

## Negative grammar bombardment

`sql-negative-syntax-contract.json` is the curated fail-closed corpus. Every case declares and verifies:

- failure stage,
- exception boundary,
- typed diagnostic code,
- typed diagnostic stage,
- typed diagnostic category,
- concrete source span.

Generated mutation tests additionally place malformed or wrong-dialect syntax in nested positions such as CTE bodies, scalar subqueries, and set branches. DML mutation tests cover parser failures, source-capability failures, and policy denial.

A negative case must fail at the intended compiler boundary. A later failure is not equivalent to an earlier, more precise rejection.

## Maintenance rules

1. Legal syntax that the compiler can represent and prove safely should be added, not permanently denied because an older version lacked it.
2. If a generated legal case fails, fix the compiler/lowering or fix an invalid test construction. Do not delete the case merely to restore green CI.
3. Renderer assertions should verify semantics, identifiers, capability-specific lowering, and parameters rather than brittle source-text preservation.
4. New dialect/version/session capabilities should add positive cases and matching negative boundary cases where appropriate.
5. Generated case-count tests are minimum floors. Increasing them is expected; decreasing them requires explicit review.
6. Keep source-dialect legality, semantic validation, target capability rejection, and policy denial distinct in typed diagnostics.
