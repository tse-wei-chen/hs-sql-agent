# Changelog

All notable changes to this project will be documented in this file.

## [3.0.0] - Unreleased

This is a **major-version release candidate**, not an already published tag. All first-party NuGet
packages use `VersionPrefix=3.0.0` in this branch. See the [documentation website](https://sql-agent.net/en/docs/).

### G9 / M04 — SQL Server exact integer key equivalence (develop only)

- Support a bounded live-verified, **exact INT/BIGINT** physical source-key
  and target equality pair at each ordinal of one-/two-component SQL Server
  CTE UPDATE/DELETE unique-key proof; mixed `(INT, BIGINT)` ordinals are
  allowed only where the actual source and target column types match.
- Reuse existing Host discovery, closed F# CTE and JOIN AST proof,
  strict row-set Preview/Approval, immutable identity and commit-time
  transaction-held metadata recheck. A private closed F# integer-kind DU
  binds each source/target physical key ordinal; versioned fingerprint
  prevents legacy single-INT approval replay.
- Real SQL Server 2022 tests include values above signed 32-bit maximum,
  PRIMARY KEY and UNIQUE index, 1–3 CTE hops, UPDATE/DELETE, Host approval,
  stale-index recheck and negative `INT`↔`BIGINT` implicit coercions,
  `SMALLINT`/`DECIMAL`/`FLOAT`/`UUID` and computed/nullable keys.
- **Unreleased:** backend `develop` only; documentation stays on site
  `develop`. The production site `main` remains pinned to the last
  published release. No new MCP tool, driver or permission inheritance.

### SQL Core G1/G2, PostgreSQL G3, SQLite/SQL Server/Firebird G4, MySQL/Oracle G5 and PostgreSQL G6 DDL

- Introduce a separate closed F# `CompileDdl` / `TryCompileDdl` compiler,
  typed Schema Plan, native six-dialect SQL renderer, deterministic fingerprint,
  capability checks and fail-closed limited additive DDL grammar.
- Introduce `PreviewDdl` / `TryPreviewDdl` for immutable hypothetical
  Before/After Schema Diff, physical preconditions, dependency impacts, risks
  and explicit unknown states; virtual Catalog is not live authorization.
- Add opt-in **PostgreSQL** `execute_ddl_sql` with read-only preview,
  independent access-key permission and DDL Approval, physical database/cluster
  and schema identity, transactional recheck, native-compiled DDL execution,
  post-execution verification, fail-closed audit and OutcomeUnknown.
- No executable DDL feature is enabled by default. Four existing read tools
  remain selected; both DML and DDL are unselected. No cross-provider,
  conditional or concurrent index DDL execution. A separately enabled,
  independently approved PostgreSQL G6 empty-table DROP is the sole destructive
  exception. PostgreSQL G6/S16 adds only a 2–4 independent CREATE TABLE batch
  under a distinct default-disabled permission and atomic-transaction proof.
- Real PostgreSQL 16 tests cover approved CREATE TABLE / ADD COLUMN / INDEX,
  Preview and rejected Approval without writes, schema drift and forged
  approval fingerprints. Six-provider DQL/DML compatibility gates remain
  mandatory.
- G4 SQLite supports an independent opt-in file-backed transactional DDL adapter
  with main-schema identity and schema-cookie freshness proof.
- G4 SQL Server 2022 adds an independent, default-disabled CREATE TABLE-only Host
  path with explicit schema allowlist, bounded simple INT columns, separate
  DDL approval, SQL Server physical metadata proof, transaction recheck and
  OutcomeUnknown handling. Neither Host adapter inherits DML permissions.
  SQL Server ALTER/INDEX and unsupported DDL operations remain
  disabled pending separate live safety proofs.
- G4 Firebird 5 introduces a separately opt-in, one-table CREATE TABLE Host
  boundary: allowlisted live database GUID/file identity, simple INTEGER
  columns, no active database/DDL triggers, independent DDL approval,
  transaction recheck, native SqlCore SQL only, typed postcondition, explicit
  rollback and OutcomeUnknown. ALTER/INDEX and destructive Firebird DDL remain
  disabled. No default MCP tool selection or DML approval policy changed.

- G5 MySQL 8.4 LTS adds an independently approved, default-disabled,
  one-statement InnoDB CREATE TABLE Host. Requires an explicitly allowed
  physical database, verified MySQL UUID/version/case mode and InnoDB
  default, live table absence, post-execution exact column metadata,
  and a session-level cooperative DDL lock. There is NO user rollback:
  CREATE TABLE implicitly commits, and lost native execution replies are
  OutcomeUnknown and never retried automatically.
  ALTER/INDEX/DROP/CTAS/TEMPORARY remain MySQL Host-disabled.

- G5 Oracle 26ai adds independent, opt-in, own-schema, single-statement
  CREATE TABLE for 1–16 INTEGER columns. Live PDB DBID/GUID, database role,
  schema owner, engine version and DBA_TRIGGERS access are mandatory.
  A cooperating session lock, fresh metadata and separate DDL approval
  guard one F#-compiled command. Oracle implicitly commits DDL: no
  synthetic rollback; lost result/invalid postcondition is OutcomeUnknown.
  No new MCP tool, F# compiler, database driver or default tool grants.

### G6 S15: PostgreSQL high-risk empty-table DROP

- Extend the same closed F# Schema Compiler with typed, qualified
  `DROP TABLE schema.table RESTRICT` only for PostgreSQL. The plan explicitly
  records destructive risk, potential data loss and a physical emptiness
  precondition. Virtual Catalog Preview remains hypothetical.
- Add `AllowDropEmptyTable` (**false by default**) and a separate
  `AllowedDropPrincipals` MCP-key identity allowlist (**empty by default**)
  to the PostgreSQL Host. This requires the existing separately granted
  `execute_ddl_sql` permission, independent human DDL approval, immutable physical evidence,
  policy revision and short-lived approval challenge.
- Enforce an owned, ordinary, persistent, non-partitioned, non-replicated,
  RLS-free and privilege-safe table with 1–16 INTEGER columns and **zero
  rows**. Reject indexes, constraints, view dependencies, triggers,
  identity/default columns, inheritance, owned sequences and other
  unproven effects. Recheck all requirements under PostgreSQL
  ACCESS EXCLUSIVE transaction lock before emitting one compiled native DROP.
- Confirm physical absence before COMMIT; use proven rollback on failure
  and `OutcomeUnknown` when COMMIT or rollback cannot be proven. Nothing
  permits dropping data-bearing tables, using CASCADE, other providers,
  TRUNCATE, DROP COLUMN, or multi-DDL batches.
- PostgreSQL 16 live tests cover preview/no-write, explicit opt-in,
  rejected/forged approval, nonempty and concurrent inserts, dependent
  views, exact absence and independent transactional rollback.

### G6 S16: PostgreSQL atomic CREATE-only DDL Batch

- Introduce an independent, **default-denied** PostgreSQL 16 whole-batch
  execution boundary on the existing `execute_ddl_sql` tool. A caller must
  explicitly opt into `AllowCreateTableBatch`, list the authenticated MCP
  key under `AllowedBatchPrincipals`, grant the existing DDL tool, and use
  the independent `IDdlApprovalProvider`.
- Reuse the F# Script lexer and single-statement Schema Compiler to compile
  **2–4 independent schema-qualified CREATE TABLE statements** into one
  ordered immutable plan fingerprint. Preview remains non-authoritative and
  does not write. Exactly one approval binds every native SQL command,
  source order, database/cluster/schema identity, fresh physical absence,
  principal, policy revision, expiration and a one-use request nonce.
- A fresh PostgreSQL transaction takes cooperative advisory locks in
  deterministic order, rechecks all physical preconditions, runs only
  closed compiled commands and verifies every typed postcondition before
  COMMIT. Confirmed rollback prevents partial application. Uncertain COMMIT
  or rollback is `outcome_unknown` and is never automatically retried.
- Cross-statement dependencies, ALTER, DROP, mixing query/DML/transaction
  statements, five other provider batches and MySQL/Oracle implicit-commit
  partial-outcome modeling remain **unsupported and rejected**. This is
  a proven transactional subset, **not** general multi-DDL execution.

### G6/S16 continuation: PostgreSQL dependent CREATE → ADD → INDEX batch

- Extend only the PostgreSQL 16 atomic Batch Host with a **separately
  default-disabled** `AllowDependentDdlBatch` gate, in addition to the
  existing independent `AllowCreateTableBatch`, `AllowAddColumn`,
  `AllowCreateIndex`, exact MCP principal allowlist and human approval.
- The F# Batch Compiler reuses the shared Script SourceSpans and closed
  single-DDL Compiler to construct one of two **typed and fingerprinted**
  topologies: previously supported unrelated CREATE-only batch, or a
  *single newly created table* with one/two nullable ADD COLUMN effects
  and an optional final ordinary CREATE INDEX referencing only materialized
  column names. No duplicate column, existing table modification,
  cross-target/interleaved CREATE, or unsupported DDL operations.
- Preview retains only **real physical Before snapshots** as evidence;
  cumulative intermediate Before/After are explicitly hypothetical effects
  and must never be used to skip metadata checks. After one human approval,
  each staged operation requires fresh real PostgreSQL metadata preconditions
  and exact postconditions inside the same transaction, with whole-table
  final readback before COMMIT and fail-closed rollback or OutcomeUnknown.
- Tests: positive and adversarial grammar matrix; real PG16 preview/no-write,
  independent approval, default-denied permission, external table drift,
  index namespace collision, final column/index parity and transactional
  rollback. Existing five-other-provider Batch requests stay rejected.

### PostgreSQL old-worker cutover verification

- Introduce a fail-closed, read-only PostgreSQL old-role verifier that
  checks effective table DML rights, transitive NOINHERIT/SET ROLE membership,
  surviving sessions and privileged monitoring authority. Live database
  tests cover grants, revocation, session drain and incomplete evidence.
  This does not attest other SQL providers or production topology.

### SQL Server old-login cutover verifier

- Add an independent read-only SQL Server old-login privilege/session verifier
  requiring full inspection authority, a mapped database user, effective
  table/view/column DML checks and session-drain evidence. The SQL Server
  mandatory real database job verifies direct grants, inherited
  `db_datawriter` access, grant removal, rejected incomplete inspection and
  live old login sessions. No automatic privilege revocation is performed,
  and SQL Server procedural/ownership-chain privileges require separate audit.

### Breaking changes and required migration

- Third-party `IDmlApprovalChallengeStore` implementations must now implement atomic
  `TryPromoteAsync`; this is intentionally source-breaking. A single-statement MCP DML
  operation also uses the aggregate challenge pipeline.
- Redis approval keys now use a `{dml-approval}:` hash-slot segment, and
  challenge values use an explicit `SchemaVersion=1` payload envelope.
  Lua promotion compares these exact versioned bytes. Legacy v2 keys
  are not consumed by v3; **drain old workers and outstanding approvals before cutover**.
  Real-Redis integration tests prevent accidental changes to this contract.
- Approval challenges without the new plan binding are rejected. Re-preview and
  obtain new human approval; do not replay consumed approvals.
- Introduce a versioned encrypted durable DML resume payload with an explicit
  legacy v2 decoder. Unknown versions fail closed instead of silently defaulting
  new approval safety requirements; the protected-payload column remains unchanged.
- Add database-enforced `AdmissionProtocolVersion` to both Admin Store providers
  so v2 binaries omitting the field (default 0) cannot insert new Pending/
  Executing approvals after v3 migration. Historical terminal evidence
  stays intact, and new versioned durable requests explicitly write 1.
- Under selective UPDATE/DELETE policy, demonstrably always-true predicates,
  including `WHERE 1=1`, are denied unless full-table authority is explicit.
- Compiler input/token resource ceilings reject previously accepted oversized SQL.

### G4: SQLite native DDL and live transactional Host adapter

- Fix the SQLite F# native `CREATE INDEX` renderer to place the `main`
  qualifier on the index name, not the `ON` table (SQLite rejects
  `CREATE INDEX idx ON main.table`). Validate against a real SQLite engine.
- Add a separate, default-disabled, file-backed SQLite DDL Host adapter
  reusing the existing `execute_ddl_sql` tool and independent
  `IDdlApprovalProvider`. No additional MCP tools or inherited DML consent.
- Enforce actual `main` file identity, schema cookie/physical SQL snapshot,
  SQLite ASCII-NOCASE identifiers, BEGIN IMMEDIATE transaction freshness,
  exact typed postconditions, explicit rollback and OutcomeUnknown reporting.
- Exercise live CREATE TABLE, nullable ADD COLUMN, plain CREATE INDEX,
  Preview, reject/forged approval, schema drift, case-conflict, and native
  transaction rollback. SQL Server/Firebird DDL execution remain disabled.

### SQL compiler, DML integrity and reliability

- Add opt-in schema-aware name/type proofing and six-provider native DML schema
  attestation with commit-time freshness checks on the same database transaction.
- Bind approved SQL plan shape, per-statement row-set evidence and authorization
  into immutable one-time approval challenges, with atomic multi-statement DML.
- Preserve distinct uncertain COMMIT versus ROLLBACK outcomes throughout durable
  completion and synchronous MCP structured responses. Both are non-retryable.
- Add experimental, opt-in target-enlisted COMMIT witnesses and execution fencing;
  this is not a claim of fleet-wide safe revocation or automatic `Executing` reconciliation.
- Expand positive/negative grammar regressions, real-provider transaction tests,
  Redis integration and fail-closed recovery test coverage.
- Provide a public rollback-only target-fence readiness probe with real
  six-provider transaction tests, including physical absent-witness and
  absent-unique-key failures plus injected witness-error rollback. A table
  with no actual `REQUEST_ID` uniqueness cannot pass readiness; additionally
  reject misleading (REQUEST_ID, ENTRY_KIND) and fingerprint-only unique keys
  and reject generic database errors as uniqueness proof. Document
  provider-specific legacy credential cutover requirements; this does
  not automatically prove old-worker quiescence.
- Integrate the built-in physical probe into **every fenced single/batch
  target DML submission**, requiring the actual connection to prove schema,
  uniqueness and rollback before approved mutations. This has two extra
  rolled-back SERIALIZABLE transactions per opted-in commit, with no
  cached or self-attested readiness.
- Verify the target-enlisted fence's revocation/admission competition,
  COMMIT-witness atomicity and rollback behavior in actual PostgreSQL, MySQL,
  SQLite, SQL Server, Oracle and Firebird integration fixtures. Reject legacy
  Pending approvals on fence-enabled workers instead of downgrading execution.

### Release gates

- Verify full CI and package smoke with the unified `3.0.0` version.
- Finish the v2-to-v3 drain / no-mixed-worker migration verification.
- Document experimental fence limits: passing six-provider database transaction
  tests is **not** proof of mixed-version fleet enrollment, an independently
  authorized revocation operator or safe `Executing` reconciliation.

### Programmatic administration

- Add dedicated **Management API Keys** for machine-to-machine access to the existing `/api` management endpoints. Authenticate using `X-API-Key: hsa_mgmt_...`; human sessions continue to use `Authorization: Bearer <JWT>`.
- Provide an Admin UI for issuing, listing, immediately revoking, and rotating Management API Keys, with one-time plaintext display and an explicit allowlist of canonical API permissions.
- Keep Management API Keys **fully separate** from MCP access keys, MCP tool authorization, and SQL execution. Machine keys cannot operate built-in identity/role administration, edit security policy, or issue other management keys.
- Persist hashed credentials with expiry / usage metadata in the Admin Store; add **SQLite and PostgreSQL schema migrations**, plus permission and credential-isolation tests.
- Remove the repository docs directory; the documentation website already provides project documentation.

### Upgrade notes

- Upgrading to 3.0.0 requires the Admin Store migrations for `ManagementApiKeys` before creating keys.
- Existing JWT admin authentication and MCP keys are unchanged. No new API key is issued automatically.
- Management API Keys are currently scoped by canonical API permissions, **not** by database or SaaS tenant; protect these keys as server-to-server credentials.
- Follow the versioned documentation site's Management API Key guide for the request header and examples.

### Agent-facing MCP contracts

- Added structured MCP output contracts for all five built-in tools while preserving JSON text fallback compatibility for older MCP clients.
- `execute_query_sql` now returns typed rows, provider, row count, duration, and machine-readable error details instead of requiring clients to parse free-form strings.
- `get_schemas`, `get_tables`, and `get_columns` now expose machine-readable schema, table, column, semantic metric, relationship, and metadata structures.
- `execute_dml_sql` now models `committed`, `pending`, `rejected`, and `failed` outcomes explicitly, including approval request/external references, affected rows, returned rows, and structured failures.
- Read/query tools are explicitly marked read-only; the safe MCP key default remains four read/query tools selected with DML opt-in.

### SQL Explain and policy simulation

- Added a compile-only SQL Explain / Policy Simulator backed by the real F# compiler pipeline and the selected database runtime capability profile.
- Query and DML simulations expose rendered target SQL, parameters, referenced tables, compiler decision/boundary, diagnostics with source spans, source/target capability evidence, and policy evidence.
- Simulations can apply an existing MCP key's tool and table scopes without executing SQL, opening transactions, creating approval requests, or mutating target data.
- DML affected-row limits remain runtime-preview evidence and are not misrepresented as compile-time proof.
- Added a Security-page Admin UI for interactive explain/simulation with explicit compile-only labeling.

### Configuration Doctor and deployment readiness

- Added Configuration Doctor under Operability with `Healthy`, `Warning`, and `Error` findings plus concrete remediation guidance.
- Doctor classifies coordination posture as `SingleNode`, `Distributed`, or `Mixed`, checks Redis completeness and fail-closed distributed limiter/concurrency settings, and diagnoses MCP endpoint, Admin Store, Data Protection, DML webhook, OIDC, OTLP, Alert/SIEM, and secret-placeholder posture without returning secret values.
- Expanded Home System Readiness into a five-stage launch path: configuration blockers, database, public MCP endpoint, MCP key, and first governed agent request.
- Doctor warnings remain reviewable while Doctor errors are treated as launch blockers; onboarding may display the public endpoint but never MCP key secrets.
