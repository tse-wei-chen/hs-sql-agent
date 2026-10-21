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
- [x] Check explicit INSERT target columns against INSERT ... SELECT projection
      width and proven type families (including propagated derived/CTE types).
      Reject incompatible families without inventing provider-specific implicit
      conversions. INSERT without an explicit target-column list remains unproven.
- [x] Propagate proven column type families through ordinary CTE and derived
      projections, sequential CTE references, computed aliases, wildcard ordering,
      scalar subqueries, and IN subqueries; keep conflicts unproven rather than guessing.
- [x] Preserve JOIN ... USING coalesced unqualified column resolution and
      ordered wildcard projections in strict schema mode, without hiding
      independent qualified columns. Propagate matching known type families
      through merged columns and keep unequal/unknown families unproven.
      Native USING coverage spans PostgreSQL, MySQL, SQLite, Oracle, Firebird;
      SQL Server remains source/target capability-rejected.
- [x] Derive NATURAL JOIN implicit merged keys from the schema-backed left
      row shape and new right source, including the empty intersection,
      nested joins, wildcard expansion and CTE/derived propagation.
      Keep qualifier-specific physical columns independent and reject
      ambiguous keys. Source/target capabilities remain unchanged:
      PostgreSQL, MySQL, SQLite and Oracle use native NATURAL JOIN;
      SQL Server and Firebird remain fail-closed.
- [x] Enforce a fail-closed **commit-time freshness boundary** for any compiled
      DML command that carries `sqlcore.schema_snapshot` assurance: single
      and atomic-batch commit verify the live schema digest through an explicit
      host verifier on the same database connection and transaction **before
      each mutation**. No verifier, missing digest, or a changed digest rejects
      the commit and rolls back; ordinary commands without a schema proof keep
      their existing runtime behavior. The host may register
      `IDmlSchemaSnapshotSource` and `IDmlSchemaSnapshotFreshnessVerifier` in
      DI: the first captures a snapshot during preview compilation, and the
      second obtains fresh metadata inside the commit transaction. Both must
      be provided for strict snapshot execution. `SqlSchemaSnapshot.Sha256`
      shares the exact canonical digest algorithm with the F# compiler, so
      host implementations must hash freshly captured snapshots rather than
      copy an expected digest. This is **not automatic database schema
      discovery**; the default runtime without both registrations remains
      snapshot-free and does not claim production-wide schema freshness.
- [x] Add transaction-enlisted DML planning metadata readers for all six providers
      by extending their existing catalog queries, preserving the old non-transactional
      interface and preventing commit metadata from escaping the active transaction.
- [x] Provide opt-in built-in `NativeDmlSchemaSnapshotService` for all six DBs,
      wired with `services.AddHsSqlAgentNativeDmlSchemaFreshness()`.
      It supports single physical-target INSERT VALUES, UPDATE and DELETE,
      verifies complete schema/table resolution, and rejects ambiguous objects
      and unsupported multi-source shapes. The snapshot's deterministic native
      metadata version records actual column names, provider data types, primary
      key membership and key ordinals; native type changes remain detectable
      even though the compiler uses conservative Unknown portable type kinds.
      It re-reads matching metadata through the transaction-bound reader before
      mutation. Host-supplied source/verifier remains supported, and no automatic
      opt-in is imposed on existing deployments.
- [x] Restore Firebird native DML target semantics exposed by the six-provider
      end-to-end approval test. Its `Default` catalog schema is **not** a SQL
      identifier namespace: DML planning renders the unqualified relation name,
      while the native Snapshot still binds `Default` into its metadata digest
      and uses it when reading transaction-bound catalog rows. The real-provider
      approval gate asserts the emitted SQL contains no `Default.` qualifier.
- [x] PostgreSQL native commit now obtains an **ACCESS SHARE** lock on the
      schema-qualified, safely quoted physical target **before** reading the
      transactional catalog. The lock lasts until commit/rollback and blocks
      ALTER/DROP's ACCESS EXCLUSIVE lock, closing this provider's final
      schema-read-to-DML-write DDL window. Validate that transaction.Connection
      is the exact supplied connection before executing any provider lock or
      metadata query, so cross-connection calls fail in the shared guard first.
      A real PostgreSQL concurrency test
      asserts ALTER times out while the guarded transaction is held, succeeds
      after rollback, and changes the schema digest. This specific lock
      guarantee is **not** attributed to SQLite, Oracle, or Firebird
      without comparable proofs.
- [x] Add opt-in native **MySQL metadata-lock fencing** before commit-time
      catalog attestation. A transaction-bound table SELECT acquires a metadata
      lock retained until commit/rollback; deliberately do **not** issue
      `LOCK TABLES` (which can implicitly commit a user transaction).
      Integration tests use MySQL 8 with both empty and nonempty tables:
      concurrent ALTER must reach metadata `lock_wait_timeout`, then succeed
      only after the verifier transaction releases the lock.
- [x] Add opt-in native **SQL Server table-lock fencing** before commit-time
      catalog attestation, using `TABLOCK, HOLDLOCK` within the existing
      Serializable transaction. The table S lock conflicts with DDL's Sch-M
      until commit/rollback. Tests cover empty and nonempty SQL Server 2022
      tables, enforce a bounded `LOCK_TIMEOUT` for concurrent ALTER, and
      assert digest drift after transaction release. **Operational tradeoff:**
      the held S lock can temporarily block other sessions' DML too. Native
      freshness remains opt-in; do not misstate this as a nonblocking guard.
- [x] Fail closed if an approved DML plan contains a Schema Snapshot but
      its compiled mutation command has lost snapshot compile evidence, or
      lacks the corresponding assurance. Single and batch SQLite approval
      regression tests prove no mutation escapes verification.
- [x] Strengthen **SQLite native schema freshness** to bind the target
      table's canonical `main.sqlite_schema` table/index/trigger DDL source
      into a scoped `native-dml-schema-v2` digest, in addition to the prior
      physical column/PK inventory. Both preview and commit inspect the same
      object set on their respective connection/transaction; missing or
      unreadable table/trigger definition fails closed. This detects SQLite
      `DEFAULT`, `CHECK`, `NOT NULL`, generated-column, explicit index
      and trigger changes even when visible column names/types/PK do not move.
      DDL for unrelated tables is excluded to prevent false invalidations.
      SQLite-only v2 is intentional; the other five providers remain on v1.
      Tests cover rebuilt equal-visible-column tables, index and trigger
      changes causing a real approved INSERT to be rejected, and isolation
      from unrelated DDL. This does **not** claim coverage of every SQLite
      external FK, view, session setting, or cross-table trigger dependency.
- [ ] Expand provider-exact type coercions, function overloads, nullability,
      typed CTE/derived projections, recursive CTE, provider-specific
      join key coercions and DML conflict/MERGE key proofs.
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
- [x] Exercise native DML Schema Snapshot Freshness against all six existing
      real-provider strategy fixtures, not only mocked metadata and SQLite:
      each provider must capture a live catalog, attest its digest using the
      exact open transaction, reject a transaction from another connection,
      and detect an added column on an isolated physical table. This is
      **12 real-provider tests** reusing pinned provider fixtures.
- [x] Add a six-provider **end-to-end approved INSERT VALUES** gate to the
      existing real-provider strategy fixture: a native compiled schema-bound
      plan goes through Preview and Commit successfully without DDL, then
      changing the same isolated table's DDL after a second Preview must
      reject that old approval before execution. Assert the committed row
      count remains unchanged. This gate exercises coordinator, challenge,
      provider transaction and native metadata together; it does not prove
      each DB prevents concurrent DDL after the freshness read.
- [ ] Extend real-provider differential execution beyond the narrow six-provider
      intersection: provider versions/session settings, parameter binding,
      DML capability intersections and affected-row behavior.
- [ ] Expand capability evidence across temporal arithmetic, UPSERT, joined DML,
      RETURNING/OUTPUT, NULL and collation rules without speculative lowering.
- [x] Add fail-closed lexical budgets before AST allocation:
      4,194,304 UTF-16 code units per SQL input and 131,072 tokens,
      with source-span, Lexical-stage typed diagnostics.
- [x] Parse arbitrary prefix-NOT chains iteratively and canonicalize
      double-negation with a SQL three-valued-logic proof, retaining UNKNOWN
      semantics and source spans. Six-dialect tests cover 2,049 prefixes,
      null checks, and nested AND/OR predicates.
- [x] Eliminate recursive Parser calls for structurally redundant grouping
      parentheses by precomputing matching delimiters iteratively and
      consuming pure wrapper chains in one expression parse. Regression
      coverage includes 2,048 wrappers across six dialects and nested scalar
      subquery / non-redundant arithmetic controls.
- [x] Reject unbalanced parentheses during lexical scanning rather than
      recursing through tens of thousands of unmatched opening groups;
      six-dialect tests assert typed lexical diagnostic codes at 25,000 opens.
- [x] Eliminate quadratic intermediate SQL string construction in
      rendering left-deep AND/OR chains with a StringBuilder and iterative
      left-spine walk. Preserve exact parentheses, source operand order, and
      parameters without boolean reassociation. Six-dialect regressions cover
      mixed operator precedence and 512-term chains.
- [x] Render narrow single-projection nested scalar SELECT wrapper chains in
      linear SQL-string space using an iterative wrapper walk and StringBuilder.
      Preserve parameter ordering, Oracle/Firebird dummy FROM, nontrivial
      aliases and non-simple query fallback. Add all-six-dialect 128-level
      in-process regressions and separate all-six-dialect 2,048-level
      subprocess probes (Stack Overflow must not kill xUnit). CI prints
      allocation/exit-status evidence per shape; compare the prior 85.60 MB
      allocation baseline only after measurement.
- [x] Add RuntimeHelpers.TryEnsureSufficientExecutionStack checks at major
      recursive parser, binder, normalizer, schema verifier and renderer entry
      points; fail with SQL_COMPILER_STACK_RESERVE and Resource diagnostic
      instead of introducing an arbitrary MaxAstDepth. This is a last-resort
      safeguard, not a guarantee of unlimited recursion on small threads.
- [x] Add a **gated** constrained-stack safety matrix: 256 KiB managed
      threads run 512/2048-deep nested scalar SELECT and left-deep AND across
      PostgreSQL, SQL Server, Oracle and Firebird in independent processes.
      All 16 cases returned the typed SQL_COMPILER_STACK_RESERVE resource
      diagnostic with zero crashes/timeouts. The matrix accepts either normal
      compilation or this typed fail-closed result; any other failure or crash
      fails the CI syntax-matrix job. This test is about host process safety,
      not an arbitrary SQL nesting-depth limit.
- [ ] Characterize remaining nested subqueries, expression trees, and AST
      traversals with isolated subprocess probes and stage-wise measurements
      before choosing any recursion budget.
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
