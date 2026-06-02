# HsSqlAgent.Server

An embeddable MCP (Model Context Protocol) SQL Agent server for ASP.NET Core. Install via NuGet and add `HsSqlAgent` to any existing ASP.NET Core application with `AddHsSqlAgent()` / `UseHsSqlAgent()` — the Admin UI is bundled inside the DLL, no external `wwwroot` needed.

## Features

- **MCP Server** — Exposes SQL database tools over the Model Context Protocol for AI agents (Claude Desktop, etc.)
- **Multi-DB Support** — SQLite, PostgreSQL, MySQL, SQL Server, Oracle, Firebird
- **Structured Query Execution** — Safe, structured query definitions prevent SQL injection
- **Two-Step DML Safety** — Dry-run first, then confirm; prevents accidental mutations
- **Access Key Auth** — HMAC-signed MCP access keys with fine-grained permissions
- **Admin API** — JWT-based admin panel (sign-in, sign-up, token refresh)
- **DB Management** — CRUD for database connections with encrypted passwords
- **Semantic Layer** — Enrich schemas with human-readable display names and descriptions
- **Custom SQL Tools** — Define parameterized SQL tools via the admin API
- **Audit Logging** — Async audit trail for all operations
- **Rate Limiting** — Per-IP rate limiting on MCP endpoints
- **Table Whitelist** — Per-key table-level access control
- **CORS** — Per-key CORS origin restrictions

## Quick Start

```bash
dotnet add package HsSqlAgent.Server
```

```csharp
// Program.cs
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHsSqlAgent(options =>
{
    options.AdminConnectionString = "Data Source=admin.db";
    options.HmacSecretKey = builder.Configuration["HMAC_KEY"]!;    // 32+ bytes
    options.JwtSecretKey = builder.Configuration["JWT_KEY"]!;      // 32+ bytes
});

var app = builder.Build();

// API + MCP only (no admin UI)
app.UseHsSqlAgent();

// Or with built-in admin UI
// app.UseHsSqlAgent().ServeAdminUi();

app.Run();
```

## Configuration

| Option | Default | Description |
|--------|---------|-------------|
| `AdminConnectionString` | `Data Source=hsagent.db` | SQLite connection string for the admin database |
| `HmacSecretKey` | — | HMAC key for MCP access key signing (min 32 bytes) |
| `JwtSecretKey` | — | JWT signing key (min 32 bytes) |
| `JwtIssuer` | `HS-Agent` | JWT issuer claim |
| `JwtAudience` | `HS-Agent-Users` | JWT audience claim |
| `JwtAccessTokenExpirationMinutes` | `1` | Access token lifetime |
| `JwtRefreshTokenExpirationDays` | `30` | Refresh token lifetime |
| `RateLimitPermitLimit` | `0` | Max requests per window (0 = disabled) |
| `RateLimitWindowSeconds` | `0` | Rate limit window duration |
| `RateLimitQueueLimit` | `0` | Max queued requests |
| `McpEndpoint` | `/mcp` | MCP HTTP transport endpoint |
| `AdminApiPrefix` | `/api` | Admin API route prefix |
| `ServeAdminUi` | `false` | Serve the built-in admin UI |

## MCP Tools

Exposed to AI agents via the MCP endpoint:

| Tool | Description |
|------|-------------|
| `execute_query_safe` | Execute a structured SELECT query (JSON definition block) |
| `execute_dml_safe` | Two-step DML: dry-run first, then confirm with `ConfirmToken` |
| `get_columns` | Get column names and types for a table |
| `get_schemas` | List all schemas in the database |
| `get_tables` | List all tables in a schema |
| `update_semantic_layer` | Upsert semantic metadata (display names, descriptions) for tables/columns |

## Admin API Endpoints

All admin endpoints are under `{AdminApiPrefix}` (default `/api`).

### Authentication

| Method | Path | Auth | Description |
|--------|------|------|-------------|
| `GET` | `/api/admin/first-run` | Anonymous | Check if first-run (no admin user exists) |
| `POST` | `/api/admin/sign-up` | Anonymous | Register the first admin account |
| `POST` | `/api/admin/sign-in` | Anonymous | Sign in, receive access + refresh tokens |
| `POST` | `/api/admin/refresh-token` | Refresh | Exchange refresh token for new tokens |

### MCP Key Management

| Method | Path | Auth | Description |
|--------|------|------|-------------|
| `GET` | `/api/runtime/mcp-keys` | Bearer | List all MCP access keys |
| `POST` | `/api/runtime/mcp-keys` | Bearer | Issue a new MCP access key |
| `POST` | `/api/runtime/mcp-keys/{id}/revoke` | Bearer | Revoke an MCP access key |
| `POST` | `/api/runtime/mcp-keys/test-db-connection` | Bearer | Test a database connection |

### DB Management

| Method | Path | Auth | Description |
|--------|------|------|-------------|
| `GET` | `/api/dbmanagement` | Bearer | List all database connections |
| `POST` | `/api/dbmanagement` | Bearer | Create a database connection |
| `GET` | `/api/dbmanagement/{id}` | Bearer | Get a database connection |
| `PUT` | `/api/dbmanagement/{id}` | Bearer | Update a database connection |
| `DELETE` | `/api/dbmanagement/{id}` | Bearer | Delete a database connection |
| `GET` | `/api/dbmanagement/{id}/schemas` | Bearer | List schemas for a connection |
| `GET` | `/api/dbmanagement/{id}/tables` | Bearer | List tables for a connection |
| `GET` | `/api/dbmanagement/{id}/columns` | Bearer | List columns for a table |

### Semantic Layer

| Method | Path | Auth | Description |
|--------|------|------|-------------|
| `GET` | `/api/dbsemantic/{dbManagementId}` | Bearer | Get all semantic entries |
| `POST` | `/api/dbsemantic` | Bearer | Upsert a semantic entry |
| `DELETE` | `/api/dbsemantic/{id}` | Bearer | Delete a semantic entry |

### Custom SQL Tools

| Method | Path | Auth | Description |
|--------|------|------|-------------|
| `GET` | `/api/customsqltool` | Bearer | List all custom tools |
| `POST` | `/api/customsqltool` | Bearer | Create a custom SQL tool |
| `GET` | `/api/customsqltool/{id}` | Bearer | Get a custom tool |
| `PUT` | `/api/customsqltool/{id}` | Bearer | Update a custom tool |
| `DELETE` | `/api/customsqltool/{id}` | Bearer | Delete a custom tool |

### Audit

| Method | Path | Auth | Description |
|--------|------|------|-------------|
| `GET` | `/api/runtime/audit` | Bearer | Query audit logs (paginated) |
| `GET` | `/api/runtime/audit/daily-summary` | Bearer | Get daily audit summary |

## Access Key Configuration

When issuing an MCP access key, you can configure:

- **Allowed Tools** — Comma-separated list of MCP tool names the key can use
- **DB Management ID** — Bind the key to a specific database connection
- **Table Whitelist** — Comma-separated `schema.table` entries the key can access
- **CORS Allowed Origins** — Restrict origins for browser-based MCP clients
- **Expiration** — Optional expiration date

## Connecting from AI Clients

The MCP endpoint accepts `X-MCP-Server-Key` header:

```json
{
  "mcpServers": {
    "hs-sql-agent": {
      "url": "http://localhost:8080/mcp",
      "headers": {
        "X-MCP-Server-Key": "<mcp-access-key>"
      }
    }
  }
}
```

## Project Structure

```
HsSqlAgent.Server/
├── Attributes/          # Custom authorization attributes
├── Background/          # Background services (audit, key last-used)
├── Controllers/         # Admin API controllers
├── Extensions/          # DI registration (AddHsSqlAgent, UseHsSqlAgent)
├── Middleware/          # MCP auth, stringified array fix, exception handler
├── Models/              # Options and DTOs
├── Tools/               # MCP tools (SqlAgentTool, CustomToolProxy)
└── HsSqlAgent.Server.csproj
```

## Dependencies

- **ModelContextProtocol** — MCP protocol implementation
- **ASP.NET Core** — Authentication (JWT Bearer), Authorization, Rate Limiting
- **EF Core + SQLite** — Admin database
- **FluentValidation** — Request validation
- **Common** — Shared utilities
- **Admin.Service** — Admin domain logic
- **SqlAgent.Service** — SQL strategy and query execution
