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
| MCP tool surface | Keep five canonical tools and four default read tools; DML remains opt-in | Add supported SQL capability inside existing tool input and typed result contracts; additive optional fields only |
| Durable approvals | Encrypted `hs-sql-agent.dml-resume` envelope, `SchemaVersion=1` | New payload versions require an explicit decoder/upcaster; legacy v2 flat shape is read as version 0 **without inventing consent**; unknown versions are non-executable |
| Distributed Redis approval store | `{dml-approval}:` hash slot and versioned `hs-sql-agent.dml-challenge` envelope, `SchemaVersion=1` | Canonical bytes for Lua compare-and-promote; future protocols require a new version plus rollout/upcast policy. Unknown versions always fail closed |
| Admin Store persisted schema | EF Core SQLite and PostgreSQL migrations with preserved data | Add migrations to both providers; avoid dropping or coercing security evidence. Preserve previously consumed requests and audit history |
| Native SQL providers | Six dialects and positive/negative source/target capability tests | Add grammar with fail-closed capability checks; no silent SQL lowering or unsupported engine fallback |
| Target Fence / worker enrollment | Opt-in `target-fence-v1` bound to protected resume payload | Gate rollout on all eligible workers being upgraded; do not infer quiescence from heartbeat, timeout or missing witness. `Executing` remains unresolvable without physical fencing proof |

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
  upgrade tests from a previously released database snapshot. This branch
  exercises the v2 durable-approval schema → latest migration path against
  both real providers, asserting that consumed approvals and protected
  evidence remain intact. Do not
  assume EF migration success proves third-party CLR or Redis compatibility.
- **Uncertain physical writes:** neither missing Admin records nor absent
  Redis keys/witnesses prove rollback. Preserve evidence and require
  operator reconciliation; consumed approvals stay non-replayable.

## CI and release vetoes

1. Run a **public-contract freeze test** on stable interface shapes so a new
   abstract member or removed overload blocks CI. NuGet ABI/package smoke
   remains a separate required gate.
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
7. Publish the versioned doc-site migration page, tagged images, package
   metadata, and upgrade notes only for artifacts which actually exist.

**Open before claiming the entire v3 surface is frozen:** comprehensive
third-party host integration, pinned published-package ABI comparison,
automated upgrades of an actual v2 Admin Store fixture for both SQLite/Postgres,
and enforceable fleet-wide worker protocol enrollment. These are release
gate tasks, not proof already delivered by this document.
