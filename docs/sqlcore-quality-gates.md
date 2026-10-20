# SqlCore quality gates — toward measurable 100%

The goal is **100% passing, independently verifiable acceptance gates**, not a claim that every SQL
dialect feature or security threat is solved. The five subjective review scores must never be
published as measured percentages or silently set to 100.

## Acceptance gates

| Dimension | 100% gate (binary release criterion) | Evidence required |
| --- | --- | --- |
| Architecture / type safety | No forbidden legacy AST dependencies inside parser/binder/normalizer/renderer; compiler stages remain private; CLR public APIs expose no F# union/option/result | Reflection boundary tests and static architecture tests pass |
| Fail-closed security | Every supported DML/policy rule has positive, negative, bypass-authorization and typed-error tests; no known high-severity policy bypass | Six-dialect negative mutation matrix, server-boundary tests, explicit threat inventory |
| Cross-dialect semantics | Every advertised translation pair has declared capability proof and executes against pinned source/target databases with result-level parity | Versioned provider/profile matrix, differential execution fixtures, NULL/type/ordering/affected-row assertions |
| SQL grammar breadth | Every *documented supported* grammar path has a positive test and every *documented rejected* path has typed diagnostics; none of the historical successful cases regress | Generated + curated corpora, main-vs-PR parity, deterministic matrix floors |
| Test engineering | All required checks green, no unexplained skips, no stale intentional-breaking allowlists; deterministic property tests and runtime coverage pass | CI run links, six-provider integration results, performance baseline |

## Stage 1 (this branch)

- [x] Retain the single typed F# compiler pipeline and six-dialect parser/rendering boundary.
- [x] Reject demonstrably always-true UPDATE/DELETE predicates unless full-table authority is explicit.
- [x] Add six-dialect regression cases and runtime-boundary diagnostic tests.
- [x] Add an opt-in, Release-mode compiler performance harness with structured JSON and a PR smoke test.
- [x] Verify first-batch CI jobs, including differential syntax parity, before this round.
- [ ] Verify CI for the new schema-aware stage.

## Next implementation gates (not done in this change)

- [x] Introduce an opt-in immutable metadata snapshot, table/column name resolution,
      ambiguity detection, and strict fail-closed gates for unproven SQL shapes.
- [ ] Extend schema binding with column types, nullability, expressions/functions,
      recursive CTE projection, NATURAL/USING coalescing and DML conflict/MERGE key proofs.
- [ ] Add provider-backed differential **execution** for each cross-dialect translation that
      claims semantic equivalence. Current differential parity compares compiler versions,
      not execution results of all six providers.
- [ ] Expand capability evidence across temporal arithmetic, UPSERT, joined DML,
      RETURNING/OUTPUT, NULL and collation rules without speculative lowering.
- [ ] Add resource-exhaustion limits for deeply nested/long SQL; benchmark parse,
      binding, validation and rendering latency/allocations across input sizes.
- [ ] Collect realistic anonymized application queries and DML scripts as separate
      regression corpora, without lowering the existing generated matrix floors.
- [ ] Distinguish compile-time SQL-policy proofs from server-time transaction,
      row-preview/approval, authorization and runtime resource enforcement.

## Proof model

- A policy denying `WHERE 1=1` is a **known-tautology guard**, not evidence that all
  full-table mutations are impossible. For example, a column-dependent tautology may not
  be statically recognized.
- SQL grammar compilation is not evidence that a query can execute against a particular
  runtime schema.
- A green unit-test matrix is not proof of identical behavior across database versions,
  collations, session settings and trigger configuration.
- Do not merge a feature expansion by shrinking a positive or negative corpus.
- Never treat a skipped CI job as a passing CI job.
