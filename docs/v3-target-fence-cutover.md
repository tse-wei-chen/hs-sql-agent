# v3 target-fence deployment and credential cutover

> **Release candidate checklist, not an automatic fleet-safety certificate.**
> The six-provider relational admission/fence kernel has real transaction
> coverage. Production protection requires an authoritative target,
> separately controlled worker identities and *verified* withdrawal of
> legacy write authority. An Admin Store schema marker alone does not do this.

## What the two proofs actually establish

1. **New v3 connection readiness:** `DmlTargetFenceReadinessProbe.VerifyAsync`
   (packed `SqlAgent.Service` public API) is run against the *same target
   endpoint, database and login* used by the enrolled v3 DML worker. The
   built-in probe begins SERIALIZABLE, inserts a unique `Admitted` row in
   `HS_SQL_AGENT_DML_FENCE`, inserts its matching row in
   `HS_SQL_AGENT_DML_WITNESS`, **rolls back**, and reads both tables to
   confirm neither row persisted. It also deliberately retries the same
   request ID inside the two rollback-only transactions and requires the
   target's **physical UNIQUE/PRIMARY KEY constraints** to reject duplicate
   Fence and Witness entries independently. Critically, it tests
   `Admitted` versus `Revoked` using the **same request ID**, so a
   misleading composite UNIQUE(`REQUEST_ID`, `ENTRY_KIND`) cannot
   masquerade as a valid fence. Witness uniqueness is tested with the
   same request ID but a **different fingerprint**, so uniqueness on
   `EVIDENCE_FINGERPRINT` alone cannot masquerade as request uniqueness.
   Only the provider's specific unique/primary-key violation diagnostic
   is accepted; an arbitrary database error (including permission denial
   or trigger failures) is not evidence of a unique key. It never executes
   application SQL,
   commits a probe row or changes any Admin Store approval.
   Missing tables, missing privileges, unsupported drivers, failed rollback,
   residual rows and cancellation must cause **readiness failure**, never a
   successful deployment decision. It uses the built-in relational fence
   and witness implementations, not user-injected no-op probes.
2. **Legacy write authority removed:** independently prove that all previous
   DML-capable identities **cannot modify any of the targeted application
   tables** and that their existing sessions cannot resume an in-flight write.
   A passing probe for the *new* login does not prove this. A negative test
   against only `HS_SQL_AGENT_DML_FENCE` is meaningless because a v2 worker
   never needs that table.

Example use in a deployment verification utility (the caller supplies its
normal provider-specific factory and the exact production/staging v3 connection):

```csharp
using HsSqlAgent.SqlCore.Enums;
using SqlAgent.Service.Core.Execution;

// connection is an open DbConnection using the intended v3 DML identity.
await new DmlTargetFenceReadinessProbe()
    .VerifyAsync(connection, SqlAgentToolType.Postgres, cancellationToken);
```

**PostgreSQL automated old-role check:** The public
`PostgresLegacyDmlCutoverVerifier.VerifyAsync` accepts an open privileged
monitoring connection and a named retired v2 role. It reads PostgreSQL's
catalogs, follows transitive role membership including NOINHERIT/`SET ROLE`,
checks effective INSERT/UPDATE/DELETE/TRUNCATE permissions (including
column-specific INSERT/UPDATE grants) on all non-system tables,
partitioned/foreign tables and views in the **current database**,
and counts surviving sessions for the retired login. It requires superuser or
`pg_read_all_stats` monitoring authority and rejects **LOGIN-enabled roles**,
unknown identities, elevated membership, remaining table privileges,
callable non-system `SECURITY DEFINER` functions and live sessions.

```csharp
using SqlAgent.Service.Core.Execution;

// adminConnection must be open against the authoritative PostgreSQL writer.
var snapshot = await new PostgresLegacyDmlCutoverVerifier()
    .VerifyAsync(adminConnection, "retired_v2_worker", cancellationToken);
```

This is **read-only point-in-time evidence**, not automatic credential
revocation or a guarantee of fleet-wide quiescence. It cannot verify old
binaries on other hosts, another writer/database, unscanned external procedural access,
future grants or deployment credentials external to PostgreSQL. The other five database engines need separate cutover proof.

**SQL Server old-login verification:** The independent
`SqlServerLegacyDmlCutoverVerifier.VerifyAsync` accepts an open **sysadmin
verification connection** to the intended target database and a specific old
SQL Server login. It requires an unambiguous server-login/database-user
mapping, queries live server sessions, and executes read-only effective rights
checks using `EXECUTE AS USER` and `HAS_PERMS_BY_NAME` against an object
inventory collected before impersonation. Ordinary tables, views,
column-scoped permissions and inherited `db_datawriter` grants are checked.
Unknown logins, elevated sysadmin membership, surviving sessions, grant
paths, incomplete or synonym-based inventory fail closed.

```csharp
using SqlAgent.Service.Core.Execution;

// adminConnection is an open sysadmin connection on the actual target DB.
var evidence = await new SqlServerLegacyDmlCutoverVerifier()
    .VerifyAsync(adminConnection, "retired_v2_sql_login", cancellationToken);
```

This is a point-in-time **read-only permission observation**, not an old
login revocation action. It does not prove an old host has exited, stored
procedures / ownership chains have no write effects, schema impersonation is
impossible, future privileges cannot be regranted, or other databases and
primaries share the same cutover state. Operators must independently
validate those paths and all other providers before claiming full fleet
enrollment.

**Runtime enforcement:** When `target-fence-v1` is requested, the
single-statement and atomic-batch coordinators both run the physical
readiness probe on the actual opened target connection, **before** the
business mutation transaction. Production fenced writes require the built-in
audited relational Fence and Witness; injected custom no-op implementations
are not accepted as validated readiness. Each fenced commit therefore costs
**two additional SERIALIZABLE transactions, both rolled back**, plus
residual-evidence visibility queries. No cross-connection/global cached
readiness can justify another write. A failed preflight occurs after approval
consumption and cannot be used to replay the original approval.

Do not publish a successful result for a different endpoint, read replica,
privilege scope or connection identity than the actual DML runtime. Verify
each managed target independently, including all tenant-specific identities.

## Required coordinated v2 → v3 cutover

1. **Inventory and block intake.** Record every target database identity,
   engine/version, shard, reachable writer endpoint, DML-capable v2
   workload, target application table, identity/role and active connection.
   Disable new v2 previews and external approval completions. Do not depend
   on a heartbeat expiry or one process-manager dashboard.
2. **Drain with evidence.** Stop v2 workers and cancel/reconcile all v2
   `Pending` approvals. Do not mark `Executing` as rolled back from age,
   missing Redis keys or missing witness records. Obtain independently
   verified physical outcomes and worker quiescence. Back up the original
   Admin Store, encryption keys and any unresolved evidence *together*.
3. **Withdraw legacy target access.** On the authoritative target, rotate or
   disable old DML credentials, revoke effective INSERT/UPDATE/DELETE rights
   over every affected table (including privileges from roles, ownership
   and global/system grants), and disconnect/drain established old sessions.
   Verify using a **fresh connection and actual effective permissions as
   the old principal**, not only a statement that `REVOKE` succeeded.
   A failed login because of an unrelated temporary network outage is not
   affirmative proof of privilege revocation.
4. **Upgrade Admin Store.** Only after old Worker quiescence, run the
   SQLite/PostgreSQL v3 migration. Its migration gate rejects any current
   `Pending` or `Executing` requests. After migration,
   `AdmissionProtocolVersion` rejects legacy attempts to INSERT or
   resurrect `Pending/Executing` with an omitted version (DB default 0).
   It **does not** stop direct SQL written with old target credentials.
5. **Provision and validate the new path.** Using a separate v3 runtime
   identity and authoritative target, install both physical tables and
   enforced unique keys as documented in
   [target-fence schema](dml-target-execution-fence.md). The v3 identity
   needs only target application DML and fence/witness INSERT/SELECT;
   fence/witness UPDATE/DELETE/DDL permissions should not be available to
   normal workers. Execute the rollback-only readiness probe on each
   configured target.
6. **Enrol as a single authority.** Bring up only `target-fence-v1`
   capable workers and verify the protected approval payload is versioned.
   Enable `AddHsSqlAgentTargetExecutionFence()` on **every** eligible
   DML worker. Preview and approve new requests; do not import old Pending
   approvals, and do not retry consumed or physically uncertain requests.
   Validate a single-statement and batch DML against a disposable test
   dataset before admitting real traffic.
7. **Confirm and retain evidence.** Record the cutover timestamp (UTC),
   target endpoint/identity fingerprint **without secrets**, affected
   principal/role inventory, privilege-denial evidence, session drain proof,
   Admin Store migration IDs, per-target readiness results, active binary
   version and follow-up operator. On loss of evidence, disable v3 DML and
   investigate; never quietly switch back to an old unfenced writer.

## Provider-specific enforcement boundary

| Provider | What an operator must verify | Caveat |
| --- | --- | --- |
| PostgreSQL | Effective per-table DML privileges (`has_table_privilege` / catalog), roles, schema ownership, database-level/system privilege, and no surviving old sessions | Check inheritance and future table grants; a superuser or owner can bypass simplistic REVOKE checks |
| MySQL (InnoDB) | Database/table/global grants, active roles, old-account secrets and existing connections; new workers use transactional InnoDB fence/witness tables | Checking the table named FENCE alone does not prove old accounts cannot modify customer tables |
| SQL Server | Effective `HAS_PERMS_BY_NAME` for each user/object plus fixed database/server roles, ownership, active sessions and the real login's database context | `REVOKE` may reveal a broader inherited grant; an old sysadmin/database owner is not fenced |
| Oracle | Object/system privileges, enabled roles, ownership, existing sessions, exact service/schema binding and database synonyms | `ANY TABLE` privileges and elevated accounts defeat a naive object-level revoke |
| Firebird | Effective `RDB$USER_PRIVILEGES`, grants through roles, target owner/SYSDBA bypasses, and remaining attachments | A privileged DB owner or retained attachment must not be considered revoked |
| SQLite | Stop every old process, isolate and move/rotate the actual database file and journal/WAL access under new OS credentials, verify old processes and open handles are gone | SQLite has **no database-user privilege model**. Do not claim independent old-worker fencing while old processes retain writable file handles |

For multi-primary or asynchronously replicated targets, a successful probe on
one writer does not prove another writer will observe the same unique
`REQUEST_ID` conflict before committing. Restrict enrolled DML to one
authoritative writer or separately prove equivalent globally serialized
admission semantics. Never interpret read-replica lag as a missing commit.

## Explicit unsupported guarantees

- The probe is **not** a fleet membership protocol, old-credential verifier,
  application-table privilege audit, or revocation operator.
- The v3 Admin Store admission constraint is not a database firewall for
  unaudited direct SQL or for historical v2 sessions already executing.
- A confirmed `Revoked` target fence row is only proof against workers
  enrolled in the same authoritative protocol. It cannot retroactively
  establish that an old binary was enrolled.
- Automatic `Executing` reconciliation and revocation-by-timeout are not
  enabled. Uncertain outcomes remain consumed and require supervised
  investigation; restore procedures must never replay the approval.
- Rolling mixed v2/v3 target-DML fleets are **not** supported as safe
  in-place upgrades. Use a coordinated drain and privilege cutover.

## CI evidence and what it does not prove

The six real provider integration fixtures execute readiness success,
real missing-witness and missing-unique-key failures, plus injected witness
failure rollback paths as well as admitted/witness atomicity
and independent revoker races. These are enforceable test results for the
database transaction implementation, **not** a production authorization
inventory. Customer-specific privilege withdrawal and old-session termination
must be recorded as deployment evidence before calling Target Fence
production-ready for that topology.


SQL Server legacy worker cutover requires a disabled retired SQL login. Externally managed Windows/AD identities cannot be attested by the local verifier.
