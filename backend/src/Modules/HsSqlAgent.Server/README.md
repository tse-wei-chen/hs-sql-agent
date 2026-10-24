# HsSqlAgent.Server

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

hs.AddHsSqlAgentRuntime();
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

hs.AddHsSqlAgentRuntime();

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

### Bootstrap MCP key handling

Bootstrap provisioning no longer prints newly generated MCP keys to stdout. On the
**first** provisioning of a bootstrap key, configure `Bootstrap:Databases:*:McpKeys:*:Key`
from a protected configuration source (for example a mounted Kubernetes Secret or
an environment variable bound through ASP.NET Core configuration). A missing key
fails startup before bootstrap records are written. On later restarts, omitting
`Key` preserves the existing stored key hash; providing a new `Key` intentionally
reconciles/rotates it. Do not put real credentials into source-controlled JSON.

Database connection-test API errors and persisted health statuses now expose only
fixed, non-sensitive diagnostics. Detailed driver error text may contain credentials
or complete connection strings and is intentionally not logged or returned.
If a key was previously printed by bootstrap, revoke/replace it and review access
to retained container and centralized logs.

### Configure an independent encryption key ring (inactive read/write rollout)

The **opt-in** `SecretEncryption` configuration supports a separate AES-256-GCM
key ring per purpose. A Base64 key must decode to **32 random bytes**, never
reused across key IDs or purposes or sourced from the MCP HMAC signing key.

For the StandardHost, configure via protected environment variables or mounted
secrets (examples use placeholders, not production keys):

```text
SecretEncryption__Enabled=true
SecretEncryption__Purposes__DatabasePassword__ActiveKeyId=db_k1
SecretEncryption__Purposes__DatabasePassword__Keys__db_k1=<BASE64_32_BYTE_RANDOM_KEY>
SecretEncryption__Purposes__DurableDmlPayload__ActiveKeyId=dml_k1
SecretEncryption__Purposes__DurableDmlPayload__Keys__dml_k1=<DIFFERENT_BASE64_32_BYTE_RANDOM_KEY>
```

For embedded hosts, configure `options.SecretEncryption` in
`AddHsSqlAgentRuntime(options => ...)`. This registers the new protector in DI
only when enabled, validates key shape at registration, and rejects the legacy
MCP HMAC-derived AES key if the MCP settings are present. Do **not** remove
previous key IDs until all old records have been safely migrated and verified.

**Important:** Enabling the key ring does not change the persisted Admin
Store ciphertext writer in this phase. Deployment needs a coordinated
dual-reader rollout before v2 writes are activated; a mixed-version fleet
must not encounter v2 ciphertext on an old-only reader.

### Database password dual-read preparation

Every built-in database password **read** path now recognizes legacy ciphertext
and v2 AES-GCM using the persisted Admin Store DB ID as AAD record identity.
MCP authentication, Admin metadata, SQL tools, runtime tests, health checks,
Durable DML resume and witness inspection are included. Missing key rings,
wrong record identities and unknown versions fail closed.

The Durable DML completion handler and independent commit-witness verification
also share a version-aware, fail-closed **payload reader**. Its AAD identity is
the immutable `DmlApprovalRequestState.RequestId`, with purpose
`DurableDmlPayload`. Existing approval **payload writes and connection
fingerprints remain legacy**. Pending/Executing approvals are neither changed
nor replayed by enabling this reader.

Existing Admin Store password **writers remain legacy** until old-only deployed
instances have been upgraded. Do not manually migrate DB passwords in a mixed-
version fleet. Re-encryption can also affect Durable DML approval connection
evidence, so pending/executing approvals must be drained or handled first.

### Versioned secret protection (infrastructure; not active for persisted records yet)

`Common.Services.VersionedSecretProtector` and `SecretKeyRing` provide an
auditable AES-256-GCM v2 envelope with purpose-specific 32-byte keys, key IDs,
immutable-record AAD binding, explicit legacy decryption, and a re-protection
operation for rotation. The wire format is
`v2:<key-id>:<base64(nonce + tag + ciphertext)>`.

**This is not yet the active Admin Store encryption path.** The current DB
password and Durable DML Approval readers/writers still use the original
unversioned `ICryptoService`. Do not store v2 ciphertext into those columns,
or remove the existing MCP HMAC secret, before all readers/writers and
deployments have been migrated. In particular, preserve DML connection
fingerprints and pending/executing request semantics. Encryption keys must be
32-byte random values from protected configuration, independent across
DB passwords / DML payloads / sensitive extra settings; do not reuse HMAC
signing keys as new AES keys. The dedicated crypto unit tests run in CI.

**Migration note:** This hardening stage does not rotate the shared legacy MCP
HMAC secret or re-encrypt stored database credentials / pending DML approvals.
Changing that secret without a coordinated migration can make saved credentials,
MCP keys and durable approval payloads unusable.

Full project documentation:
https://github.com/tse-wei-chen/hs-sql-agent
