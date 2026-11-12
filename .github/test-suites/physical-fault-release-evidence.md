# #575 — Physical fault and trust-boundary integration evidence

This matrix records **what mandatory CI proves**, not what compilation or mock exceptions imply. The new scenarios use a pinned Toxiproxy 2.12.0 Testcontainer; the real database is the pinned Docker CI service already started in each provider's job. The Testcontainers proxy and provider-native DB are physically separate. PostgreSQL and Redis keep their existing dedicated Testcontainers-backed suites.

## Actual real-provider coverage

| Provider | Engine / CI owner | Physical network/fault evidence | Independent witness and recovery | Remaining limit |
|---|---|---|---|---|
| PostgreSQL | PostgreSQL 16 / server-test | Existing in-flight Query proxy cut, backend termination, cancellation and COMMIT fault | Direct PostgreSQL witness plus successful resumed Query; separate real post-durable COMMIT wire EOF proof under sqlagent-test | No blanket multi-node failover guarantee |
| MySQL | MySQL 8.4 / sqlagent-test | **New:** successful proxied production Query → TCP cut → rejected Query → restore | Direct MySQL witness succeeds during partition; production Query resumes | Connection/admission partition, not lost COMMIT acknowledgment |
| SQL Server | SQL Server 2022 / sqlagent-fence-sqlserver | **New:** successful proxied production Query → TCP cut → rejected Query → restore | Direct SQL Server witness succeeds; production Query resumes | Explicit nonempty allowset with privileged CI login isolates transport; NOT least-privilege proof |
| Oracle | Oracle 26ai / sqlagent-oracle-ddl-parity | **New:** same real TCP partition sequence | Direct PDB witness; production Query resumes | No autonomous PL/SQL/external I/O proof |
| Firebird | Firebird 5 / sqlagent-firebird-ddl-parity | **New:** same real TCP partition sequence | Direct Firebird witness; production Query resumes | No external PSQL/failover proof |
| SQLite | Embedded / native live suites | Existing file-backed permission, transaction, cancellation and callback/fence checks | Independent file-based readback | SQLite has no TCP server to cut; a fake TCP partition would be invalid evidence |
| Redis | Redis 7 / server-test | Existing process-stop and genuine TCP-partition revocation and admission tests | Independent direct Redis witness; revocation cannot recover into the old lease | Not Redis Sentinel/Cluster failover, replication or quorum proof |

## Mandatory gate

- Four **exactly one case, zero failed, zero skipped** provider partition checks are owned by existing dedicated CI jobs. The server integration manifest excludes the class from the ordinary shard, and the ownership auditor rejects missing assignments or missing exact-case gates.
- For each case the **production typed Query first succeeds through Toxiproxy**, then an independent real native DB witness succeeds. Only then is the TCP proxy disabled. The Query must fail while the direct database remains healthy; after restoration, another production Query must succeed.
- All four cases use live native provider connections, actual Docker databases and a physically separate Testcontainers proxy. They do not use synthetic provider exceptions, and they are not mislabeled as **in-flight COMMIT reply loss** or **distributed failover**.
- Missing DB credentials or a missing service is a **hard CI failure**. CI does not accept no-match filters or skipped physical cases.

## Remaining release blockers (do not overstate this batch)

1. Query least privilege: six-provider DB roles, unqualified/UDF/SQL CLR and autonomous/external effects still require bounded negative proofs or explicit documented unsupported behavior. Existing real native-function tests are necessary but do not certify all effects.
2. Distributed behavior: Redis Sentinel/Cluster promotion and replicated lease fencing, actual DB process crash/failover, global connection pool ceiling, and each provider's truly ambiguous COMMIT/ROLLBACK behavior cannot be inferred from these admission cuts. An unproven guarantee must be disabled/unsupported, never silently asserted.
3. Release verification: six-provider positive and negative ledgers, dependency/CVE and compiler warning review, CodeQL's lack of F# semantic analysis, followed by 18/18 Tests jobs and SqlCore baseline on the actual promoted main push SHA.

**Batch stop rule:** Four added provider physical partition tests pass with zero skips, the existing PostgreSQL/Redis cases remain mandatory, and all 18 jobs plus SqlCore baseline pass for the identical PR head. This closes a bounded real TCP admission-partition evidence gap, **not** the entirety of issue #575 or the v3.1.0 release gate.
