# v3.0.0 compatibility contract and migration policy

> This contract describes the **target** for the unpublished v3 line.
> It is a release gate and architecture policy, not a claim that unforeseen
> security issues or dependency changes can *never* require a new major version.

## Default rule from v3.0.0 onward

A supported v3 client, host or approval deployment must continue to work
through later v3.x upgrades without code changes, **unless an explicitly
documented security emergency requires an intentional major-version decision**.
Treat incompatible behavior as a regression rather than rebranding each
feature addition as another major release.

| Compatibility surface | Freeze rule | How v3 evolves without recurring breakage |
| --- | --- | --- |
| Public .NET interfaces | Do not add abstract members to released `IDmlApprovalChallengeStore`, `IDmlCoordinator`, `IDmlTargetExecutionFence` or hosting interfaces | New optional capabilities use **new independently discoverable interfaces** and an explicit fail-closed runtime capability check. Never add a throwing default implementation |
| Public model/DTO APIs | Preserve constructors, overloads, enum wire values, required parameter order and existing properties | Add optional properties or independent V2 models; keep original callable CLR signatures as delegating overloads |
| Hosting composition | Keep public `AddHsSqlAgentCore()` signatures stable; share registration state per `IServiceCollection` | Multiple modules may obtain independent fluent builders without re-registering Runtime/Admin/MVC or bypassing host-vs-built-in authentication exclusivity; later reconfiguration is an explicit error |
| MCP tool surface | Keep five canonical tools and four default read tools; DML remains opt-in | Add supported SQL capability inside existing tool input and typed result contracts; additive optional fields only |
| Durable approvals | Encrypted `hs-sql-agent.dml-resume` envelope, `SchemaVersion=1` | New payload versions require an explicit decoder/upcaster; legacy v2 flat shape is read as version 0 **without inventing consent**; unknown versions are non-executable |
| Distributed Redis approval store | `{dml-approval}:` hash slot and versioned `hs-sql-agent.dml-challenge` envelope, `SchemaVersion=1` | Canonical bytes for Lua compare-and-promote; future protocols require a new version plus rollout/upcast policy. Unknown versions always fail closed |
| Admin Store persisted schema | EF Core SQLite and PostgreSQL migrations with preserved data | Add migrations to both providers; avoid dropping or coercing security evidence. Preserve previously consumed requests and audit history |
| Native SQL providers | Six dialects and positive/negative source/target capability tests | Add grammar with fail-closed capability checks; no silent SQL lowering or unsupported engine fallback |
| Target Fence / worker enrollment | Opt-in `target-fence-v1` bound to protected resume payload | Before **every enrolled single/batch target COMMIT**, the actual open DML connection must pass a rollback-only built-in Fence/Witness proof, including physical uniqueness. After COMMIT acknowledgement the coordinator must independently read both committed Admission and Witness evidence and reject contradictory/missing proof with a non-replayable `DmlCommitOutcomeUnknownException`, not return `Committed=true`. This has two additional rollback-only transactions (SERIALIZABLE for both on five providers; Oracle uses READ COMMITTED solely for the intentional witness UNIQUE-key conflict after its SERIALIZABLE admission/rollback proof) and two post-commit reads per fenced commit; no cached success may justify a different target. Old-worker privilege withdrawal is a separate deployment proof; `Executing` is not automatically reconcilable |

## Versioned storage migration

- **v2 → v3:** start with *quiescence*, not a destructive migration. Stop
  v2 DML preview issuance and drain old workers; complete/reject Pending
  requests or require fresh approval where v3 evidence bindings changed.
  The old Redis prefix and unversioned values cannot be silently promoted.
- **Frozen v1 DTOs:** both durable-resume and Redis challenge envelopes
  serialize explicit private v1 wire projections, **not** evolving CLR
  record objects. Adding an optional .NET property later cannot alter
  canonical Redis Lua equality or implicitly add a security obligation
  to the encrypted v1 approval.
- **Encrypted pending payloads:** the Admin Store column is unchanged.
  V3 writes only the versioned envelope, and the same codec is used by both
  completion and independent witness inspection. Legacy flat JSON uses a
  consciously audited reader. No bulk re-encryption is necessary to read it;
  a legacy request still has to pass **all** current authorization and target
  fencing checks before execution.
- **Future v3 storage version:** ship the new reader/upcaster **before**
  producing new versions. In multi-node deployments, prove all eligible
  processes understand the new format before enabling writes. Unknown schemas
  must never be accepted with defaulted security booleans.
- **DB migrations:** first-party schema migrations must have both
  SQLite and PostgreSQL implementations, startup readiness diagnostics, and
  upgrade tests from a previously released database snapshot. Both Admin
  Store providers now ship a **v3 admission marker migration** that refuses
  Pending/Executing durable approvals; a corresponding startup preflight
  gives a diagnostic before the normal migrator runs. The tests upgrade the
  v2 durable-approval schema, prove unresolved requests block direct EF
  migration, and verify consumed evidence survives after explicitly
  resolving the test fixture. The v3 marker also adds a database-enforced
  `AdmissionProtocolVersion` column (old writes default to 0): any
  Pending/Executing INSERT or UPDATE without protocol version 1+ is denied.
  Both providers test that late legacy writes fail, versioned v3 writes
  succeed and terminal evidence is preserved. This gate does not prevent
  an unupgraded worker from operating against the target DB: deployment
  quiescence remains a separate required proof. Do not
  assume EF migration success proves third-party CLR or Redis compatibility.
- **Uncertain physical writes:** neither missing Admin records nor absent
  Redis keys/witnesses prove rollback. Preserve evidence and require
  operator reconciliation; consumed approvals stay non-replayable.

## CI and release vetoes

1. Run a **public-contract freeze test** on stable interface shapes so a new
   abstract member or removed overload blocks CI. At v3.0.0, source-level
   freeze and a packed external consumer are available. From **v3.0.1 onward**,
   the package smoke runs `.github/scripts/verify-v3-package-abi.sh` and
   uses the official .NET APICompat tool to compare **every packaged first-party
   HsSqlAgent assembly** against its real published 3.0.0 NuGet baseline.
   It fails on signature/parameter incompatibilities and missing baseline
   packages; it never silently skips an unavailable baseline. The 3.0.0
   initial release itself cannot have a pre-existing 3.0.0 package to
   compare against, so full binary freeze activates with subsequent 3.x
   builds. This does not replace behavior or security-contract testing.
2. Run round-trip and unsupported-version tests for every persisted protocol,
   including a real Pending→restart→complete path and independent witness
   inspection.
3. From an **independently generated ASP.NET Core application**, restore
   the packed `HsSqlAgent.Hosting` / transitive `HsSqlAgent.Server` NuGet
   graph and run both first-party standard hosting and modular host mounting.
   Verify host authentication defaults, authorization policy, MVC JSON
   options, Admin API/controller discovery, host endpoint preservation, and
   compile-time third-party `IDmlApprovalChallengeStore` implementation.
   A source-project test is not a substitute for a packaged consumer.
4. Run real Redis Lua compare/promote races and migration isolation tests.
   No sequential register/consume replacement is allowed.
5. Run real six-provider grammar, schema, DML and physical fence tests;
   maintain explicit target environment limitations.
6. Confirm the v2→v3 upgrade procedure with a rolling-deployment **rejection**
   exercise and a coordinated-drain success path before production release.
   Use the [target-fence cutover evidence checklist](v3-target-fence-cutover.md)
   to verify old principal DML denial, existing-session quiescence, and
   rollback-only new-identity readiness for every target/provider.
   PostgreSQL additionally has a read-only legacy-role verifier that
   requires NOLOGIN, no transitive table/column DML or TRUNCATE,
   no callable non-system SECURITY DEFINER functions, and no live sessions.
   The result is a point-in-time PostgreSQL check, not a fleet certificate.
   SQL Server separately supports an opt-in, sysadmin-authorized old-login
   privilege/session verifier using effective per-object impersonation. Its
   evidence is also a point-in-time check, not a fleet certificate. The
   application cannot introspect arbitrary external credentials, and SQLite
   requires OS/process-based file isolation instead of DB user roles.
7. Publish the versioned doc-site migration page, tagged images, package
   metadata, and upgrade notes only for artifacts which actually exist.

**Open before claiming the entire v3 surface is frozen:** comprehensive
third-party host integration, pinned published-package ABI comparison,
automated upgrades of an actual v2 Admin Store fixture for both SQLite/Postgres,
and enforceable fleet-wide worker protocol enrollment. These are release
gate tasks, not proof already delivered by this document.
