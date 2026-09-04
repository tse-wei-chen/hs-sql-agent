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

Full project documentation:
https://github.com/tse-wei-chen/hs-sql-agent
