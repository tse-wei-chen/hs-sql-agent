# U6 — Selective Script Execution Proof decision

**Decision: NO-GO for U7; keep user-authored TCL + DML scripts Analyze-only.**

- Decision date: 2026-11-17
- Review baseline: `develop` at `7d1f66f91f0c97c93d0217ca6c79d3cc7664fcd9` (U5 / #655)
- Owner boundary: SqlCore proves *static meaning*; Server owns physical connections, admission, approval, and COMMIT/ROLLBACK
- Applies to: **new executable caller-authored TCL envelopes**, including `BEGIN; ...DML...; COMMIT`, `SAVEPOINT`, and `ROLLBACK TO`. It does not turn off or relabel any existing DQL, DML, or DDL execution path.
- This is a **decision**, not a `ScriptExecutionPolicy` or a new public negative compiler API. No Script execution capability is implemented in this PR.

## 1. Verified baseline

| Existing boundary | What is implemented | What it **does not** prove |
| --- | --- | --- |
| U2 / #652 `StaticDependencyEdges` | Ordered, typed Virtual Catalog dependencies for verified physical names | Live object existence, locks, row set, effective permissions, execution, or complete runtime dependencies |
| U3 / #653 `DmlEffectReconciliations` | Modeled savepoint and rollback expectations, with `Pending` / `StaticRollbackExpected` / `StaticCommitBoundary` / `Unproven` | Execution attempt, actual server rollback, trigger/sequence/external side-effect rollback |
| U4 / #654 `CompositionStatus` | Typed mixed-family risk and Unknown propagation; `IsCompositionExecutionProof=false` for **all** outcomes | Eligibility or authorization for Host script execution, even when statements are individually valid |
| U5 / #655 DDL Host | Revocable admission check before native DDL, with provider-specific unknown-commit handling | A general mixed DDL+DML transaction or six-provider network-/split-brain-safe COMMIT proof |
| Existing `execute_dml_sql` | Bounded ordered DML-only batch, with a **Host-owned** physical transaction, aggregate approval, child row-set/payload fingerprints, rechecks and unknown-commit classification | Accepting caller `BEGIN` / `SAVEPOINT` / `COMMIT`, or approval for arbitrary cross-family scripts |
| Existing `McpBuiltInTools` | Six known built-in tools; four read/query defaults, DML and DDL not selected by default | A generic `execute_script` capability or inherited DML/DDL approval |

The existing `CoreDmlBatchTextParser.ParseDmlBatch` rejects TCL. This is an intentional safety contract: a semicolon-delimited **DML-only** batch is not an arbitrary user-controlled transaction script. The Host, not the source SQL, owns its transaction boundaries.

## 2. Candidate topology versus demonstrated value

Candidate **A**: PostgreSQL `BEGIN; DELETE FROM order_details WHERE order_id = 7; DELETE FROM orders WHERE id = 7; COMMIT;`.

**Verdict: NO-GO as a new execution capability.** The core business requirement (two approved deletes in one atomic Host transaction) is already served by passing the two supported DELETE statements to `execute_dml_sql` **without** caller-provided TCL. The existing path still requires physical row-set approval and recheck. Introducing an envelope parser / Script approval mode would enlarge the security and product surface without evidence of a missing capability.

Candidate **B**: PostgreSQL `BEGIN; UPDATE...; SAVEPOINT...; DELETE...; ROLLBACK TO...; COMMIT`.

**Verdict: NO-GO.** U3 can statically identify the deleted row effect as expected to be rolled back, but does not prove the effects of triggers, sequences, functions, external I/O or other non-transactional actions. Exposing this shape would also require explicit approval semantics for rows touched **and rolled back**, not merely net final effects, plus an authenticated native savepoint orchestration and lost-ACK contract. None of those is a public Script runtime today.

Candidate **C**: DQL+DML, DDL+DML, DCL / Utility / Procedural or multi-batch SQL.

**Verdict: NO-GO.** The U4 classification intentionally separates their security domains and can report `Unknown`. A verified Virtual Catalog relation cannot substitute for a live row-identity proof or grant union.

Other providers cannot inherit the PostgreSQL candidate's transaction semantics. MySQL/Oracle DDL implicit COMMIT, SQL Server batches, SQLite savepoint behavior and unmodeled provider effects demand distinct, actual evidence.

## 3. Required executable Script proof (all must pass; current verdict)

| Gate from architecture v3.1 §5.6 | Evidence required | U6 finding |
| --- | --- | --- |
| 1. Typed statement semantics | Every statement has closed, validated, supported semantics; no unknown/partial effects | **Not proven** for arbitrary caller TCL+DML; only bounded static subsets |
| 2. Provider/version/transaction/session/batch | Full ordered provider-specific live contract and no implicit-commit ambiguity | **Not proven** for a Script plan |
| 3. Ordered ReadSet/WriteSet/schema/savepoint proof | Immutable plan-level set and full effect lineage, including side effects of rolled-back statements | **Not proven**; U2/U3 are static partial evidence |
| 4. Permission union | Independent per-statement and whole-script authorizations, Query-only cannot acquire write authority | **Not implemented** for a Script Host |
| 5. Whole-script DML approval | Exact actual affected rows/payload/fingerprint and commit-time drift revalidation for all branches | **Implemented only for existing Host DML-only batch**, not user TCL/script |
| 6. Independent DDL/DML approval | No permission inheritance; stronger mixed-script policy | **Not implemented** as executable mixed Script |
| 7. Unknown outcomes | Lost COMMIT/ROLLBACK response, reconciliation, non-retryability, one-use approval | **Existing domain runtimes have bounded handling**, no end-to-end Script outcome contract |
| 8. Host admission & identity | Live DB identity, metadata, policy revision, approval expiry, lease, and native execution boundary rechecked | **Existing domain-specific gates only**, no Script approval/execution session |

**No executable Script Plan is authorized because the eight gates are not all met.** U2/U3/U4 outputs are not Host-trusted credentials, and neither a public DTO nor a known Virtual Catalog can be upgraded by conversion or a new boolean property.

## 4. Acceptance tests and stop rule

Public black-box regression tests in `SqlCoreScriptExecutionDecisionGateTests` pin three contracts:

1. A DML-only two-statement batch still parses in original order (existing, supported Host flow preserved).
2. A fully analyzable, known-catalog `BEGIN; UPDATE; COMMIT` may show a static commit boundary but **fails** the executable DML batch parser and has `IsCompositionExecutionProof=false`.
3. A known-catalog `SAVEPOINT; DELETE; ROLLBACK TO` may show static expected rollback, but **never proves server/external rollback** or enters the executable DML batch parser.

These are **GREEN characterization / decision-gate tests** over existing public behavior; this decision PR makes no new execution feature and therefore does not claim artificial RED→GREEN development. The existing full CI / ABI / six-provider checks remain mandatory on the latest PR head.

**Stop U6 after documenting this NO-GO and merging passing characterization tests.** Do not create a new tool, API, policy interface, second parser, synthetic execution-token wrapper, or redundant Host transaction path merely to satisfy a roadmap numbering convention.

## 5. Conditions to reconsider a future U7

Open a **new, separately authorized** scope only when all of the following are demonstrated:

- A real product use case cannot be handled by the current Host-owned atomic DML batch (including supported operations and result semantics).
- One exact `Provider × Version × Statement Order × Transaction/Savepoint Shape` is selected; no generic multi-provider claim.
- It has a private, non-forgeable, policy-checked Script Plan with no raw caller TCL forwarded to native commands; Syntax/Analysis DTOs cannot be used as execution proof.
- All affected and possibly affected rows (including DML later rolled back) are represented in an aggregate approval and rechecked inside the same live transaction, with trigger/UDF/sequence effects explicitly bounded or rejected.
- Physical tests prove happy-path commit, row-set drift rollback, savepoint semantics, cancellation before/after native execution, lost COMMIT/ROLLBACK ACK and non-replay; independent witness connections are used, not static Catalog assertions.
- Host independently verifies exact DB identity, credentials/privileges, policy/approval expiry, distributed admission and audit correlation.
- The introduction of a new MCP tool is separately justified against the existing **few-tools** product goal. A new script tool is *not* the default choice.

If any prerequisite is missing, the status stays **Analyze-only / NO-GO**. Future grammar/capability U8 work and v3.1.0 tag-based ABI verification are independent tracks.
