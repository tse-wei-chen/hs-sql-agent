# Changelog

All notable changes to this project will be documented in this file.

## [3.0.0] - Unreleased

This is a **major-version release candidate**, not an already published tag. All first-party NuGet
packages use `VersionPrefix=3.0.0` in this branch. See the [documentation website](https://sql-agent.net/en/docs/).

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
