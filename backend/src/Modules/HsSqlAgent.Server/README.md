# HsSqlAgent.Server

Embeddable MCP SQL Agent components for ASP.NET Core. New applications compose capabilities explicitly; each capability owns only the options it needs. The aggregate `AddHsSqlAgent()` / `UseHsSqlAgent()` pair remains as a compatibility path for existing consumers.

## Install

```bash
dotnet add package HsSqlAgent.Server
```

## HTTP surfaces

The current public HTTP contracts are intentionally fixed:

- MCP: `/mcp`
- administration API: `/api`
- administration UI: `/`

Canonical permission paths such as `/auth/role` and `/runtime/db-management` are authorization resource identifiers, not HTTP mount paths. Changing a page or API URL must not silently change its permission identity.

## Standalone server

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

Capability defaults preserve the previous aggregate defaults. Omitting a capability does not allocate or validate that capability's options. For example, a host-authorization integration never needs JWT, SMTP, OIDC, or password-reset settings.

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

In host-authorization mode HsSqlAgent does not publish its built-in `AuthController`, `MemberController`, or `RoleController`, does not install its JWT identity schema, and does not add an `/api`-wide authentication/authorization middleware branch. HsSqlAgent permission checks delegate to the configured host policy and pass canonical permission keys in `HsSqlAgentPermissionResource`.

If the same host also wants MCP, add it separately. MCP machine access remains a separate security boundary from human/admin authorization:

```csharp
hs.AddHsSqlAgentMcp(options =>
{
    options.PublicEndpoint = "https://example.com/mcp";
    options.HmacSecretKey = builder.Configuration["HMAC_KEY"]!;
});

// later
app.UseHsSqlAgentMcp();
```

## Capability options

New code configures the capability that owns a setting:

- `HsSqlAgentRuntimeOptions`: bootstrap, operability, cache, rate limiting, security-policy sync, outbound-delivery sync, SQL concurrency, DML approval store.
- `HsSqlAgentAdminStoreOptions`: administration database provider and connection string.
- `HsSqlAgentBuiltInAuthOptions`: JWT, password reset/SMTP, enterprise identity/OIDC.
- `McpOptions`: public MCP endpoint and MCP-key HMAC secret.
- `TelemetryOptions`: Prometheus and OTLP settings.

Defaults remain the same as the legacy aggregate contract. Examples include SQLite + `Data Source=hsagent.db`, Memory-backed cache/synchronization stores, fail-closed rate/concurrency modes, JWT issuer `HS-Agent`, JWT audience `HS-Agent-Users`, Prometheus on `localhost:9000`, and the existing key prefixes/timeouts.

### Legacy compatibility preset

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

`HsSqlAgentServiceOptions` is retained as a compatibility DTO. The compatibility registration translates it into the same capability-specific option objects used by new integrations.

## Built-in identity as an explicit capability

If an application wants HsSqlAgent's own JWT/member/role model, opt into it explicitly:

```csharp
var hs = builder.Services.AddHsSqlAgentCore();
hs.AddHsSqlAgentRuntime();
hs.AddHsSqlAgentAdminStore(options =>
{
    options.Provider = "Sqlite";
    options.ConnectionString = "Data Source=hsagent.db";
});
hs.AddHsSqlAgentBuiltInAuth(options =>
{
    options.Jwt.SecretKey = builder.Configuration["JWT_KEY"]!;
});
hs.AddHsSqlAgentAdminApi();
```

Built-in schemes are namespaced (`HsSqlAgent.Jwt`, `HsSqlAgent.ExternalCookie`, `HsSqlAgent.Oidc`) and do not replace the host application's default authentication schemes or default authorization policy. Built-in identity and host-authorization mode are mutually exclusive.

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

## Package contents

The Server package currently brings in `HsSqlAgent.SqlCore`, provider abstractions, and all six supported providers: PostgreSQL, MySQL, SQLite, SQL Server, Oracle, and Firebird. Provider package decomposition is separate from the server embedding contract and may be refined independently.

## Compiler observability

`HsSqlAgent.SqlCore` remains telemetry-provider agnostic and returns deterministic `SqlCompileEvidence` with each translated or rejected compile decision. When `AddHsSqlAgentTelemetry()` is selected, the Server package observes that evidence at the runtime boundary:

- structured `ILogger` events contain verdict, decision boundary/code, source/target provider, capability-matrix version and evidence fingerprint;
- when `OtlpEndpoint` is configured, those logs and `sql.compile.decision` trace spans are exported through OTLP;
- `hsqlagent.sql.compiles` is emitted through the existing OpenTelemetry meter and Prometheus exporter with low-cardinality verdict/boundary/decision/provider labels.

Raw SQL, runtime literal/parameter values, full evidence objects, allowed-table snapshots, evidence fingerprints and trace IDs are not used as Prometheus labels. The database-backed `AuditLog` remains the durable governance/audit store rather than a general compiler telemetry store.

Do not commit production keys. Supply HMAC/JWT keys and database credentials through your deployment secret store.

Full configuration and deployment documentation:
https://github.com/tse-wei-chen/hs-sql-agent
