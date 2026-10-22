# Upgrade to v3.0.0 (PR #299)

This is an in-progress migration guide, not an already published release.

## Breaking changes

- **Custom approval stores:** `IDmlApprovalChallengeStore.TryPromoteAsync` is required for atomic child-to-aggregate promotion. Both one-statement and multi-statement `execute_dml_sql` use the aggregate path. Custom implementations must implement atomic all-or-nothing promotion; sequential delete/add is not safe.
- **Redis approval keys and value protocol:** v3 uses `{dml-approval}:` within keys for same-slot atomic Redis Cluster promotion and a `hs-sql-agent.dml-challenge` envelope with `SchemaVersion=1` for stored values and Lua atomic promotion. v2 outstanding approvals are not readable by the new key format; stop new approvals and drain/reject old requests before cutover. A real Redis integration test proves an old key cannot be consumed or promoted by v3. Unknown/changing schemas reject rather than defaulting missing security fields. Mixed old/new workers are unsafe.
- **Challenge binding:** outstanding approvals without `ApprovalPlanBinding` will fail closed. Request new previews and obtain fresh approval; never replay a consumed nonce.
- **Durable resume storage contract:** new Pending approvals use an encrypted, versioned `hs-sql-agent.dml-resume` envelope (`SchemaVersion=1`, independent of product version). Legacy flat JSON is read through an explicit version-0 upcaster preserving original fence/witness flags, not silently authorizing new capabilities. Unknown or damaged versions reject before any target SQL. No Admin Store table migration is required for this format change.
- **Mutation policy:** `UPDATE`/`DELETE` with provably always-true predicates (for example `WHERE 1=1`) are denied when a selective predicate is required. Use an actual predicate or explicit full-table authorization.
- **SQL resource limits:** input beyond 4,194,304 UTF-16 units or 131,072 lexer tokens is rejected.
- **Uncertain target outcomes:** `dml.commit_outcome_unknown` and `dml.rollback_outcome_unknown` are distinct non-retryable outcomes. Neither implies a safe retry.

## Operational rollout

1. Quiesce new v2 DML previews; classify Pending durable requests and short-lived Redis challenges. Do not mistake missing keys or an absent witness for database rollback. Both SQLite and PostgreSQL now contain a **v3 migration drain gate**: if any Pending/Executing durable approval exists, the migration fails closed before it can mark the Admin Store v3-ready. The application startup preflight also reports sample blocked request IDs. After the gate succeeds, the migration adds `AdmissionProtocolVersion` (database default 0), with a SQLite/PostgreSQL database CHECK constraint: **only versioned approvals (protocol ≥ 1) may enter `Pending`/`Executing`**. v3 writes version 1 explicitly; v2 inserts omit the column and fail even from another connection after upgrade. Historical terminal approvals remain protocol 0 and cannot be reopened. This closes the legacy *Admin Store* write path, but does not revoke old workers' direct target-database permissions.
2. Drain **all** old DML-capable workers before enabling v3 pending-approval execution. A mixed fleet is not a valid execution-fence proof. A fence-enabled v3 worker also rejects legacy Pending approvals lacking `target-fence-v1`, even if they originally enrolled a commit witness. Only a fresh fenced preview and new human approval may proceed.
3. Deploy v3 and rebuild custom approval stores together. Ensure storage and authorization are healthy before resuming DML.
4. Require fresh approval for challenges incompatible with v3; verify six-provider regression and structured `outcome_unknown` tests.
5. Only enable opt-in native schema freshness or target execution fencing after provisioning tables/permissions and validating all participating target providers, including [rollback-only v3 target readiness and legacy-credential withdrawal](v3-target-fence-cutover.md). The new commit path also runs this proof on the actual target connection,
adding two rollback-only SERIALIZABLE transactions to every opted-in fenced
write. A passing new-identity probe does **not** prove that old binaries
have lost direct target DML authority.

`AddHsSqlAgentNativeDmlSchemaFreshness()` and `AddHsSqlAgentTargetExecutionFence()` remain **opt-in**. All six provider fixtures now exercise real target admission, revocation, transaction rollback and witness effects, including independent revocation competition. This is NOT proof that all production workers enforce the protocol. Fleet enrollment, separately authorized supervised revocation, and safe `Executing` reconciliation are still unfinished; never treat experimental fencing as automatic authorization to reconcile a live worker. See [fence documentation](dml-target-execution-fence.md).
