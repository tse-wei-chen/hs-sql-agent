# Experimental target-enlisted DML execution fencing

## Why the existing COMMIT witness alone is insufficient

The target COMMIT witness is inserted **after** approved DML and before COMMIT.
It can establish a positive committed result but cannot stop an `Executing`
worker from starting and committing later. A timeout, missing witness, dead
process heartbeat or an Admin Store lease is not a target-database fence.

This **opt-in, versioned** admission protocol adds a second, independently
provisioned target table: `HS_SQL_AGENT_DML_FENCE`. An enrolled worker inserts
an `Admitted` row under the approval's unique `REQUEST_ID` **inside the
same target transaction, before schema revalidation and before any approved
mutation**. The row participates in the same target COMMIT as the approved SQL
and the existing `HS_SQL_AGENT_DML_WITNESS` row. If the SQL transaction rolls
back, both the admission and witness roll back.

An independent, target-transaction revoker can instead insert a persistent
`Revoked` row with that same `REQUEST_ID`. Because the database enforces a
unique primary key, **exactly one of the competing rows can commit**.
If revocation is COMMIT-acknowledged first, a late admitted worker's insertion
fails before any SQL mutation. If an admitted worker commits first, the
revocation collides and **MUST NOT** be reported as successful.

If both are concurrent, the provider's unique-index locking/serialization
behavior decides the winner. A deadlock, lock timeout, connection loss,
constraint violation, unexpected table data, or uncertain COMMIT cannot
be reported as a proven revocation. The system does not use wall-clock
age as evidence.

## Target schema

Provision the existing witness table described in
[COMMIT witness](dml-target-commit-witness.md) **and** this additional
fence table on each opted-in target. Use an actual transaction-capable
table engine with an enforced `REQUEST_ID` primary key.

**PostgreSQL**

```sql
CREATE TABLE HS_SQL_AGENT_DML_FENCE (
    REQUEST_ID VARCHAR(64) PRIMARY KEY,
    EVIDENCE_FINGERPRINT CHAR(64) NOT NULL,
    ENTRY_KIND VARCHAR(16) NOT NULL
        CHECK (ENTRY_KIND IN ('Admitted', 'Revoked'))
);
```

**MySQL (transactional InnoDB)**

```sql
CREATE TABLE HS_SQL_AGENT_DML_FENCE (
    REQUEST_ID VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin PRIMARY KEY,
    EVIDENCE_FINGERPRINT CHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    ENTRY_KIND VARCHAR(16) NOT NULL
) ENGINE=InnoDB;
```

**SQLite**

```sql
CREATE TABLE HS_SQL_AGENT_DML_FENCE (
    REQUEST_ID VARCHAR(64) PRIMARY KEY,
    EVIDENCE_FINGERPRINT VARCHAR(64) NOT NULL,
    ENTRY_KIND VARCHAR(16) NOT NULL
        CHECK (ENTRY_KIND IN ('Admitted', 'Revoked'))
);
```

**Microsoft SQL Server**

```sql
CREATE TABLE HS_SQL_AGENT_DML_FENCE (
    REQUEST_ID VARCHAR(64) NOT NULL PRIMARY KEY,
    EVIDENCE_FINGERPRINT CHAR(64) NOT NULL,
    ENTRY_KIND VARCHAR(16) NOT NULL
        CHECK (ENTRY_KIND IN ('Admitted', 'Revoked'))
);
```

**Oracle**

```sql
CREATE TABLE HS_SQL_AGENT_DML_FENCE (
    REQUEST_ID VARCHAR2(64 CHAR) PRIMARY KEY,
    EVIDENCE_FINGERPRINT CHAR(64 CHAR) NOT NULL,
    ENTRY_KIND VARCHAR2(16 CHAR) NOT NULL
        CHECK (ENTRY_KIND IN ('Admitted', 'Revoked'))
);
```

**Firebird**

```sql
CREATE TABLE HS_SQL_AGENT_DML_FENCE (
    REQUEST_ID VARCHAR(64) NOT NULL PRIMARY KEY,
    EVIDENCE_FINGERPRINT CHAR(64) NOT NULL,
    ENTRY_KIND VARCHAR(16) NOT NULL,
    CONSTRAINT CK_HSA_DML_FENCE_KIND
        CHECK (ENTRY_KIND IN ('Admitted', 'Revoked'))
);
```

Do not use a view or non-transactional engine. Protect the table from
out-of-band deletion, UPDATE, triggers, or insertions by unrelated principals.
Grant least-privilege INSERT/SELECT for the DML runtime and the authorized
fencing operator. Avoid deploying with mixed or unverified connection
routing (e.g., read replica lag, untrusted multi-primary replication).
PostgreSQL 16 and MySQL 8 now have real CI integration coverage for the
fixed SQL/ADO parameters and target transaction boundaries. Each test creates
an isolated PostgreSQL schema or an isolated MySQL InnoDB database with the
production-style unique fence and witness tables. Tests cover revocation-first,
admission-first, rollback, missing-witness rollback, and an independently
connected revoker contending with an uncommitted admitted worker. A revoker
must never acknowledge success while a competing worker holds uncommitted
admission. Beyond the ADO fence kernel, real PostgreSQL/MySQL CI also
executes **compiled DML through both `DmlCoordinator` and
`DmlAtomicTransactionCoordinator`**: a committed revocation rejects
single-statement and batch execution before any application row changes,
while a successful target COMMIT records the application mutation,
`Admitted` fence row and matching witness in one transaction. These are
physical database and coordinator-path proofs, **not** a claim of
fleet-wide fencing or automatic `Executing` reconciliation.

A separate mandatory SQL Server 2022 GitHub Actions job exercises nine
real database integration cases: revocation-first, worker-first, rollback,
missing-witness rollback, concurrent uncommitted admission and both compiled
single/batch DML coordinator paths for approved and revoked requests. Every
case uses independent physical connections and a dedicated isolated target
database. The first real-service job passed all 9 with zero skips, and the
dedicated test job remains required for PR CI. Oracle and Firebird live
concurrency interoperability remains **not yet demonstrated**. PostgreSQL/MySQL coverage also does not prove that every
production deployment uses a single authoritative writer or that all fleet
members enforce `target-fence-v1`.

## Opt-in and rollout

Call `builder.AddHsSqlAgentTargetExecutionFence()` on an
`HsSqlAgentRegistrationBuilder` on every server instance handling durable
approvals. This also opts into the required target COMMIT witness.
The default runtime remains unchanged.

When this feature is enabled at the time a durable approval is persisted,
the protected approval payload is stamped `FenceProtocol=target-fence-v1`.
Execution of that approval later *requires* a fencing-capable runtime and
an enabled COMMIT witness. Downgrading to an unfenced runner is prohibited.
Approvals created before enrollment are **not** retroactively proven safe.
Do not interpret them as fenced, even if the process is upgraded later.

**Deployment safety precondition:** ALL potentially eligible worker binaries
and nodes must already implement and enforce this protocol *before any
fenced approval is issued*. Old binaries can ignore newly serialized
fields and remain able to execute a previously pending approval. A mixed
fleet or rolling upgrade is NOT safe evidence of quiescence.

The `IDmlTargetExecutionFence` interface exposes `AdmitAsync`,
`RevokeAsync` and `ReadAsync`. Only `AdmitAsync` is part of the normal
execution path. **No automatic reconciliation or operator endpoint for
`RevokeAsync` is enabled at this stage.** A revocation must be performed by
a controlled, separately authorized workflow after verifying the original
target identity and versioned approval eligibility. Never manually change
Admin Store `Executing` to a terminal state simply because a fence
table row appears.

## Proof boundaries

Real SQLite tests cover worker-first, revoker-first, SQL rollback, target
COMMIT witness coexistence, single-statement and atomic-batch execution,
restarted durable approvals, and missing component fail-closed behavior.
PostgreSQL/MySQL CI tests additionally exercise real server-side unique-key
conflicts, committed/rolled-back witness atomicity, revocation and a concurrent
uncommitted admission, plus both *compiled* single-DML and atomic batch
coordinators against real target databases (positive COMMIT with admission
and witness, and pre-revoked approvals with zero mutations).
The unique-key protocol is not the same as a verified fleet-wide fencing
deployment. A positive `Revoked` entry can become strong negative-outcome
evidence **only** when the approval has the versioned protocol, target HMAC
identity remains bound, all workers enforce admission-before-mutation, and
the revocation is durably COMMIT-acknowledged on the authoritative target.

Until these conditions are independently checked and real-provider
interoperability exists, the reconciliation API still forbids settling
`Executing`. A missing `Admitted` or COMMIT-witness row is not proof
that the original worker cannot commit.
