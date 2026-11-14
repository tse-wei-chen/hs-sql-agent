# DML approval connection-target binding (physical SQLite proof)

RED test commit `84f5cbea3ff5f8dfaa661575a4c62f5b3d7c515c` demonstrated
that pre-fix single `CommitAsync` and atomic `CommitTransactionAsync` could
execute a preview approved on SQLite file A against a different file B with
identical schema, rows and logical `DmlApprovalExecutionContext`. Both failed
because **no exception was thrown**: the physical cross-target mutation was
allowed.

The fix binds each server-created `TypedDmlApprovalSession` to the original
provider + exact connection descriptor using a **process-secret HMAC**.
The proof is checked before consuming any single/aggregate approval or opening
the commit connection. Missing proof on legacy manually assembled sessions is
fail-closed; a denied substitution leaves the original approved A session usable.

The HMAC is held internally in-memory and never placed in the MCP approval
response, SQL logs, AdminStore durable resume payload or Redis challenge
serialization. Durable pending completions **re-preview** after restart and
already have independent persistent HMAC verification of the connection
configuration. Existing public DML API signatures remain unchanged.

Proof scope is exact **connection-descriptor continuity**. It is not an
independent provider-native attestation of the physical server instance; if
DNS, symlinks, load balancers or external data files change behind an
identical descriptor, a separate real-server identity/transaction witness
is required. Preserve that distinction when describing security properties.
