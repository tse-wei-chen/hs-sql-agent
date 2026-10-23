# Optional target-enlisted DML commit witness

A durable DML approval is first consumed in the **Admin Store** and then revalidated and
executed in the **target database**. Those are independent databases and do not share an
atomic commit. A dropped COMMIT acknowledgement or a crashed worker can leave the Admin
Store in `Executing` even if target mutations have committed.

The opt-in `AddHsSqlAgentTargetCommitWitness()` facility writes a unique request-ID
witness in the **same physical target transaction** as approved single/batch DML, **after**
every mutation has passed commit-time revalidation, **before** the target COMMIT.
The witness stores the durable request ID and the SHA-256 approval evidence fingerprint.
A positive, independently read, matching witness is strong evidence of a committed
target transaction **if the table's integrity and access controls are trusted**.

It **does not** prove that an in-flight worker is no longer running. An absent witness
does not mean the target did not commit or cannot commit later. It is not a fencing
lease, a distributed transaction, an audit signature, or authorization to replay
the original approval. Human reconciliation of `Executing` remains forbidden.

## Provisioning

Create the following table in **each target database** where you plan to enable
this optional facility. The table must have an enforced **unique primary key**
on `REQUEST_ID`; without that constraint, duplicate-witness protection is not valid.
SQL names are intentionally fixed, uppercase ASCII and unquoted. Do not point
`HS_SQL_AGENT_DML_WITNESS` at an application view or a table with destructive triggers.

### PostgreSQL

```sql
CREATE TABLE HS_SQL_AGENT_DML_WITNESS (
    REQUEST_ID VARCHAR(64) PRIMARY KEY,
    EVIDENCE_FINGERPRINT CHAR(64) NOT NULL
);
```

### MySQL (InnoDB, transactional)

```sql
CREATE TABLE HS_SQL_AGENT_DML_WITNESS (
    REQUEST_ID VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin PRIMARY KEY,
    EVIDENCE_FINGERPRINT CHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL
) ENGINE=InnoDB;
```

### SQLite

```sql
CREATE TABLE HS_SQL_AGENT_DML_WITNESS (
    REQUEST_ID VARCHAR(64) PRIMARY KEY,
    EVIDENCE_FINGERPRINT VARCHAR(64) NOT NULL
);
```

### Microsoft SQL Server

```sql
CREATE TABLE HS_SQL_AGENT_DML_WITNESS (
    REQUEST_ID VARCHAR(64) NOT NULL PRIMARY KEY,
    EVIDENCE_FINGERPRINT CHAR(64) NOT NULL
);
```

### Oracle

```sql
CREATE TABLE HS_SQL_AGENT_DML_WITNESS (
    REQUEST_ID VARCHAR2(64 CHAR) PRIMARY KEY,
    EVIDENCE_FINGERPRINT CHAR(64 CHAR) NOT NULL
);
```

### Firebird

```sql
CREATE TABLE HS_SQL_AGENT_DML_WITNESS (
    REQUEST_ID VARCHAR(64) NOT NULL PRIMARY KEY,
    EVIDENCE_FINGERPRINT CHAR(64) NOT NULL
);
```

Configure appropriate schema/search path and **least-privilege INSERT and SELECT**
rights for the target database account. The witness SQL never creates or migrates
schema at request time. A missing table, insufficient permissions, a duplicate
`REQUEST_ID`, a missing transaction or a witness write failure aborts the DML
transaction rather than committing mutation without the witness. Always verify
your provider's actual transactional behavior (including MySQL InnoDB).

## Opt-in host configuration

On your existing `HsSqlAgentRegistrationBuilder` (after configuring
the SQL Agent runtime), add:

```csharp
builder.AddHsSqlAgentTargetCommitWitness();
```

The host registration injects the `RelationalDmlTargetCommitWitness` into
`TypedDmlRuntime`. Durable approvals automatically attach their
server-owned request ID and already-persisted evidence fingerprint.
Unrelated calls that do not supply a durable witness intent retain their
existing behavior. The default installation does **not** enable this
feature or require a witness table.

**Durable restart contract:** When a Pending approval is created while the
commit witness is enabled, the encrypted resume payload records
`RequiresCommitWitness = true`. Its later completion MUST have an enlisted
witness writer even after a service restart or a deployment configuration
change. Disabling the writer cannot silently downgrade that already-issued
approval to a witness-free COMMIT; the consumed approval fails closed without
executing any target mutation. Historical payloads issued before this
requirement deserialize with `false` for backward compatibility; enabling
the witness later may provide additional evidence but does not retroactively
turn a legacy approval into a proof-bound one.


For independent investigation, `IDmlTargetCommitWitness.ReadFingerprintAsync`
can inspect a request ID on a separately opened, correctly authenticated
target connection. A reader must compare the result with the Admin Store
record's expected evidence fingerprint. An absent, unreadable, mismatched
or untrusted witness cannot justify declaring `not_committed` or permitting
a retry. The operator-only reconciliation API deliberately does **not**
automatically promote `Executing` from this read.

### Read-only operator inspection

A human with `/runtime/security.edit` may inspect one uncertain approval via
`GET /api/runtime/dml-approvals/reconciliation/{requestId}/witness`, or click
**Inspect target witness** on the Admin Console reconciliation page.
Management API keys cannot grant this permission.

Before opening a target DB connection, the server checks the approval's
database ID/provider/name and recomputes its original *HMAC-bound connection
fingerprint* from the encrypted persisted resume payload. Changed or
undecryptable connection configuration prevents a probe. The response returns
only a categorical result, request ID, approval status, inspection time and
safety warning; never SQL, target connection strings or evidence fingerprints.

- `CommittedWitnessMatch`: matching witness observed in the originally bound
  target. Positive target-commit evidence; the original worker may nevertheless
  still need fencing before any state transition from `Executing`.
- `NotObserved`: no witness row observed. **Not** proof of noncommit or
  future worker quiescence.
- `EvidenceMismatch`: a witness row was found but its evidence fingerprint
  conflicts with the persisted approval. Treat as potentially tampered.
- `TargetConfigurationChanged`: the target identity/connection no longer
  matches the approved target; no probe is attempted.
- `WitnessNotConfigured`, `ReadUnavailable`, `ApprovalChanged`: no usable
  physical result, respectively because the feature is off, a probe failed,
  or the Admin Store snapshot changed during I/O.

Inspection never runs a DML mutation, writes an Admin Store status, reissues
approvals, or unlocks reconciliation of `Executing`. For eligible terminal
uncertain states, the backend independently re-checks this witness when an
operator attempts to attest `not_committed` or `indeterminate` and rejects
either outcome if a matching committed witness exists. A negative or
unverifiable witness remains just one input for the operator's independent
investigation, never an automatic disposition.
If an operator has already inspected a matching witness in the Admin Console,
the UI additionally removes `not_committed` and `indeterminate` from the
displayed attestation choices. This is **only** an operator safeguard; the
server independently repeats the verification before saving, so a stale
or modified browser response never becomes authoritative.

## Validation and remaining limitations

Real SQLite tests verify all-or-nothing target DML and witness insertion for
single-statement and atomic-batch paths, rejection of duplicate request IDs,
fail-closed behavior when the witness table is missing, and end-to-end durable
approval binding after a service restart.

The insert/read command uses fixed, parameterized SQL with the dialect's ADO.NET
placeholder convention for all six configured database providers. Real SQLite,
PostgreSQL 16, MySQL 8 (InnoDB) and SQL Server 2022 suites exercise witness
writes and reads using actual target transactions. The PostgreSQL/MySQL and
SQL Server suites also cover independently connected revocation competition
and compiled single/batch DML paths. **Oracle and Firebird live target-fence
and witness concurrency parity remains unverified**, even though their
parameterized commands and schema definitions exist.

The hard-coded table name depends on the target session's resolved schema;
protect both witness and fence tables from out-of-band modification.
Other applications or administrators who can forge rows can also forge
witness evidence. A target admission fence provides physical exclusion
only if every eligible worker enforces the protocol on the authoritative
target and revocation COMMIT is positively acknowledged. Automatic
reconciliation of `Executing` is still forbidden.

The post-probe Admin Store re-read also compares the approval's protected resume payload, access key ID, provider and database identity, in addition to status, update timestamp, reason, evidence fingerprint and target management ID. Any concurrent mutation of those binding fields invalidates the inspected snapshot as `ApprovalChanged`; it cannot be presented as positive COMMIT evidence for a different approval binding.

A concurrent edit to the Admin Store target configuration during the independent witness probe also invalidates the result: the server re-reads provider, database, host, port, username, encrypted password and extra connection settings after the probe and returns `TargetConfigurationChanged` if any differ. This protects the result against a stale target configuration snapshot; it is not a cross-database atomic snapshot or a worker-fencing proof.

Fixed-width `CHAR(64)` implementations (notably Oracle) may return right-padded values. Witness reads strip only trailing ASCII space padding before strict 64-character hex validation and constant-time fingerprint comparison. This compatibility rule does not loosen evidence matching or confer worker-fencing proof; live Oracle parity remains outstanding.
