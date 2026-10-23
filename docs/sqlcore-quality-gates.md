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
- [x] Keep the real Oracle schema-bound end-to-end approval test on
      **Serializable** rather than downgrading transaction isolation to avoid
      Oracle `ORA-08177`. Oracle defaults to deferred segment creation for
      new empty tables; the test's own table now specifies
      `SEGMENT CREATION IMMEDIATE`, materializing storage before the first
      approved INSERT. Production Oracle applications using empty,
      segment-less tables may still see a fail-closed serialization error
      until the table segment exists; this is a database constraint, not
      evidence that the schema verifier should bypass the DML transaction.
- [x] Extend Oracle 21c and Firebird native Snapshot to **v2** with
      provider-specific, scoped, transaction-enlisted definition inventory.
      Oracle reads `ALL_TAB_COLS` (including hidden/virtual/identity and
      nullability metadata), `ALL_CONSTRAINTS` / `ALL_CONS_COLUMNS`
      (status, check expression where available, FK delete rules, key members),
      and `ALL_TRIGGERS` / `ALL_SOURCE` (trigger activation and body text).
      Firebird reads `RDB$RELATION_FIELDS` / `RDB$FIELDS` (default/computed
      BLOB TEXT, nullability, identity), `RDB$RELATION_CONSTRAINTS` /
      `RDB$REF_CONSTRAINTS` (constraint and FK actions), and
      `RDB$TRIGGERS` (user-trigger source/status/sequence). Any absent
      required trigger source, unrecognizable catalog value, or Oracle
      potentially truncated 4000-character CHECK condition returns no proof.
      Two real-provider positive/negative DDL fixtures compare the exact
      scoped Snapshot after CHECK and trigger changes. **Boundary:** Oracle
      21c `DATA_DEFAULT` is `LONG`; its full expression text cannot yet
      be safely attested here, and default lengths do not prove equal-length
      default replacements. Firebird system-generated constraint trigger
      bodies and external dependencies are not all attested; do not claim
      full semantic schema coverage. Other providers retain existing versions.
- [x] Verify SQLite active commit transaction locks out a concurrent ALTER
      on a separate connection, releases that lock on rollback, and then
      invalidates the held `native-dml-schema-v2` digest. The test uses an
      isolated file database and bounded busy timeout, not fixture-level
      silent skips.
- [x] Harden the **approval-to-commit security boundary** against schema
      proof downgrades and child-challenge substitution. When a host has an
      `IDmlSchemaSnapshotSource` configured, both single and atomic typed
      runtime commits now reject legacy/no-Snapshot sessions **before** any
      challenge is consumed or database connection opened. Reusing a preview
      from before Strict mode was enabled requires a fresh schema-attested
      preview; opt-out legacy hosts remain supported.
- [x] Remove the atomic coordinator's divergent, less strict copy of
      `ValidatePlan`: it now calls the same validator as single DML
      (operation/command kind, maximum rows, approval mode and match-query
      provider requirements). It additionally binds each child preview to
      its plan's operation, table, policy version, exact plan fingerprint,
      affected-row count, challenge nonce and INSERT payload. Swapped or
      edited child approval objects fail **before opening a commit connection**.
      Added server-side strict-mode downgrade tests and atomic negative
      challenge/plan mutation matrices.
- [x] Bind **every approved DML plan's execution-relevant shape** to its
      one-time challenge, separately from SqlCore's stable mutation-command
      fingerprint. The versioned `dml-approval-plan-binding-v1` SHA-256
      includes physical target, operation, approval mode, policy, row limit,
      approval TTL, Snapshot digest, canonical Match Query command/parameters,
      row-identity assurance and ordered columns, and ordered INSERT preview
      values with deterministic dictionary-key ordering. Single and atomic
      Commit both recompute and require this binding before consuming a
      challenge or connecting to the database. Atomic transaction evidence
      also binds the child's full-plan fingerprint, so a tampered child cannot
      be substituted under a valid batch approval.
      This preserves the existing `DmlFingerprintService.ComputePlanFingerprint`
      contract for the compiled mutation while preventing match-query or
      approval-metadata swaps that leave that older fingerprint unchanged.
      **Backward compatibility:** approvals issued before the extra binding
      was introduced must be re-previewed; no legacy challenge is accepted
      for execution. Added negative tests for changed match SQL/row identities,
      target table, max-row cap, TTL, INSERT displayed values and missing
      binding (including atomic child approvals).
- [x] Replace the **non-atomic batch-approval promotion** sequence
      (`TryConsumeAsync` on each child, then `RegisterAsync` aggregate)
      with `IDmlApprovalChallengeStore.TryPromoteAsync`. The in-memory store
      serializes register/consume/promote using one critical section; the Redis
      store performs compare-all-child-payloads, NX aggregate registration with
      TTL, and child deletes in **one Lua script**. Any expired, missing,
      modified, duplicated or already consumed child, or existing aggregate
      nonce, aborts without partial mutation. A fixed hash tag ensures all
      keys touched by the Redis script share a Redis Cluster slot; this changes
      the Redis approval key namespace, so existing ephemeral challenge
      tokens must be re-previewed after upgrade.
      Existing third-party challenge stores remain binary/source-compatible
      through a default fail-closed method, but **must implement atomic
      promotion to support batch previews**. Cancellation-aware best-effort
      cleanup uses an independent token and does not mask the original error;
      if Redis remains unavailable, child TTL bounds retention.
      Added in-memory promotion/race/cancellation cases, Redis Lua/cluster-slot
      contract tests, and server failure-injection checks for partial preview
      registration and failed promotion without leaked executable approvals.
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
- [x] Start behavior-preserving stack-independent traversal migration:
      common explicit heap-backed scalar walker replaces recursive UPDATE/DELETE
      volatility inspection, INSERT VALUES scope validation and aggregate-order
      row-column checks. Expr.unspan is iterative.
- [x] Replace `Expr.equivalent` recursive structural comparison with
      a typed pair-stack walk: no managed recursion for Unary/Binary,
      functions, CASE, window, list and BETWEEN expression pairs.
      Preserve exact CTE/subquery conservatism, span-insensitivity,
      correlation restrictions and ORDER BY expression matching.
      SQL Server DISTINCT/OFFSET regression covers 16/64/128 nested
      computed expression chains, both equivalent and mismatched.
- [x] Convert query-table policy validation from mutually recursive
      expression/query/source/select functions to an explicit continuation-frame
      machine. Preserve CTE-first, FROM-first, projection/WHERE/JOIN and
      ORDER BY alias-check diagnostic precedence; retain nested subquery
      whitelist verification with six-dialect positive/negative tests.
- [x] Replace recursive parent-scope qualifier/source lookup in Binder
      with iterative ancestry scans. Existing correlated-subquery binding
      behavior and six-dialect 2,048-level nested scalar probes remain gates.
- [x] Use value-type continuation frames and avoid per-node intermediate
      List.map/collect allocations to bound the new policy walk's GC cost.
- [ ] Migrate remaining binder/normalizer/schema/type/semantic and renderer
      recursive paths; compare SQL output, parameter order, diagnostics and
      allocations before claiming arbitrary-depth compiler support.
- [ ] Characterize remaining nested subqueries, expression trees, and AST
      traversals with isolated subprocess probes and stage-wise measurements
      before choosing any recursion budget.
- [ ] Collect realistic anonymized application queries and DML scripts as separate
      regression corpora, without lowering the existing generated matrix floors.
- [ ] Distinguish compile-time SQL-policy proofs from server-time transaction,
      row-preview/approval, authorization and runtime resource enforcement.

## Real Redis approval concurrency gate

- [x] Add a dedicated `redis-approval-integration` GitHub Actions job
      with **real Redis 7**, distinct from the existing Mock-based Redis
      contract tests. `server-test` continues to run ordinary unit tests
      without requiring a local Redis service; the dedicated job explicitly
      provides and requires the Redis connection.
- [x] Execute concurrent `TryPromoteAsync` calls from independent store
      instances with 12 competing aggregate challenges for the same pair
      of children. Verify exactly one aggregate wins, all child keys are
      removed, losing aggregates never materialize, and two simultaneous
      consumers cannot both consume the winner.
- [x] Prove mismatched child payloads or a pre-existing aggregate key leave
      all children intact; verify an expired child is not promotable and
      real Redis TTL deletes its key.
- [x] Race a single child approval consume against aggregate promotion
      across independent stores for ten rounds. Assert exactly one winner:
      when single consume wins, no aggregate exists and the other child
      remains; when aggregate promotion wins, both children disappear and
      the aggregate is one-time. This guards against double authorization.
- [x] Inject a **lost client acknowledgement after actual Redis Lua execution**
      for both aggregate promotion and single-challenge consumption.
      Confirm that neither retry nor independent store consumption
      resurrects child approvals. The aggregate remains one-time. An
      ambiguous network failure is **not** treated as proof of failure:
      the server may already have promoted the challenge, so callers
      fail closed, rely on aggregate TTL, and request a fresh preview.
      This is single-server Redis 7 concurrency evidence, not a claim of
      Redis Cluster failover or cross-region exactly-once execution.

## DML commit acknowledgement and replay boundary

- [x] Treat an exception from the database's actual `COMMIT` call as
      **outcome unknown**, never as proof of rollback. Single-statement
      UPDATE/DELETE and INSERT VALUES as well as atomic batches now throw
      `DmlCommitOutcomeUnknownException` with the original provider exception
      as `InnerException`. The approval has already been consumed and must
      **not** be replayed; operators must inspect the actual database state
      before issuing a new Preview.
- [x] Rollback cleanup no longer inherits the possibly-cancelled user
      request token. It gets its own bounded 10-second cancellation window,
      and a rollback error is retained under
      `Exception.Data["HsSqlAgent.DmlRollbackFailure"]` instead of masking
      the primary DML/COMMIT error. This preserves real provider diagnostics
      if cancellation, connection failure, or a completed/uncertain
      transaction makes rollback fail.
- [x] Add fault-injection unit tests for lost commit acknowledgements,
      cancelled request cleanup, failed rollback diagnostic preservation,
      and clean commit. A test transaction decorator also calls a **real
      SQLite COMMIT**, then injects an acknowledgement loss: the row is
      physically committed, the coordinator's shared helper reports
      `DmlCommitOutcomeUnknownException`, and unsuccessful follow-up
      rollback does not replace the primary error. Separate real SQLite
      DML tests prove a constraint error rolls back without allowing the
      consumed approval to execute again.
      **Boundary:** Redis challenge consumption and external SQL commit are
      separate systems. These changes preserve at-most-once *approval
      consumption*, not a distributed atomic commit or a guarantee that
      external consumers can always know the database's final outcome.

## Commit outcome uncertainty propagated to MCP, HTTP, webhook and Audit UI

- [x] Preserve distinct **outcome_unknown** semantics beyond the transaction
      coordinator. MCP `execute_dml_sql` returns `status: "outcome_unknown"`,
      `error.code: "dml.commit_outcome_unknown"`, `error.stage: "Commit"`,
      `error.retryable: false`, `commitConfirmed: null`, and no speculative
      affected-row count or RETURNING rows. The legacy `committed: false`
      means **not confirmed**, not proof of rollback, under this explicit
      status. Immediate approval flows preserve the actual approval request
      ID and wait duration for investigation.
- [x] HTTP unhandled-commit responses use the same stable machine code and
      status, `committed: null` and `retryable: false` with HTTP 500, while
      unrelated exception payloads retain their existing shape.
- [x] Durable webhook completions store an `OutcomeUnknown` terminal state
      (no schema migration; `Status` is a bounded string) instead of
      `Failed`. The API's `DmlApprovalCompletionStatus.OutcomeUnknown`
      and `errorCode: "dml.commit_outcome_unknown"` are preserved on
      replayed callbacks. The webhook responds 409 Conflict, not 200
      success, and duplicate callbacks cannot reopen or re-execute the
      consumed approval.
- [x] Record `outcome_unknown` as a separate DML audit result; the existing
      Admin Audit Logs page highlights these events distinctly and explains
      why operators must reconcile actual database rows before initiating
      a new approval. Tests cover structured MCP tri-state JSON, HTTP error
      JSON, callback status, durable terminal replay and frontend labels.
      There is **no Admin UI for directly initiating DML commits**; this
      frontend change is to the actual operator-facing audit interface.
- [ ] End-to-end fault-injection against each provider's network transport
      after COMMIT, and a reconciled outcome workflow/ledger, remain outside
      the current scope. The UI must never claim proof of success/failure
      from a lost acknowledgement.

## Durable DML approval audit failure isolation

- [x] The durable webhook approval completion persists its terminal `Executed`
      state **before** writing the audit event. If audit storage is
      unavailable after the target database has already committed, the
      completion still returns `Executed` and the persisted approval remains
      terminal. Audit I/O uses a separate bounded 10-second token; an audit
      exception must not enter the generic failure handler and rewrite an
      actually committed operation to `Failed`.
- [x] Real SQLite durable approval integration test injects an
      `IAuditService.WriteEventAsync` failure after the approved target INSERT.
      It verifies the row exists, terminal state is `Executed`, the completion
      reports success, and restarting the service cannot replay the approval.
- [x] Treat Admin Store terminal `SaveChangesAsync` failure **after a
      confirmed target COMMIT** as a distinct, non-retryable outcome,
      `dml.approval_state_unknown`, rather than overwriting the already
      committed operation with `Failed`. Attempt to persist a terminal
      `OutcomeUnknown` marker using independent bounded cleanup I/O; when
      that second write fails, the previously persisted `Executing` claim
      remains non-replayable across service restarts. A successful terminal
      write may have committed despite an acknowledgement failure, so no
      automatic replay or inference of rollback is made. The returned message
      explicitly states that the **target COMMIT succeeded** while the Admin
      Store status needs reconciliation. Tests inject both a failed terminal
      save and a failed fallback save after a real approved SQLite INSERT,
      checking physical rows, persisted status, error code and restart replay.
      Another fault-injection test lets Admin Store `Executed` **actually commit**
      but loses the SaveChanges acknowledgement afterward. Whether the
      conservative fallback retains `Executed` or records `OutcomeUnknown`,
      the approval remains one-time and the target row is not executed twice.
      `dml.approval_state_unknown` is distinct from
      `dml.commit_outcome_unknown`: the former means the *target COMMIT was
      acknowledged* but Admin Store terminal persistence was not.
      An `Executing` status after failed fallback persistence is an
      operator-reconciliation state, not permission to repeat the write.
- [x] Persist new Admin Store terminal-save uncertainty as distinct
      `ApprovalStateUnknown` (20 characters, fits the existing Status
      column) rather than classifying new records by matching English
      `Reason` text. Legacy `OutcomeUnknown` records remain readable,
      retaining the original non-replayable approval and error codes.
- [x] Add operator-assisted reconciliation for consumed uncertain approvals
      through the `/api/runtime/dml-approvals/reconciliation` Admin API.
      Only the human-authorized `/runtime/security.edit` permission may
      invoke it; this permission is not grantable to Management API keys.
      A bounded read-only inventory hides encrypted execution payloads
      and approval fingerprints. A resolution requires the inspected
      Status/UpdatedAt, an operator, an evidence reference, a written note
      and an explicit attested disposition (committed / not_committed /
      indeterminate). `Executing` items are visible for investigation but
      **cannot be resolved**: their age is not evidence that an in-flight
      worker has stopped. Only terminal `OutcomeUnknown` and
      `ApprovalStateUnknown` are currently eligible. The bounded
      inventory returns explicit `CommitEvidence` and
      `AllowedResolutions` fields for each request. Confirmed target
      COMMIT (including old `OutcomeUnknown` reason encoding) allows only
      `committed`, physical commit uncertainty offers three operator
      attestations, and `Executing` offers no settlement action. The
      classification is performed server-side without exposing protected
      SQL, approval fingerprints or the internal Reason text; it is
      **not** a physical transaction-fencing or independent proof mechanism.
      Inventory `CreatedAt` and `UpdatedAt` values are emitted with an
      explicit UTC designator even when SQLite restores their
      `DateTime.Kind` as Unspecified; resolution requires an ISO timestamp
      carrying UTC or an explicit timezone offset and normalizes it to UTC
      before validating the observed state. Ambiguous timezone-free
      timestamps fail closed rather than guessing the operator's locale.
      The Admin Console exposes a Security-edit-only reconciliation page
      under Runtime Management. It lists bounded, nonsecret facts;
      `Executing` is read-only. Other dispositions require the exact
      request ID, a referenced external evidence artifact, rationale,
      acknowledgement of independent verification, and a server-advertised
      allowed resolution. The page sends the original status and timestamp
      without computing a new claim, re-fetches on concurrency conflict,
      and cannot enable forbidden statuses by modifying the server reply.
      Backend authorization and final state checks remain authoritative.
      The resulting AttestedCommitted / AttestedNotCommitted /
      AttestedIndeterminate statuses are permanently non-replayable;
      the prefix makes clear these are human attestations, not independent
      compiler or physical COMMIT proofs. A confirmed target COMMIT
      (`ApprovalStateUnknown` or its legacy `OutcomeUnknown` encoding)
      can only be attested as committed; operator input cannot contradict
      a previously acknowledged database COMMIT.
      Status transition, an operator-attributed AuditLog and (when SIEM
      integration is enabled) its durable outbound delivery are committed
      in a single Admin Store transaction, protected by optimistic status
      concurrency. The ordinary audit redactor is applied to free-form
      operator evidence before storing the attestation or SIEM payload.
      Notification failure after successful persistence does not reset
      the approval. The ordinary AuditService outbox writer also isolates
      delivery-signal failure after acknowledged SaveChanges, preventing
      phantom retry/fallback of a successfully persisted audit event.
      No target database connection or mutation is performed.
      An attestation is **not** cryptographic proof of transaction outcome
      or a distributed atomic commit; operators must independently
      investigate physical effects first.
- [x] Separate COMMIT acknowledgement uncertainty from failure to confirm
      a ROLLBACK after DML error. The shared transaction recovery layer now
      emits `DmlRollbackOutcomeUnknownException` when a rollback attempt also
      fails, preserving the original SQL exception and rollback failure.
      Both single and atomic DML coordinators propagate this typed result
      rather than turning it into a definitive SQL failure.
      Durable approvals record a distinct `RollbackOutcomeUnknown`
      status (within the existing Status length), return
      `dml.rollback_outcome_unknown`, and never re-execute the consumed
      approval after restart. The authenticated operator inventory classifies
      it as `RollbackUnconfirmed` with conservative evidence-based choices.
      An already-unknown COMMIT remains `DmlCommitOutcomeUnknownException`
      even when secondary rollback cleanup also fails. The scope of this
      gate is process/provider rollback acknowledgement, **not** proof of
      the physical outcome in a disconnected database.
- [x] Preserve acknowledged target COMMIT results even if a provider's
      transaction or connection `DisposeAsync` later fails. Both single
      and atomic DML coordinators capture a confirmed result before
      exiting the provider cleanup scope and do not convert secondary
      cleanup exceptions into a failed mutation. Real SQLite INSERT
      tests inject a connection disposal failure *after* the underlying
      connection has been disposed and assert a successful commit result
      and exactly one durable target row in both code paths. COMMIT
      acknowledgement loss still remains `DmlCommitOutcomeUnknownException`;
      no cleanup exception is allowed to invent a successful COMMIT.
- [x] Prevent stale reconciliation from overwriting a newer, same-status
      commit-evidence record. The Admin Store concurrency contract now
      compares `Status`, `Reason` and `UpdatedAt` atomically; a legacy
      `OutcomeUnknown` record updated to encode an acknowledged COMMIT
      cannot be overwritten by an operator using a stale, previously
      unknown-commit snapshot. Two real SQLite context-interleaving tests
      cover same-status Reason-only and UpdatedAt-only writes, verifying
      the losing transaction rolls back both audit and SIEM outbox.
      These concurrency tokens alter EF predicates, not table schema.
- [x] Preserve primary SQL/transaction failure classification when
      provider `DisposeAsync` throws during stack unwinding. Single and
      atomic DML commit paths carry the original failure via
      `ExceptionDispatchInfo`, prioritizing confirmed physical COMMIT,
      then typed COMMIT/ROLLBACK uncertainty, then the actual SQL failure
      over a secondary cleanup error. Real SQLite cases verify
      duplicate-key mutation failures are not masked by a connection
      disposal error, with the batch still rolled back. This is not
      a substitute for target-enlisted commit evidence or worker fencing.
- [x] Introduce an **opt-in, target-enlisted positive COMMIT witness** for durable
      DML. The physical target database must be provisioned with a unique
      `HS_SQL_AGENT_DML_WITNESS.REQUEST_ID` primary key, INSERT/SELECT privileges
      and transactional storage. With
      `builder.AddHsSqlAgentTargetCommitWitness()`, durable approvals pass
      their persisted request ID and evidence fingerprint through TypedDmlRuntime.
      Both single and atomic batch coordinators write the witness on the exact
      open target DbConnection/DbTransaction **after** mutation revalidation and
      **before** COMMIT; missing table, invalid fingerprint, duplicate request ID,
      write failure or no registered writer fail closed. SQLite real-database
      tests cover successful commit, same-transaction failure rollback, duplicate
      request rejection and end-to-end durable approval binding after restart.
      The read method observes an independently committed fingerprint but
      does not make negative reads authoritative. This remains disabled by
      default and does **not** change existing six-provider capabilities.
      See [target-enlisted COMMIT witness](dml-target-commit-witness.md).
      Other five providers' live DDL/ADO transport interoperability and
      independent multi-worker safety require additional integration evidence;
      do not infer worker quiescence from a missing witness.
- [x] Add a read-only, permission-gated target witness inspection endpoint
      and Admin Console investigation action. The service first validates
      the persisted encrypted approval's HMAC-bound original target
      connection identity, database ID, provider and name; only then may
      it open a target connection for a bounded independent witness read.
      Results distinguish `CommittedWitnessMatch`, `NotObserved`,
      `EvidenceMismatch`, `TargetConfigurationChanged`,
      `WitnessNotConfigured`, `ReadUnavailable` and `ApprovalChanged`.
      API responses contain no SQL, connection string, credential or
      evidence hash. `NotObserved` cannot settle `Executing` or authorize
      a retry. A matching positive witness prevents contradictory human
      `not_committed` and `indeterminate` attestations server-side.
      Add real SQLite restart, configuration-drift, forged-witness, missing
      witness and read-failure tests and verify the GET route is restricted
      to human `/runtime/security.edit`. This still is **not** worker
      fencing or six-provider live witness parity.
- [x] Implement the **opt-in, version-stamped target admission/fence kernel**
      before mutation execution: `target-fence-v1` is sealed into the
      encrypted durable approval payload at issuance. Both single and atomic
      DML coordinators must INSERT a unique `Admitted` row into a
      separately provisioned `HS_SQL_AGENT_DML_FENCE` **at the start of
      the same physical target transaction**, before revalidation or SQL.
      Independent `RevokeAsync` competes for the same unique request ID,
      leaving a durably COMMIT-acknowledged `Revoked` row. Whichever
      COMMITs first excludes the other; conflicts, cancellation and
      uncertain COMMIT are not negative proof. All mutation, admission and
      positive COMMIT witness rows succeed/rollback atomically. This adds
      neither a default feature nor any reconciliation of `Executing`.
      Real SQLite tests cover revocation-first, worker-first, failed SQL,
      restart and missing DI fail-closed for both DML execution routes.
      See [target execution fence protocol](dml-target-execution-fence.md).
      **Still open:** fleet-version enforcement, supervised revocation API,
      six-provider real unique-index/transaction semantics and admission
      race fault injection before `Executing` can be resolved.
- [x] Exercise real **PostgreSQL 16 and MySQL 8 target-fence/witness
      transactions in CI** (separate from the SQLite model): isolated
      PostgreSQL schemas and MySQL InnoDB databases; direct ADO admission,
      revocation and COMMIT-witness writes; independent target connections;
      revocation-first, worker-first, rolled-back admission, absent witness
      rollback and concurrent revoker vs uncommitted admitted worker.
      An additional 2-provider × 2-execution-path × 2-outcome integration
      matrix exercises the actual compiled single-DML/atomic-batch coordinators:
      a committed revocation must block all application SQL; a committed
      mutation must persist both fence admission and target witness with
      its application row. The CI job requires `TEST_POSTGRES_CONNECTION` and
      `TEST_MYSQL_CONNECTION`; it fails rather than silently skipping
      when those values are missing. Oracle/Firebird provider-level
      fencing is now additionally exercised via the six-provider shared
      real-database fixture; fleet-version validation, controlled
      revocation/reconciliation and authoritative topology proof remain open.
- [x] Add a separate mandatory SQL Server 2022 target-fence CI shard:
      nine actual SQL Server cases prove direct admission/revocation
      competition, rollback, missing-witness rollback, and a revoker
      blocked by an uncommitted worker. Both compiled single-statement
      and atomic-batch coordinators must atomically commit mutation,
      admission, witness, or reject pre-revoked execution. The first
      live service run passed 9/9 with zero skips. The SQL Server
      suite is excluded from other shards without its target service.
- [x] Seal the **commit-witness requirement** independently of the versioned
      target admission fence into every newly issued durable approval when
      witness opt-in is active. A restart with the witness writer removed
      now fails closed before any target SQL, rather than silently
      committing without evidence. Older protected resume payloads retain
      compatible deserialization. Add a real SQLite restart matrix for
      witness-present and witness-removed completions, consumed approval
      non-replayability, and target witness row atomicity.
- [x] Preserve target revocation's physical COMMIT classification across
      provider transaction DisposeAsync failures. Acknowledged revocation
      COMMIT remains successful even if cleanup fails; unacknowledged COMMIT
      remains a typed `DmlCommitOutcomeUnknownException`, and failed
      mutation plus unconfirmed rollback remains a typed
      `DmlRollbackOutcomeUnknownException`. Fault-injection tests cover
      all three branches. Cleanup exceptions cannot accidentally authorize
      an operator to infer successful revocation or definite failure.
- [x] Validate provider-native admission, revocation-first denial,
      mutation/witness COMMIT atomicity and rollback release in all six
      physical database fixtures. Add a concurrent independent revoker
      against an uncommitted worker for the same six providers and verify
      no premature revocation acknowledgement. The same real-driver
      corpus runs in the existing integration shard with Oracle and Firebird
      containers, not a mocked provider or optional local-only test.
      This proves selected database-transaction behavior, **not** fleet
      enrollment or authoritative worker quiescence.
- [x] Reject pre-fencing Pending approvals when resumed by a fence-enabled
      worker, including approvals previously protected only by a COMMIT
      witness. Require a newly issued versioned preview/approval rather
      than silently executing without target admission. This does not
      change the behavior of unupgraded binaries, so mixed fleets are
      still unsafe.
- [ ] Prove worker quiescence with an actual fencing mechanism before
      permitting reconciliation of `Executing` approvals. A timeout or a
      timestamp alone must never authorize that transition.
      External proof verification is also still open.

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
