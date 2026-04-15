# hs-sql-agent
![GitHub License](https://img.shields.io/github/license/tse-wei-chen/hs-sql-agent) [![Docker](https://github.com/tse-wei-chen/hs-sql-agent/actions/workflows/docker-publish.yml/badge.svg?event=release)](https://github.com/tse-wei-chen/hs-sql-agent/actions/workflows/docker-publish.yml) [![CodeQL Advanced](https://github.com/tse-wei-chen/hs-sql-agent/actions/workflows/codeql.yml/badge.svg?event=release)](https://github.com/tse-wei-chen/hs-sql-agent/actions/workflows/codeql.yml)

`hs-sql-agent` is an HTTP MCP server for relational databases with an integrated admin panel.
It lets MCP clients call safe SQL tools while you manage access keys, audit logs, per-key database mapping, and global rate limits.

## Features

- MCP over HTTP at `/mcp`
- SQL tools powered by [SqlKata](https://sqlkata.com/) (structured query building)
- Built-in admin APIs and web UI
- API key lifecycle management (issue, list, revoke)
- Audit log and daily summary APIs
- Per-key SQL provider/connection override
- Global IP rate limit override
- Supports `Sqlite`, `Postgres`, `Mysql`, `SqlServer`, `Oracle`, `FireBird`

## MCP Tools

| progress | Tool | Description |
|------|------|-------------|
| 🚧 | `execute_query_safe` | Execute a query (supports join, where, order by, group by, limit) |
| 🚧 | `get_columns` | Get column names of a table |
| 🚧 | `get_schemas` | Get schemas in the database |
| 🚧 | `get_tables` | Get tables in the database |
| 🚧 | `execute_dml_safe` | Execute a DML statement (INSERT, UPDATE, DELETE) |

## Admin Panel API keys
| progress | Tool | Description |
|------|------|-------------|
| 🚧 | `allowed Tools` | List allowed tools for the key |
| ✅ | `Per-key SQL provider/connection` | Override SQL provider/connection settings for a specific API key |
| ✅ | `global RateLimit` | global rate limit |
| ✅ | `issue Key` | Issue a new API key with optional SQL override and rate limit override |
| ✅ | `list Keys` | List all API keys with metadata (excluding secret values) |
| ✅ | `revoke Key` | Revoke an API key by ID |

## Architecture

- Backend: ASP.NET Core (`net10.0`) in `backend/src/ToolBox`
- Admin data store: SQLite via `AppConnectionString`
- Frontend: Nuxt 4 in `frontend`
- MCP endpoint: `http://localhost:8080/mcp`

## Quick Start (Docker)

Pull from GHCR and run:

```bash
docker pull ghcr.io/tse-wei-chen/hs-sql-agent:latest
docker run --rm -p 8080:8080 \
  -e AppConnectionString="Data Source=hsqlagent.db" \
  -e McpKeySettings__HmacSecretKey="YourMcpHmacSecretKeyHere-AtLeast32Chars!" \
  -e JwtSettings__SecretKey="YourSuperSecretKeyHere-AtLeast32Chars!" \
  -e JwtSettings__Issuer="YourAppIssuer" \
  -e JwtSettings__Audience="YourAppAudience" \
  -e JwtSettings__AccessTokenExpirationMinutes="60" \
  -e JwtSettings__RefreshTokenExpirationDays="1" \
  -e RateLimiting__PermitLimit="0" \
  -e RateLimiting__WindowSeconds="0" \
  -e RateLimiting__QueueLimit="0" \
  ghcr.io/tse-wei-chen/hs-sql-agent:latest
```

⚠️ For production deployment, replace the example values for `McpKeySettings__HmacSecretKey`, `JwtSettings__SecretKey`, `JwtSettings__Issuer`, and `JwtSettings__Audience` before running the container.

If you want to build locally instead of pulling from GHCR:

```bash
docker build -t hs-sql-agent .
docker run --rm -p 8080:8080 \
  -e AppConnectionString="Data Source=hsqlagent.db" \
  -e McpKeySettings__HmacSecretKey="YourMcpHmacSecretKeyHere-AtLeast32Chars!" \
  -e JwtSettings__SecretKey="YourSuperSecretKeyHere-AtLeast32Chars!" \
  -e JwtSettings__Issuer="YourAppIssuer" \
  -e JwtSettings__Audience="YourAppAudience" \
  -e JwtSettings__AccessTokenExpirationMinutes="60" \
  -e JwtSettings__RefreshTokenExpirationDays="1" \
  -e RateLimiting__PermitLimit="0" \
  -e RateLimiting__WindowSeconds="0" \
  -e RateLimiting__QueueLimit="0" \
  hs-sql-agent
```
[![How to Use](https://img.shields.io/badge/How%20to%20Use-Jump-0f766e)](#how-to-use)


## Quick Start (Local Development)

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Node.js 20+](https://nodejs.org/)
- [pnpm](https://pnpm.io/) (recommended)

### 1. Clone

```bash
git clone https://github.com/your-org/hs-sql-agent.git
cd hs-sql-agent
```

### 2. Configure backend settings

Copy the sample file:

```bash
cp backend/src/ToolBox/appsettings.Sample.json backend/src/ToolBox/appsettings.json
```

Required settings:

- `AppConnectionString`
- `McpKeySettings.HmacSecretKey` (at least 32 bytes)
- `JwtSettings.SecretKey` (at least 32 bytes)
- `JwtSettings.Issuer`
- `JwtSettings.Audience`

Optional settings:

- `SqlConfig` (global SQL fallback if key override is not provided)
- `RateLimiting` (global fallback rate limit)

Minimal example:

```json
{
  "ASPNETCORE_URLS": "http://localhost:8080",
  "AppConnectionString": "Data Source=hsqlagent.db",
  "McpKeySettings": {
    "HmacSecretKey": "YourMcpHmacSecretKeyHere-AtLeast32Chars!"
  },
  "JwtSettings": {
    "SecretKey": "YourSuperSecretKeyHere-AtLeast32Chars!",
    "Issuer": "YourAppIssuer",
    "Audience": "YourAppAudience",
    "AccessTokenExpirationMinutes": 60,
    "RefreshTokenExpirationDays": 30,
    "ChangePasswordTokenExpirationMinutes": 5
  },
  "RateLimiting": {
    "PermitLimit": 0,
    "WindowSeconds": 0,
    "QueueLimit": 0
  }
}
```

Optional SQL:

```json
{
  "SqlConfig": {
    "Provider": "Postgres",
    "ConnectionString": "Host=localhost;Port=5432;Database=mydb;Username=myuser;Password=mypassword"
  }
}
```

Notes:

- `RateLimiting.PermitLimit <= 0` or `WindowSeconds <= 0` means no limit.
- Current defaults are `0/0/0` when `RateLimiting` is omitted.
- `SqlConfig` is optional and acts as fallback when key-level SQL override is absent.

### 3. Run backend

```bash
cd backend/src/ToolBox
dotnet run -e Development
```

Backend runs on `http://localhost:8080` by default.

### 4. Run frontend (optional, for admin UI)

In another terminal:

```bash
cd frontend
pnpm install
pnpm dev
```

Frontend runs on `http://localhost:3000`.

## How to Use

### First-time setup flow

1. Start backend and frontend.
2. Open `http://localhost:3000` (if you using Docker `http://localhost:8080`).
3. First run: create the first admin account.
4. Sign in with that admin account.
5. Go to Runtime MCP Keys page (`/runtime/mcp-keys`).
6. Click `Issue Key` to create a new MCP key.
7. Copy the `One-time key value` immediately (it is only shown once).
8. Paste that value into MCP client config headers: `"X-MCP-Server-Key": "<YOUR_MCP_KEY>"`.

### Claude Desktop

```json
{
  "mcpServers": {
    "hs-sql-agent": {
      "url": "http://localhost:8080/mcp",
      "headers": { "X-MCP-Server-Key": "<YOUR_MCP_KEY>" }
    }
  }
}
```

### VS Code

```json
{
  "servers": {
    "hs-sql-agent": {
      "type": "http",
      "url": "http://localhost:8080/mcp",
      "headers": { "X-MCP-Server-Key": "<YOUR_MCP_KEY>" }
    }
  }
}
```

### Cursor

```json
{
  "mcpServers": {
    "hs-sql-agent": {
      "type": "http",
      "url": "http://localhost:8080/mcp",
      "headers": { "X-MCP-Server-Key": "<YOUR_MCP_KEY>" }
    }
  }
}
```

## Project Structure

```text
backend/
  src/
    Common/      Shared models and utilities
    Modules/     Data access and domain services
    ToolBox/     ASP.NET host, MCP tools, middleware, and controllers
frontend/
  app/           Nuxt app (admin panel)
```

## License

[Apache License](LICENSE)
