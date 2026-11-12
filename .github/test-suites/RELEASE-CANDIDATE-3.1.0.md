# 3.1.0 — Develop Release Candidate acceptance contract

**Release stage: PREPARE / develop only.** This is a finite, reproducible
security and integration acceptance plan. A green release-candidate check
does **not** merge into `main`, close #575, create a Git tag, publish NuGet
packages, create a GitHub Release, or push an OCI/Docker image. Promotion
to `main` is a separate, owner-authorized action.

## Candidate quality gates (all mandatory)

| Gate | Required physical or executable proof | Evidence source |
|---|---|---|
| Query minimum privilege / six providers | Positive legitimate Query and negative unauthorized table, native UDF and callback paths; PostgreSQL **11**, SQLite **11**, MySQL **3**, SQL Server **2**, Oracle **4**, Firebird **1**; **32 cases, 0 skips** | Existing six-provider Testcontainers/real-DB/file-backed cases, CI `server-test` and four dedicated SQL provider jobs |
| SQL physical network partition | PostgreSQL real in-flight Query/TCP fault; MySQL, SQL Server, Oracle and Firebird healthy→cut→fail→restore; Redis real TCP partition and process stop with lease revocation | Testcontainers / pinned Docker service containers; each exact-count/no-skip check |
| Distributed SQL admission | Redis `FailOpen` never grants a lease; Sentinel/multi-endpoint SQL coordination is unsupported; two independent Redis primaries each grant locally (physical split-brain **negative proof**); `MaxConcurrentSql` bounds *admitted operations*, **not** all DB connections | Redis 7 split-brain and outage Testcontainers, DI and Doctor tests, explicit deployment contract |
| Durable DML outcome | Non-replayable `outcome_unknown`, original approval ID, separated COMMIT/ROLLBACK categories; six-provider native transaction + simulated lost acknowledgment, PostgreSQL real post-durable COMMIT wire EOF, independent DB readback | Six real-provider Custom MCP suites plus PostgreSQL socket fault case; **do not** relabel the synthetic cases as true network faults |
| Four kinds of CI evidence | All **18 named Tests jobs** pass on the exact `develop` *push* SHA, a separate **one-job** SqlCore baseline passes on that SHA, and the same Tests run uploaded nonempty `security-dependency-advisory-evidence` | `.github/workflows/test.yml`, `sqlcore-comparison.yml`, read-only `develop-release-candidate.yml`; verification artifact `develop-3.1.0-release-candidate-proof` |
| Dependencies and compiler | Actual current NuGet and production pnpm advisory results; missing/invalid scan or high/critical vulnerability fails closed; compiler warning baseline recorded, not erased; low/moderate reviewed explicitly before promotion | Existing infrastructure-test `audit-dependency-security.sh` and uploaded compiler log |
| Source and publication contract | `VersionPrefix=3.1.0`, `CHANGELOG.md` contains `[3.1.0] - Unreleased`; supported DB identities/limitations documented; exact-`main` release gates unchanged | `verify-develop-release-candidate.sh`, Query security contract, issue #575 |

The `Develop 3.1.0 release candidate (read-only)` workflow is triggered by a
**push to `develop`** and waits (bounded to 30 minutes) for the two independent
`develop`-push CI workflows. It refuses stale SHA, wrong repo/branch,
PR-only runs, old green runs hidden by newer red/pending runs, missing jobs,
failed jobs, missing security artifacts, expired artifacts, and missing
version/contract files. It uploads a machine-readable JSON record with
`publicationAuthorized=false`. The verifier has mandatory offline positive
and negative regression tests in the existing infrastructure CI job.

## Supported 3.1.0 security scope

- Six-provider Query-only security requires **actual least-privileged target
  DB identities**, least-privilege object/table access, no elevated routine
  EXECUTE or unsafe extensions, and an appropriately restricted Host;
  see [the provider-specific role matrix](query-security-deployment-contract.md).
  A modeled `SELECT` AST alone does **not** prove DB-level or external-effect
  read-only behavior.
- Redis SQL concurrency is supported only with **one authoritative standalone
  Redis primary** and `FailClosed`. Sentinel, Cluster, multi-endpoint
  failover, split-brain, separate DNS/proxy promotion, global session/pool
  ceilings and arbitrary external routine purity have **no strong guarantee**.
  Unsupported configurations and runtime promises must not be advertised as
  proven; see [physical fault evidence](physical-fault-release-evidence.md).
- All six SQL providers' Custom MCP DML paths require preview→approval→
  revalidation→native commit. Post-native uncertain acknowledgment is distinct
  from committed success; **never automatically replay**. A physical
  provider process crash or partition cannot be inferred from synthetic
  post-native lost acknowledgments.

## Recorded develop evidence before this candidate gate

The preceding hardening delivery [#630](https://github.com/tse-wei-chen/hs-sql-agent/pull/630)
succeeded at final PR HEAD `f0acb395fe30de18241df6b7c19881e1311c0b26`:
[18/18 Tests jobs](https://github.com/tse-wei-chen/hs-sql-agent/actions/runs/43207831495),
[SqlCore baseline](https://github.com/tse-wei-chen/hs-sql-agent/actions/runs/43207831487),
the 32 mandatory Query cases, full real Redis fault/split-brain checks,
NuGet zero known vulnerable advisories, pnpm production high/critical zero,
and **215 distinct compiler warnings** (predominantly F# nullability) retained
as a reviewable warning baseline. These are **historical PR-head evidence**,
not a claim that a future `develop` or `main` SHA passed.

The codebase uses GitHub CodeQL for configured Actions, C# and
JavaScript/TypeScript analysis. **CodeQL does not semantically analyze F#**;
the negative SQL capability, grammar, differential and real-provider suites
provide independent bounded coverage, not an F# CodeQL claim.

## What is deliberately not performed at this stage

The existing **publication** gates still require an owner-approved promotion
to `main`, successful **18/18 Tests + SqlCore baseline on the *actual promoted
main push SHA***, and completed/fully-checked #575. Those facts do not exist
while the user is preparing release solely on `develop`. Therefore the
candidate gate does **not** close #575, overwrite/check the final main-only
checkbox, authorize NuGet or Docker publication, or bypass either existing
release gate. Mark this stage READY only when the exact-`develop` candidate
workflow finishes green; otherwise treat it as BLOCKED and inspect its logs.
