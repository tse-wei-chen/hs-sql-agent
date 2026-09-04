# HsSqlAgent.Server

Embeddable MCP SQL Agent components for ASP.NET Core. The package can be used as a complete standalone server preset or composed into an existing ASP.NET Core host without replacing that host's authentication, authorization, MVC JSON, exception, or telemetry defaults.

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

## Standalone server preset

For the packaged toolbox/server experience, use the compatibility preset. It registers runtime services, the administration store, built-in identity, MCP, administration API, and telemetry.

```csharp
using HsSqlAgent.Server.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHsSqlAgent(options =>
{
    options.AdminDatabaseProvider = "Sqlite";
    options.AdminConnectionString = "Data Source=hsagent.db";
    options.HmacSecretKey = builder.Configuration["HMAC_KEY"]!; // at least 32 bytes
    options.JwtSecretKey = builder.Configuration["JWT_KEY"]!;   // at least 32 bytes
    options.Mcp.PublicEndpoint = "http://localhost:8080/mcp";
});

var app = builder.Build();
app.UseHsSqlAgent().ServeAdminUi();
app.Run();
```

`AddHsSqlAgent()` remains a convenience preset. It is not the primitive integration API for an existing application.

## Existing ASP.NET Core host

Compose only the capabilities that the host needs. Host authentication and authorization stay host-owned.

```csharp
using HsSqlAgent.Server.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Existing application authentication/authorization remains configured by the host.
builder.Services.AddAuthentication(/* host schemes */);
builder.Services.AddAuthorization(options =>
{
    // Configure the host's SqlAgentAdmin policy here.
});

var hs = builder.Services.AddHsSqlAgentCore(options =>
{
    options.AdminDatabaseProvider = "Sqlite";
    options.AdminConnectionString = "Data Source=hsagent.db";
    options.HmacSecretKey = builder.Configuration["HMAC_KEY"]!;
    options.Mcp.PublicEndpoint = "https://example.com/mcp";
});

hs.AddHsSqlAgentRuntime()
  .AddHsSqlAgentAdminStore()
  .AddHsSqlAgentHostAuthorization("SqlAgentAdmin")
  .AddHsSqlAgentAdminApi()
  .AddHsSqlAgentMcp();

// Optional. Do not add this unless the host wants HsSqlAgent's telemetry exporters.
// hs.AddHsSqlAgentTelemetry();

var app = builder.Build();

// The host owns these middleware decisions.
app.UseAuthentication();
app.UseAuthorization();

app.UseHsSqlAgentMcp();
app.UseHsSqlAgentAdminApi();

// Optional. The packaged SPA currently mounts only at `/`.
// app.UseHsSqlAgentAdminUi();

app.Run();
```

In host-authorization mode HsSqlAgent does not publish its built-in `AuthController`, `MemberController`, or `RoleController`, does not install its JWT identity schema, and does not add an `/api`-wide authentication/authorization middleware branch. HsSqlAgent permission checks delegate to the configured host policy and pass the requested canonical permission keys as the authorization resource.

## Built-in identity as an explicit capability

If an application wants HsSqlAgent's own JWT/member/role model, opt into it explicitly:

```csharp
var hs = builder.Services.AddHsSqlAgentCore(options =>
{
    options.AdminDatabaseProvider = "Sqlite";
    options.AdminConnectionString = "Data Source=hsagent.db";
    options.HmacSecretKey = builder.Configuration["HMAC_KEY"]!;
    options.JwtSecretKey = builder.Configuration["JWT_KEY"]!;
    options.Mcp.PublicEndpoint = "https://example.com/mcp";
});

hs.AddHsSqlAgentRuntime()
  .AddHsSqlAgentAdminStore()
  .AddHsSqlAgentBuiltInAuth()
  .AddHsSqlAgentAdminApi()
  .AddHsSqlAgentMcp();
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
- when `Telemetry.OtlpEndpoint` is configured, those logs and `sql.compile.decision` trace spans are exported through OTLP;
- `hsqlagent.sql.compiles` is emitted through the existing OpenTelemetry meter and Prometheus exporter with low-cardinality verdict/boundary/decision/provider labels.

Raw SQL, runtime literal/parameter values, full evidence objects, allowed-table snapshots, evidence fingerprints and trace IDs are not used as Prometheus labels. The database-backed `AuditLog` remains the durable governance/audit store rather than a general compiler telemetry store.

Do not commit production keys. Supply HMAC/JWT keys and database credentials through your deployment secret store.

Full configuration and deployment documentation:
https://github.com/tse-wei-chen/hs-sql-agent
