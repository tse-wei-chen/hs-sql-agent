# Upgrade to v3.0.0 (PR #299)

This is an in-progress migration guide, not an already published release.

## Breaking changes

- **Custom approval stores:** `IDmlApprovalChallengeStore.TryPromoteAsync` is required for atomic child-to-aggregate promotion. Both one-statement and multi-statement `execute_dml_sql` use the aggregate path. Custom implementations must implement atomic all-or-nothing promotion; sequential delete/add is not safe.
- **Redis approval keys:** v3 uses `{dml-approval}:` within keys for same-slot atomic Redis Cluster promotion. v2 outstanding approvals are not readable by the new key format; stop new approvals and drain/reject old requests before cutover.
- **Challenge binding:** outstanding approvals without `ApprovalPlanBinding` will fail closed. Request new previews and obtain fresh approval; never replay a consumed nonce.
- **Mutation policy:** `UPDATE`/`DELETE` with provably always-true predicates (for example `WHERE 1=1`) are denied when a selective predicate is required. Use an actual predicate or explicit full-table authorization.
- **SQL resource limits:** input beyond 4,194,304 UTF-16 units or 131,072 lexer tokens is rejected.
- **Uncertain target outcomes:** `dml.commit_outcome_unknown` and `dml.rollback_outcome_unknown` are distinct non-retryable outcomes. Neither implies a safe retry.

## Operational rollout

1. Quiesce new v2 DML previews; classify Pending durable requests and short-lived Redis challenges. Do not mistake missing keys or an absent witness for database rollback.
2. Drain **all** old DML-capable workers before enabling v3 pending-approval execution. A mixed fleet is not a valid execution-fence proof.
3. Deploy v3 and rebuild custom approval stores together. Ensure storage and authorization are healthy before resuming DML.
4. Require fresh approval for challenges incompatible with v3; verify six-provider regression and structured `outcome_unknown` tests.
5. Only enable opt-in native schema freshness or experimental target execution fencing after provisioning tables/permissions and validating all participating target providers.

`AddHsSqlAgentNativeDmlSchemaFreshness()` and `AddHsSqlAgentTargetExecutionFence()` remain **opt-in**. Experimental fence behavior has real SQLite, PostgreSQL and MySQL coverage; remaining provider/fleet parity, supervised revocation, and safe `Executing` reconciliation are not fully proven. See [fence documentation](dml-target-execution-fence.md).
