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
- The existing native Query/UDF security controls are now separately mandatory and no-skip: MySQL **3**, SQL Server **2**, Oracle **4**, Firebird **1** actual live cases (10 total). The audit also rejects missing security assignments and an altered per-provider expected count. Their bounded proof is **not** universal third-party function purity.
- For each case the **production typed Query first succeeds through Toxiproxy**, then an independent real native DB witness succeeds. Only then is the TCP proxy disabled. The Query must fail while the direct database remains healthy; after restoration, another production Query must succeed.
- All four cases use live native provider connections, actual Docker databases and a physically separate Testcontainers proxy. They do not use synthetic provider exceptions, and they are not mislabeled as **in-flight COMMIT reply loss** or **distributed failover**.
- Missing DB credentials or a missing service is a **hard CI failure**. CI does not accept no-match filters or skipped physical cases.

## Follow-up: mandatory physical Query and Redis FailOpen evidence

The next security batch promotes existing end-to-end PostgreSQL 16 Testcontainers
Query Host controls to **11 mandatory cases**, and file-backed SQLite Host
negative controls to **11 mandatory cases**. The four remaining native-provider
UDF/permissions tests already run **3 + 2 + 4 + 1 = 10** mandatory cases.
All six providers therefore have an explicitly owned non-skippable physical
Query negative/positive suite. Exact counts must be verified on the final PR
head; these counts are not certifications of arbitrary third-party code.

Redis SQL admission is now **fail-closed regardless of a legacy caller's
FailOpen option**, and dependency injection refuses to register a Redis-backed
SQL limiter with FailOpen. Runtime Doctor reports it as Error. The real Redis
process-stop Testcontainers test explicitly exercises both FailClosed and
legacy FailOpen options and checks the existing token is revoked and a second
client is refused. This **does not** make Redis asynchronous replication,
Sentinel/Cluster leader promotion, or independent split-brain primaries into
linearizable lease/fencing services.

**Actual split-brain negative control:** two independent real Redis 7
Testcontainers primaries, each with `MaxConcurrentSql=1` and the same
logical key, both grant one lease to independently connected limiter instances.
A second client sharing the first primary is correctly denied. This is
deliberate real counterevidence to any claim of quorum-fenced Redis HA.
Standard-host registration rejects explicit Sentinel `serviceName` or
multi-endpoint Redis SQL coordination; no product guarantee is made for
undetectable DNS/proxy failover or asynchronous promotion behind one endpoint.
Runtime Doctor marks explicit unsupported topologies as Error. Do **not**
set up split-brain routing for SQL concurrency.

## Release dependency, compiler and static-analysis evidence

Within the existing `infrastructure-test` job (retaining **18 required Tests
jobs**), a pinned .NET 10 CLI package advisory scan and a production
`pnpm audit` query current advisory feeds. The reports are uploaded as the
`security-dependency-advisory-evidence` artifact alongside a real Release
build of the ToolBox Host + referenced C# and F# modules and its full warning
log/count. The security parser has nine offline positive/negative regressions;
a missing/malformed advisory response or unreviewed high/critical advisory
fails CI. Low/moderate advisories remain visible for release review.

**Scope limits:** advisory feeds identify known CVEs at scan time, not an
exhaustive supply-chain or exploitability proof. Compiler warnings are
recorded, not silently suppressed; this is an observational warning baseline
pending explicit review of any nonzero count. The CodeQL workflow's Actions,
C# and JavaScript/TypeScript scans **do not semantically analyze F#**. F# safety
requires the dedicated SQL grammar, capability, and differential/parity proof
corpora plus physical tests; do not claim CodeQL certified F# behavior.

No `develop` PR run authorizes release. The existing publish preflight still
requires that the **actual promoted `main` push SHA** has all 18 required
Tests jobs and a successful SqlCore baseline, in addition to a completed
and fully checked #575 release gate. Evidence artifacts must be taken from
that exact final SHA before release publication.

## Remaining release blockers (do not overstate this batch)

1. Query least privilege: six-provider DB roles, unqualified/UDF/SQL CLR and autonomous/external effects still require bounded negative proofs or explicit documented unsupported behavior. Existing real native-function tests are necessary but do not certify all effects.
2. Distributed behavior: Redis Sentinel/Cluster promotion and replicated lease fencing, actual DB process crash/failover, global connection pool ceiling, and each provider's truly ambiguous COMMIT/ROLLBACK behavior cannot be inferred from these admission cuts. An unproven guarantee must be disabled/unsupported, never silently asserted.
3. Release verification: six-provider positive and negative ledgers, dependency/CVE and compiler warning review, CodeQL's lack of F# semantic analysis, followed by 18/18 Tests jobs and SqlCore baseline on the actual promoted main push SHA.

**Batch stop rule:** Four added provider physical partition tests pass with zero skips, the existing PostgreSQL/Redis cases remain mandatory, and all 18 jobs plus SqlCore baseline pass for the identical PR head. This closes a bounded real TCP admission-partition evidence gap, **not** the entirety of issue #575 or the v3.1.0 release gate.
