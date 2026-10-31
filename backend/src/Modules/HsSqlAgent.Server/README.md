# HsSqlAgent.Server


Secret protection now requires independent Base64-encoded random 32-byte keys for database passwords, DML payloads, and DML connection fingerprints. The standard host binds the `SecretProtection` configuration section; custom runtime registrations must set `options.SecretProtection`. Legacy encrypted passwords must be re-entered and approvals drained before upgrade. See the [documentation website](https://sql-agent.net/en/docs/).

Embeddable MCP SQL Agent components for ASP.NET Core. New applications compose capabilities explicitly; each capability owns only the options it needs. The aggregate `AddHsSqlAgent()` / `UseHsSqlAgent()` pair remains as a compatibility path for existing consumers.

## Install

```bash
dotnet add package HsSqlAgent.Server
```

## Choose the composition you need

| Host scenario | Registration |
|---|---|
| Existing app with its own login/permissions | Runtime + AdminStore + HostAuthorization + AdminApi |
| Existing app that also exposes MCP | Add MCP to the host-auth composition |
| Standalone HsSqlAgent identity/Admin experience | Runtime + AdminStore + BuiltInAuth + AdminApi, optionally MCP/Telemetry/UI |
| Existing consumer using the old aggregate API | `AddHsSqlAgent(...)` / `UseHsSqlAgent()` compatibility preset |

`AddHsSqlAgentCore()` itself is optionless and does not install optional capabilities.

## HTTP surfaces

The current public HTTP contracts are intentionally fixed:

- MCP: `/mcp`
- administration API: `/api`
- administration UI: `/`

Canonical permission paths such as `/auth/role` and `/runtime/db-management` are authorization resource identifiers, not HTTP mount paths. Changing a page or API URL must not silently change its permission identity.

## Cross-module registration consistency

`AddHsSqlAgentCore()` is safe to call from multiple modules using the
**same** `IServiceCollection`. They share the registered feature and
capability-option state, so a second registration does not duplicate Runtime,
Admin Store, filters or middleware configuration. Configuration is only
accepted on the **first** registration of each capability; a later attempt
to override it fails with a clear error. Built-in identity and host
authorization remain mutually exclusive even when they come from different
NuGet integration modules. A second *legacy aggregate-options* initialization
with a different object is rejected rather than silently changing settings.

This registration identity is scoped to `IServiceCollection`, not global to
the process. Independently configured ASP.NET Core hosts remain isolated.

## Existing ASP.NET Core host with its own login and permissions

A host can omit the HsSqlAgent frontend and built-in identity entirely. The host keeps ownership of authentication, authorization, exception handling, telemetry, and controller endpoint mapping.

```csharp
using HsSqlAgent.Server.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAuthentication(/* host schemes */);
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("SqlAgentAdmin", policy =>
    {
        // Add the host application's own requirement/handler here.
        // The handler can inspect HsSqlAgentPermissionResource.Permissions.
    });
});

var hs = builder.Services.AddHsSqlAgentCore();

hs.AddHsSqlAgentRuntime(options =>
{
    // Required: purpose-specific encryption key rings and independent DML fingerprint key.
    builder.Configuration.GetSection("SecretProtection").Bind(options.SecretProtection);
});
hs.AddHsSqlAgentAdminStore(options =>
{
    options.Provider = "Postgres";
    options.ConnectionString = builder.Configuration.GetConnectionString("HsSqlAgent")!;
});
hs.AddHsSqlAgentHostAuthorization("SqlAgentAdmin");
hs.AddHsSqlAgentAdminApi();

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.UseHsSqlAgentAdminApi();
app.MapControllers();
app.Run();
```

In host-authorization mode HsSqlAgent:

- does not publish `AuthController`, `MemberController`, or `RoleController`;
- does not install the HsSqlAgent identity schema;
- does not require JWT, SMTP, password-reset, or OIDC settings;
- does not install an `/api`-wide HsSqlAgent authentication/authorization middleware branch;
- delegates canonical permission checks to the host policy as `HsSqlAgentPermissionResource`;
- leaves the host's authentication defaults, authorization policy provider, MVC JSON options, exception handling, telemetry, and controller mapping under host ownership.

### Add MCP independently

MCP machine access is a separate security boundary from human/admin authorization. Add it only when the host needs an MCP endpoint:

```csharp
hs.AddHsSqlAgentMcp(options =>
{
    options.PublicEndpoint = "https://example.com/mcp";
    options.HmacSecretKey = builder.Configuration["HMAC_KEY"]!; // at least 32 bytes
});

// after builder.Build()
app.UseHsSqlAgentMcp();
```

If `AddHsSqlAgentMcp()` is not selected, MCP options and MCP services are not installed.

## Standalone server / packaged Admin experience

The first-party ToolBox uses the same modular registration surface as package consumers. It explicitly selects runtime, persistence, built-in identity, MCP, administration API, telemetry, controller mapping, UI, and host-wide exception handling.

```csharp
using HsSqlAgent.Server.Extensions;
using HsSqlAgent.Server.Middleware;

var builder = WebApplication.CreateBuilder(args);
var hs = builder.Services.AddHsSqlAgentCore();

hs.AddHsSqlAgentRuntime(options =>
{
    // Required: purpose-specific encryption key rings and independent DML fingerprint key.
    builder.Configuration.GetSection("SecretProtection").Bind(options.SecretProtection);
});

hs.AddHsSqlAgentAdminStore(options =>
{
    options.Provider = "Sqlite";
    options.ConnectionString = "Data Source=hsagent.db";
});

hs.AddHsSqlAgentBuiltInAuth(options =>
{
    options.Jwt.SecretKey = builder.Configuration["JWT_KEY"]!; // at least 32 bytes
});

hs.AddHsSqlAgentMcp(options =>
{
    options.PublicEndpoint = "http://localhost:8080/mcp";
    options.HmacSecretKey = builder.Configuration["HMAC_KEY"]!; // at least 32 bytes
});

hs.AddHsSqlAgentAdminApi();
hs.AddHsSqlAgentTelemetry();

builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

var app = builder.Build();
app.UseExceptionHandler();
app.UseHsSqlAgentMcp();
app.UseHsSqlAgentAdminApi();
app.MapControllers();
app.UseHsSqlAgentAdminUi();
app.Run();
```

Built-in schemes are namespaced (`HsSqlAgent.Jwt`, `HsSqlAgent.ExternalCookie`, `HsSqlAgent.Oidc`) and do not replace the host application's default authentication schemes or default authorization policy. Built-in identity and host-authorization mode are mutually exclusive.

## Capability ownership

New code configures the capability that owns a setting:

| Capability | Options / responsibility |
|---|---|
| `AddHsSqlAgentRuntime()` | `HsSqlAgentRuntimeOptions`: bootstrap, operability, cache, rate limiting, security-policy sync, outbound-delivery sync, SQL concurrency, DML approval store |
| `AddHsSqlAgentAdminStore()` | `HsSqlAgentAdminStoreOptions`: administration DB provider and connection string |
| `AddHsSqlAgentBuiltInAuth()` | `HsSqlAgentBuiltInAuthOptions`: JWT, password reset/SMTP, enterprise identity/OIDC |
| `AddHsSqlAgentHostAuthorization()` | Delegate administration permission checks to an existing ASP.NET Core policy |
| `AddHsSqlAgentMcp()` | `McpOptions`: public MCP endpoint and MCP-key HMAC secret |
| `AddHsSqlAgentAdminApi()` | HsSqlAgent controllers plus controller-scoped validation and exception mapping |
| `AddHsSqlAgentTelemetry()` | `TelemetryOptions`: Prometheus and OTLP exporters |

Dependencies are composed automatically where required. For example, AdminStore ensures Runtime is present; BuiltInAuth ensures AdminStore is present. This does not mean unrelated capabilities are installed.

## Defaults and compatibility

Capability defaults preserve the previous aggregate defaults. Omitting a capability does not allocate or validate that capability's options.

Notable preserved defaults:

| Setting | Default |
|---|---|
| Admin database provider | `Sqlite` |
| Admin database connection | `Data Source=hsagent.db` |
| MCP public endpoint | `http://localhost:8080/mcp` |
| Cache provider | `Memory` |
| Rate limiter provider / failure mode | `Memory` / `FailClosed` |
| Security-policy sync provider | `Memory` |
| Outbound-delivery sync provider | `Memory` |
| SQL concurrency provider / failure mode | `Memory` / `FailClosed` |
| SQL concurrency lease | `30` seconds |
| DML approval store provider | `Memory` |
| JWT issuer / audience | `HS-Agent` / `HS-Agent-Users` |
| Access / refresh token lifetime | `1` minute / `30` days |
| Sign-in lockout | `5` attempts / `15` minutes |
| Password reset expiration | `30` minutes |
| SMTP port / SSL | `587` / enabled |
| Prometheus | enabled on `localhost:9000` |
| Telemetry service name | `hs-sql-agent` |
| Health probe | enabled, `60s` interval, `10s` timeout, max concurrency `4` |
| Security-policy refresh | `30` seconds |

Secret values intentionally default to empty strings. They are validated only when their owning capability is selected: BuiltInAuth requires a JWT secret of at least 32 bytes; MCP requires an HMAC secret of at least 32 bytes.

The existing key prefixes are also preserved, including:

```text
hsqlagent:cache:
hsqlagent:ratelimit:
hsqlagent:security-policy:
hsqlagent:outbound-delivery:
hsqlagent:sql-concurrency
hsqlagent:dml-approval:
```

The first-party ToolBox also preserves the existing configuration fallback chains for distributed runtime connection strings; splitting the options model does not change those deployment semantics.

Regression tests compare the complete legacy-default capability option trees against modular defaults so future refactors cannot silently drift these defaults.

## Legacy compatibility preset

Existing package consumers can continue to use the aggregate options shape:

```csharp
builder.Services.AddHsSqlAgent(options =>
{
    options.AdminDatabaseProvider = "Sqlite";
    options.AdminConnectionString = "Data Source=hsagent.db";
    options.JwtSecretKey = builder.Configuration["JWT_KEY"]!;
    options.HmacSecretKey = builder.Configuration["HMAC_KEY"]!;
    builder.Configuration.GetSection("SecretProtection").Bind(options.SecretProtection);
});

var app = builder.Build();
app.UseExceptionHandler();
app.UseHsSqlAgent().ServeAdminUi();
```

`HsSqlAgentServiceOptions` is retained as a compatibility DTO. The compatibility registration translates it into the same capability-specific option objects used by new integrations. New code should prefer `AddHsSqlAgentCore()` plus explicit capability registration.

## Canonical permissions

The frontend and backend use stable canonical permission identities. Examples:

```text
/auth/role.view
/auth/role.edit
/auth/user.create
/runtime/db-management.view
/runtime/db-management/semantic.edit
/runtime/mcp-keys.create
```

Frontend navigation paths are separate from these identifiers. Relative UI checks such as `v-permission="'edit'"` resolve from the page's declared canonical permission metadata, never from `route.path`.

Host authorization handlers receive the requested canonical permission keys through `HsSqlAgentPermissionResource.Permissions` and can map them into the host application's own authorization model.

## MVC and pipeline ownership

`UseHsSqlAgentAdminApi()` initializes the HsSqlAgent administration surface but deliberately does not call `MapControllers()` in modular host mode. ASP.NET Core `MapControllers()` enables attribute-routed controllers for the MVC application as a whole, so the host must own that decision.

HsSqlAgent validation and exception mapping are attached only to HsSqlAgent controllers. The modular Admin API does not globally install FluentValidation auto-validation, HsSqlAgent ProblemDetails, global JSON converters, or a host-wide HsSqlAgent exception handler.

The packaged Admin UI is optional and currently mounts only at `/`:

```csharp
app.UseHsSqlAgentAdminUi();
```

Arbitrary relocation of `/mcp`, `/api`, or the SPA is intentionally rejected until routing, frontend base paths, assets, and security boundaries can move together.

## Package contents

The Server package currently brings in `HsSqlAgent.SqlCore`, provider abstractions, and all six supported providers: PostgreSQL, MySQL, SQLite, SQL Server, Oracle, and Firebird. Provider package decomposition is separate from the server embedding contract and may be refined independently.

## Compiler observability

`HsSqlAgent.SqlCore` remains telemetry-provider agnostic and returns deterministic `SqlCompileEvidence` with each translated or rejected compile decision. When `AddHsSqlAgentTelemetry()` is selected, the Server package observes that evidence at the runtime boundary:

- structured `ILogger` events contain verdict, decision boundary/code, source/target provider, capability-matrix version and evidence fingerprint;
- when `OtlpEndpoint` is configured, those logs and `sql.compile.decision` trace spans are exported through OTLP;
- `hsqlagent.sql.compiles` is emitted through the existing OpenTelemetry meter and Prometheus exporter with low-cardinality verdict/boundary/decision/provider labels.

Raw SQL, runtime literal/parameter values, full evidence objects, allowed-table snapshots, evidence fingerprints and trace IDs are not used as Prometheus labels. The database-backed `AuditLog` remains the durable governance/audit store rather than a general compiler telemetry store.

Do not commit production keys. Supply HMAC/JWT keys and database credentials through your deployment secret store.

Full project documentation:
https://github.com/tse-wei-chen/hs-sql-agent

## G3: PostgreSQL-only transactional DDL, explicit opt-in

The single MCP tool `execute_ddl_sql` is **disabled and hidden by default**
and is never automatically selected or granted by DML permissions. Existing
four metadata/Query tools remain selected; DML and DDL remain unselected.

## G5 MySQL 8.4 LTS — implicit-commit CREATE TABLE Host (opt-in)

MySQL InnoDB atomic DDL is **not** user-transactional DDL. Only one
unqualified CREATE TABLE (1–16 simple signed INT columns; no constraints,
defaults, temporary tables or CTAS) can be approved through the already
existing execute_ddl_sql MCP tool. All other MySQL Host DDL remains denied.

```csharp
using HsSqlAgent.Server.Extensions;
using HsSqlAgent.Server.Services;

services.AddHsSqlAgentMySqlDdl(new MySqlDdlExecutionOptions
{
    Enabled = true,
    AllowCreateTable = true,
    AllowedDatabases = new HashSet<string>(StringComparer.Ordinal) { "app_database" },
    PolicyRevision = "mysql84-ddl-governance-v1"
});
services.AddSingleton<IDdlApprovalProvider, MyIndependentDdlApprovalProvider>();
// Separately grant execute_ddl_sql on the intended MCP access key.
```

Requires real MySQL 8.4 (not MariaDB/Percona), a valid server UUID,
the exact selected non-system database matching the access key and allowlist,
case-sensitive table names mode 0, session default engine InnoDB, and complete
read access to information_schema. A released preview connection never
authorizes execution. The new execution connection obtains a cooperative
session named lock (not a cross-client CAS), rechecks physical identity,
policy, table absence and approval expiry, executes only the immutable
compiled SQL **once**, and verifies exact InnoDB columns afterward.

No BEGIN, COMMIT or ROLLBACK are issued by this Host. The single CREATE
statement implicitly commits and cannot be undone by user ROLLBACK.
When a native statement may have run but no conclusive response/postcondition
can be proven, the result is outcome_unknown, NEVER a safe-to-retry failure.
External clients are not prevented from altering schema by GET_LOCK. DML
approval never authorizes DDL, startup opt-in alone does not expose the tool,
and the four default read-only selections remain unchanged.

A Hosting application must independently enable it, allowlist schema(s) and
operations, and register its own DDL approval provider *before building DI*:

```csharp
using HsSqlAgent.Server.Extensions;
using HsSqlAgent.Server.Services;
services.AddHsSqlAgentPostgresDdl(new PostgresDdlExecutionOptions
{
    Enabled = true,
    AllowedSchemas = new HashSet<string>(StringComparer.Ordinal) { "app" },
    AllowCreateTable = true,
    AllowAddColumn = true,
    AllowCreateIndex = true,
    PolicyRevision = "ddl-governance-2026-10"
});
services.AddSingleton<IDdlApprovalProvider, MyIndependentDdlApprovalProvider>();
// Implement the provider to obtain independent human approval; return
// the exact request ID, evidence fingerprint and approver identity.
// The MCP access key must separately grant "execute_ddl_sql".
```

Without this opt-in, an explicit key grant, valid PostgreSQL context and a
separately configured `IDdlApprovalProvider`, execution is denied. The
provider is **not** `IDmlApprovalProvider`; no DML consent or session is
reused. `previewOnly=true` captures live metadata without execution or
approval. Pending approvals cannot be resumed to execute: request a new
preview and approval. The result distinguishes `preview`, `committed`,
`rejected`, `pending`, `failed` (confirmed rollback) and
`outcome_unknown` (commit/rollback reply unverified).

Supported: PostgreSQL 12+ with **explicit qualified schema.table** and
independently enabled operation: ordinary CREATE TABLE, nullable ADD COLUMN,
nonunique simple-column CREATE INDEX. Cross-provider, unqualified
search_path, IF EXISTS, CASCADE, DROP, TRUNCATE, CONCURRENTLY, dynamic
SQL and multi-statement requests are unsupported. The PG account must read
physical metadata and `pg_control_system()` for physical cluster identity
(e.g. explicit pg_monitor privilege); unproven metadata fails closed.

The Host verifies actual server/database/cluster identity, table OID,
column types, nullability, index keys and schema namespace collisions.
Approval fingerprints bind native G1 compiled SQL, typed effects, physical
before-snapshot, principal/key, database, policy revision, request ID and
expiry, plus a human-readable Before/ExpectedAfter diff. A fresh connection
re-verifies the target under a PostgreSQL transaction, with a table lock for
ALTER/INDEX, executes only the immutable compiled native SQL once and
checks actual post-DDL metadata before COMMIT. The DDL attempt requires a
durable pre-execution AuditLog write; outcome is audited separately.
Unknown commit or rollback outcomes must be reconciled, never auto-retried.

Exclusive locks, external DDL event triggers and the complete dependency
graph are not magically proven by the table-local metadata snapshot.
The Host does not claim global cross-client atomic CAS, known lock duration
or affected row counts; other database adapters require independent G4/G5
transaction/capability proofs. SqlCore remains driver-free.

## G4 SQLite — independent, file-backed transactional DDL adapter

SQLite DDL shares **the same** existing `execute_ddl_sql` MCP tool and
independent `IDdlApprovalProvider` interface, but **not** PostgreSQL's
physical OID, schema-lock or runtime proof. It must be explicitly opted in:

```csharp
services.AddHsSqlAgentSqliteDdl(new SqliteDdlExecutionOptions
{
    Enabled = true,
    AllowCreateTable = true,
    AllowAddColumn = true,
    AllowCreateIndex = true,
    PolicyRevision = "sqlite-ddl-policy-2026-10"
});
services.AddSingleton<IDdlApprovalProvider, MyIndependentDdlApprovalProvider>();
// Also grant execute_ddl_sql explicitly on the MCP access key.
```

Only **file-backed SQLite 3.35+**, no attached DB, no `:memory:`, no
TEMP and **explicit `main.table`** target. The authenticated database
identity must be the target's absolute file path; the public preview
provides only its SHA-256 identity, schema cookie, typed columns/indexes
and Before/ExpectedAfter. Qualified `CREATE INDEX` must render
`CREATE INDEX "main"."index" ON "table" (...)` (SQLite forbids a
schema-qualified ON table). No second parser and no user SQL execution.

The SQLite-specific adapter checks file identity, `PRAGMA main.schema_version`,
the complete target table SQL, table columns and physical index keys.
It releases the preview connection before human approval, binds the
physical snapshot and actor/policy into an expiring single-request
fingerprint, then starts a **BEGIN IMMEDIATE** transactional writer
reservation before rechecking metadata. Only the F# compiled command
is executed, with expected schema-cookie delta and typed postcondition
verification before COMMIT; failed or uncertain rollbacks are not
reported as committed, and an unknown COMMIT is **OutcomeUnknown**.

No DML permission or consent is inherited. Startup opt-in,
`execute_ddl_sql` key grant, separate DDL approval provider and durable
pre-execution audit are all required. Only the G1 additive operations
are executable; unknown operations, expression/partial indexes,
unmodeled virtual targets, schema drift and implicit name resolution
fail closed. SQL Server and Firebird have separately opt-in, restricted Host adapters described below.

## G4 SQL Server 2022 — restricted transactional DDL (independent opt-in)

The existing **single** `execute_ddl_sql` MCP tool has a separate SQL
Server adapter. Installation and ordinary Query/DML rights never enable it.
Only a qualified `dbo.table` (or another explicitly allowlisted user schema)
with **1–16 simple INT columns**, no constraints/defaults/identity, is in the
initial SQL Server Host executable subset. `ALTER TABLE`, `CREATE INDEX`,
`DROP`, conditional/batch DDL, unsupported types or effects are rejected.
SQL Server parser / Catalog support is not evidence of Host execution support.

```csharp
using HsSqlAgent.Server.Extensions;
using HsSqlAgent.Server.Services;
services.AddHsSqlAgentSqlServerDdl(new SqlServerDdlExecutionOptions
{
    Enabled = true,
    AllowedSchemas = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dbo" },
    AllowCreateTable = true,
    PolicyRevision = "sqlserver-ddl-approval-v1"
});
services.AddSingleton<IDdlApprovalProvider, MyIndependentDdlApprovalProvider>();
// Separately grant execute_ddl_sql to the intended MCP access key.
```

The SQL Server 2022+ adapter is distinct from the PostgreSQL/SQLite runtimes.
An enabled Host requires an actual non-system database, SQL Server 2022+
version, proven case-insensitive database collation, existing schema ID,
complete permission to inspect DDL triggers, and an absent target object.
Preview reads physical `sys.*` metadata without mutations; an expiring,
one-time independent approval binds database/server identity, schema/object
snapshot, actor, compiled native plan, policy and readable Before/After.
The approval connection is released before the human review. At execution,
a new connection takes a transaction-owned application lock, rechecks live
metadata inside a SERIALIZABLE transaction, executes **only** the SqlCore
compiled native command, verifies actual column types/nullability before
commit, then records the committed result. A failure with proven rollback
is not success; uncertain COMMIT/rollback returns `outcome_unknown` and
must be reconciled, **never automatically retried**.

Transaction application locks coordinate this Host's requests; they are not
a global compare-and-swap against unrelated external SQL Server DDL.
External schema changes or triggers without sufficient evidence fail closed.
No public SQL Core DTO can authorize physical execution; the separate MCP
grant, DDL approval, and durable pre-execution audit remain mandatory.
The four default read-only tools remain selected; DML and DDL are not.

## G4 Firebird 5 — CREATE TABLE-only transactional DDL (independent opt-in)

The existing `execute_ddl_sql` tool also supports an **independently
configured Firebird 5** Host. The first executable slice permits one
**unqualified** `CREATE TABLE name (...)` with **1–16 simple INT/INTEGER
columns**. No constraints, defaults, ALTER, INDEX, DROP, implicit schema,
dynamic SQL, or batch statements are executable through this adapter.

```csharp
using HsSqlAgent.Server.Extensions;
using HsSqlAgent.Server.Services;
// Provision this GUID from an authenticated Firebird DBA session:
// SELECT MON$GUID FROM MON$DATABASE
services.AddHsSqlAgentFirebirdDdl(new FirebirdDdlExecutionOptions
{
    Enabled = true,
    AllowCreateTable = true,
    AllowedDatabaseGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "<approved-physical-firebird-database-guid>"
    },
    PolicyRevision = "firebird-ddl-governance-v1"
});
services.AddSingleton<IDdlApprovalProvider, MyIndependentDdlApprovalProvider>();
// Grant execute_ddl_sql explicitly on the intended MCP access key.
```

The Host requires verifiable Firebird 5 engine identity, SQL dialect 3,
physical `MON$GUID` / `MON$FILE_ID` / database creation metadata,
the configured database GUID allowlist, reliable system catalog reads,
an absent target, and **no active user database/DDL triggers**. Physical
identifiers and paths are hashed before exposure to MCP. The preview is
read-only and releases its connection before requesting a separately
authorized human decision with an expiring, single-request fingerprint.

A new connection starts a Firebird transaction, rechecks the same physical
identity, policy, target absence and trigger boundary, executes **only**
the compiled SqlCore native command, and compares exact physical columns
before committing. A verified rollback cannot be reported as success;
an uncertain COMMIT or rollback returns `outcome_unknown`, not an
automatic retry. This does not promise a global compare-and-swap against
unrelated Firebird DDL writers. The startup opt-in, explicit key grant,
durable pre-execution audit, and independent `IDdlApprovalProvider`
are mandatory. The four default read-only tools are unchanged; DML and
DDL remain unselected.


## G5 Oracle 26ai — independently approved, implicit-commit CREATE TABLE

This Host feature reuses the same disabled-by-default `execute_ddl_sql`
MCP tool and F# `CompileDdl` native SQL renderer. The initial executable
subset is **one unqualified CREATE TABLE with 1–16 simple INTEGER columns**
in the exact current user schema. It excludes custom constraints, defaults,
indexes, CTAS, ALTER, DROP, dynamic SQL, batch SQL and other Oracle versions.

```csharp
using HsSqlAgent.Server.Extensions;
using HsSqlAgent.Server.Services;

services.AddHsSqlAgentOracleDdl(new OracleDdlExecutionOptions
{
    Enabled = true,
    AllowCreateTable = true,
    AllowedPdbNames = new HashSet<string>(StringComparer.Ordinal) { "FREEPDB1" },
    AllowedSchemas = new HashSet<string>(StringComparer.Ordinal) { "SQLCORE" },
    PolicyRevision = "oracle-ddl-independent-governance-v1"
});
services.AddSingleton<IDdlApprovalProvider, MyIndependentDdlApprovalProvider>();
// Separately grant execute_ddl_sql to this MCP key.
// All four default read-only tools remain selected; DML/DDL are not.
```

The designated Oracle Host account must be a **non-system user** with
`CURRENT_SCHEMA = SESSION_USER`, connecting to an explicitly allowlisted,
writable, primary Oracle 26ai (23.26+) PDB. Provision only the necessary
dictionary and cooperative-lock privileges, using a DBA-owned deployment
step (not an LLM-created or auto-granted privilege):

```sql
GRANT SELECT ON SYS.V_$DATABASE TO SQLCORE;
GRANT SELECT ON SYS.V_$PDBS TO SQLCORE;
GRANT SELECT ON SYS.DBA_TRIGGERS TO SQLCORE;
GRANT EXECUTE ON SYS.DBMS_LOCK TO SQLCORE;
```

These `SELECT` grants are required to prove CDB/PDB identity and complete
visibility of active schema/database DDL triggers. Missing access is a
hard denial, not a fallback to `ALL_TRIGGERS`. The read-only Preview
releases the first connection prior to separate human approval. A fresh
connection takes a session-held cooperative `DBMS_LOCK` (which survives
DDL implicit COMMIT), rechecks physical metadata, plan/policy/expiration,
then executes only the typed compiled statement and verifies actual
`USER_OBJECTS` / `USER_TAB_COLS` state.

**Oracle DDL performs an implicit COMMIT before and after the statement.**
This Host never uses `BeginTransaction`, never promises rollback, and
never automatically retries an unknown outcome. After native execution
starts, a lost response or postcondition mismatch is `outcome_unknown`
and requires live DBA reconciliation. The Host's cooperative session
lock does **not** prevent unrelated Oracle clients from changing objects.
Independent access-key grant, DDL approval, pre-execution durable audit,
and DB permission checks are all required; install alone enables nothing.

## G6 PostgreSQL — separately approved high-risk DROP of an empty table

The existing `execute_ddl_sql` can now **optionally** accept a single
`DROP TABLE schema.table RESTRICT`. This is a destructive operation and
is **not enabled** by `AllowCreateTable`, ordinary DML permission,
or a default DDL installation. Enable it only for an isolated, dedicated
PostgreSQL 12+ application schema, and explicitly grant `execute_ddl_sql`
to the intended MCP access key.

```csharp
using HsSqlAgent.Server.Extensions;
using HsSqlAgent.Server.Services;

services.AddHsSqlAgentPostgresDdl(new PostgresDdlExecutionOptions
{
    Enabled = true,
    AllowedSchemas = new HashSet<string>(StringComparer.Ordinal) { "staging_cleanup" },
    AllowDropEmptyTable = true, // false by default, independent from additive DDL
    AllowedDropPrincipals = new HashSet<string>(StringComparer.Ordinal)
    {
        "mcp-key:123" // authenticated ID of the separately authorized MCP key
    },
    PolicyRevision = "postgres-g6-drop-empty-governance-v1"
});
services.AddSingleton<IDdlApprovalProvider, MyIndependentDdlApprovalProvider>();
// Separately grant execute_ddl_sql to the desired MCP key.
```

The Host also requires that the authenticated MCP key principal match
`AllowedDropPrincipals`. Merely granting `execute_ddl_sql` to a key does
**not** authorize destructive DROP; changing this allowlist invalidates
outstanding approvals.

The MCP preview exposes `Risk=Destructive`, `MayLoseData=true`, and the
human-readable Before/After impact summary. None of these is an execution grant.

**This is not a general DROP TABLE permission.** Preview and execution
both demand an actual **empty** ordinary PostgreSQL table with 1–16
INTEGER columns, direct owner privilege, no secondary indexes,
constraints, dependent views, triggers, defaults/identity,
inherited/partitioned tables, RLS, publication or unproven schema
effects. Preview is read-only; any change in those properties before
execution is rejected. The approved statement, schema identity,
physical PostgreSQL cluster, actor, policy and short-lived nonce are
bound to one independent human approval challenge.

Execution acquires an `ACCESS EXCLUSIVE` lock inside a real
PostgreSQL transaction, rechecks the exact table OID, permissions,
dependencies and zero-row condition, issues the **F#-compiled native
RESTRICT command only once**, checks that the table is absent, then
commits. A confirmed rollback prevents reported success. Lost COMMIT
or rollback outcomes are `outcome_unknown`; **never retry automatically**.
The risk warning explicitly states that after COMMIT the schema
definition is permanently deleted. No nonempty tables, CASCADE,
`IF EXISTS`, multi-table DROP, or MySQL/Oracle DROP are enabled.

The four default read-only MCP tools remain selected. DML and DDL
remain unselected by default. Rollback of the feature switch means
disabling `AllowDropEmptyTable`; it does not restore a table already
committed as deleted.

## G6 S16 — PostgreSQL atomic 2–4 statement CREATE TABLE Batch (opt-in)

The existing `execute_ddl_sql` MCP tool now recognizes an intentionally
limited **single-schema PostgreSQL 16 atomic batch**: 2–4 independently
compiled `CREATE TABLE schema.name (…)` statements, each with a different
table name. There are **no cross-statement dependencies** in this first
proven subset. No new MCP tools, SQL parsers, DML approval or inferred
permissions are introduced. The F# batch plan is not a promise of
atomicity until PostgreSQL actually commits the transaction.

```csharp
using HsSqlAgent.Server.Extensions;
using HsSqlAgent.Server.Services;

services.AddHsSqlAgentPostgresDdl(new PostgresDdlExecutionOptions
{
    Enabled = true,
    AllowCreateTable = true,
    AllowCreateTableBatch = true,  // false by default, distinct from single DDL
    AllowedBatchPrincipals = new HashSet<string>(StringComparer.Ordinal)
    {
        "mcp-key:123" // authenticated MCP key ID, not caller SQL
    },
    AllowedSchemas = new HashSet<string>(StringComparer.Ordinal)
    {
        "staging_cleanup"
    },
    PolicyRevision = "postgres-g6-atomic-batch-v1"
});
services.AddSingleton<IDdlApprovalProvider, MyIndependentDdlApprovalProvider>();
// Also separately grant execute_ddl_sql to the designated MCP access key.
// Do not select DML/DDL tools by default.
```

Example input for the **same existing** `execute_ddl_sql` tool:

```sql
CREATE TABLE staging_cleanup.import_a (id INTEGER NOT NULL);
CREATE TABLE staging_cleanup.import_b (id INTEGER NOT NULL);
```

`previewOnly=true` returns the ordered compiled SQL statements, one
batch plan fingerprint, every physical Before observation and every
hypothetical After plus a human-readable whole-batch diff. Preview
never executes SQL or obtains approval. On `previewOnly=false`, the
Host first requires the **batch-only key allowlist**, then one independent
human approval bound to the **entire ordered plan**. A new PostgreSQL
connection rechecks the original server, schema identity, object absence,
approval expiry and policy. Every compiled native CREATE and its physical
postcondition execute under **one PostgreSQL transaction**; the Host
confirms every final table before COMMIT. A confirmed rollback restores
the state when execution fails. A lost COMMIT or uncertain rollback
returns `outcome_unknown`, not a false success/failure, and must **never
be retried automatically**.

Cross-schema targets, repeated table names, statement counts outside
2–4, ALTER/INDEX/DROP, dependencies between statements, data-writing
CTAS, DML, `GO` and other Provider DDL batches remain blocked. In
particular, MySQL and Oracle **implicit commits prohibit claiming this
PostgreSQL atomic batch contract**. This Host opt-in does not change
the four default read-only tool selections or the default-unselected
DML/DDL choices. Disable `AllowCreateTableBatch` to stop *future*
batches; already-committed schema changes are not undone.
