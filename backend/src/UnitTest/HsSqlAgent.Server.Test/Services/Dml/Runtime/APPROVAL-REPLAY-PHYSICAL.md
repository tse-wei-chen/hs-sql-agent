# SQLite physical DML approval replay & TOCTOU matrix

Boundary: `TypedDmlRuntime` preview / aggregate approval / physical
commit, using the first-party SQLite provider and `NativeDmlSchemaSnapshotService`
for both schema capture and freshness verification.

This deterministic, isolated-file suite covers:

- Six actor/target/provider/database identity substitutions after preview.
- Changed DML row policy, widened table whitelist, and reordered batch
  statements, all rejected before any physical mutation.
- Recovery of the original approval after the above unprivileged attempts;
  a correct transaction commits exactly once and cannot be replayed.
- Concurrent double-commit against one in-memory aggregate approval: one
  succeeds and the second sees the already consumed challenge.
- Mutation of the second statement's approved row set after preview: rollback
  must also undo the first statement's physical mutation.
- Unique-key failure in statement two: rollback of statement one, and no
  reuse of the consumed approval.
- Expired aggregate challenge: fail closed without touching physical rows.

All tests assert the *actual* on-disk SQLite state before/after execution,
not just mock call counts. This does not substitute for separate durable
cross-instance Redis/AdminStore recovery, nor physical parity on the other
five database engines. Those have distinct CI requirements.
