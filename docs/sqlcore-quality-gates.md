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
- [x] Add optional immutable portable type metadata with proven incompatible
      comparisons, arithmetic and DML assignment rejection; include schema digest
      in plan/evidence fingerprints.
- [x] Propagate proven column type families through ordinary CTE and derived
      projections, sequential CTE references, computed aliases, wildcard ordering,
      scalar subqueries, and IN subqueries; keep conflicts unproven rather than guessing.
- [ ] Expand provider-exact type coercions, function overloads, nullability,
      typed CTE/derived projections, recursive CTE, NATURAL/USING coalescing
      and DML conflict/MERGE key proofs.
- [x] Establish PostgreSQL 16, MySQL 8 and SQLite execution-result parity
      for seven portable SELECT scenarios over identical seeded fixtures.
      All nine source/target combinations are compiled and executed per
      scenario (63 compiled executions), comparing ordered rows and NULL
      semantics to a PostgreSQL native baseline.
      CI requires both PostgreSQL and MySQL; local runs explicitly skip if absent.
- [x] Add transactional real-provider INSERT/UPDATE/DELETE parity for the
      PostgreSQL / MySQL / SQLite 3x3 source-target matrix: 5 portable DML
      scenarios x 9 compilation paths (45 executions). Assert affected-row
      counts, ordered row state, NULL writes, zero-match deletes and rollback.
      Also verify duplicate-key failure rolls back an earlier statement for
      all three provider engines.
- [x] Add typed-schema SqlCore native compile + actual execution assertions to
      the shared strategy integration fixture. This exercises PostgreSQL,
      MySQL, SQLite, SQL Server 2022, Oracle XE 21, and Firebird using their
      existing databases without launching duplicate fixtures. Native execution
      is **not** evidence of cross-dialect equivalence.
- [x] Add six-source × six-target real-provider executions for four
      narrowly portable literal-free SELECT shapes, reusing existing
      PostgreSQL, MySQL, SQLite, SQL Server, Oracle and Firebird fixtures:
      144 cross-dialect executions, deterministic row-order/value assertions.
      Not a claim of complete six-provider semantic equivalence.
- [ ] Extend real-provider differential execution beyond the narrow six-provider
      intersection: provider versions/session settings, parameter binding,
      DML capability intersections and affected-row behavior.
- [ ] Expand capability evidence across temporal arithmetic, UPSERT, joined DML,
      RETURNING/OUTPUT, NULL and collation rules without speculative lowering.
- [x] Add fail-closed lexical budgets before AST allocation:
      4,194,304 UTF-16 code units per SQL input and 131,072 tokens,
      with source-span, Lexical-stage typed diagnostics.
- [ ] Add a bounded AST/parser recursion depth and stage-wise parse,
      binding, validation and rendering latency/allocation benchmarks across
      nested and adversarial input sizes.
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
