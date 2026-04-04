# hs-sql-agent
[![.NET](https://github.com/tse-wei-chen/hs-sql-agent/actions/workflows/dotnet.yml/badge.svg)](https://github.com/tse-wei-chen/hs-sql-agent/actions/workflows/dotnet.yml) ![GitHub License](https://img.shields.io/github/license/tse-wei-chen/hs-sql-agent)

A Model Context Protocol (MCP) server that exposes SQL query capabilities as tools, allowing AI assistants to safely query relational databases via structured parameters.

## Features

- Supports **SQLite**, **PostgreSQL**, and **MySQL**
- Exposes MCP tools for querying, schema inspection, and table discovery
- Query builder powered by [SqlKata](https://sqlkata.com/) — no raw SQL injection risk
- HTTP transport at `/mcp` (compatible with Claude Desktop and other MCP clients)
- Provider and connection string configurable via `appsettings` or environment variables

## MCP Tools

| Tool | Description |
|------|-------------|
| `execute_query_safe` | Execute a SELECT query with joins, filters, ordering, grouping, and limits |
| `get_columns` | List columns of a given table |
| `get_schemas` | List all schemas in the database |
| `get_tables` | List all tables in the database |
| `get_table_reference` | Get table reference metadata |

## Try it Out

Public hosted endpoint: `https://hs-sql-agent-pg.zeabur.app/mcp`

This endpoint is connected to a **Northwind demo database** for testing and learning.

### Claude Desktop Integration

Add the server to your `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "hs-sql-agent": {
      "url": "https://hs-sql-agent-pg.zeabur.app/mcp"
    }
  }
}
```
### vscode

Add the server to your `mcp.json`:
```json
{
    "servers": {
        "hs-sql-agent": {
            "type": "http",
            "url": "https://hs-sql-agent-pg.zeabur.app/mcp"
        }
    }
}
```

### cursor
Add the server to your `mcp.json`:
```json
{
	"mcpServers": {
		"hs-sql-agent": {
			"type": "http",
			"url": "https://hs-sql-agent-pg.zeabur.app/mcp"
		}
	}
}
```

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)

## Getting Started

### 1. Clone the repository

```bash
git clone https://github.com/your-org/hs-sql-agent.git
cd hs-sql-agent
```

### 2. Configure database connection

Choose one of the following methods.

Option A: use `appsettings.json` (good for local development)

1. Copy `src/ToolBox/appsettings.Sample.json` to `src/ToolBox/appsettings.json`.
2. Fill in `SqlConfig.Provider` and `SqlConfig.ConnectionString`.

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning",
      "Microsoft.EntityFrameworkCore.Database.Command": "Information"
    }
  },
  "AllowedHosts": "*",
  "ASPNETCORE_URLS": "http://localhost:8080",
  "SqlConfig": {
    "Provider": "Postgres",
    "ConnectionString": "Host=localhost;Port=5432;Database=mydb;Username=myuser;Password=mypassword"
  },
  "RateLimiting": {
    "PermitLimit": 0,
    "WindowSeconds": 0,
    "QueueLimit": 0
  },
  "JwtSettings": {
    "SecretKey": "YourSuperSecretKeyHere",
    "Issuer": "YourAppIssuer",
    "Audience": "YourAppAudience",
    "AccessTokenExpirationMinutes": 60,
    "RefreshTokenExpirationDays": 30
  }
}
```

Option B: use environment variables (recommended for deployment)

```powershell
$env:SqlConfig__Provider="Postgres"
$env:SqlConfig__ConnectionString="Host=localhost;Port=5432;Database=mydb;Username=myuser;Password=mypassword"
```

Supported providers: `Sqlite`, `Postgres`, `Mysql`

### 3. Run the server

```bash
cd src/ToolBox
dotnet run
```

The MCP endpoint will be available at `http://localhost:8080/mcp`.

### Claude Desktop Integration

Add the server to your `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "hs-sql-agent": {
      "url": "http://localhost:8080/mcp"
    }
  }
}
```
### vscode

Add the server to your `mcp.json`:
```json
{
    "servers": {
        "hs-sql-agent": {
            "type": "http",
            "url": "http://localhost:8080/mcp"
        }
    }
}
```

### cursor
Add the server to your `mcp.json`:
```json
{
	"mcpServers": {
		"hs-sql-agent": {
			"type": "http",
			"url": "http://localhost:8080/mcp"
		}
	}
}
```
## Project Structure

```
src/
  Common/           Shared models and base types
  ToolBox/          MCP server entry point and tools
    Tools/          SqlAgent MCP tool definitions
    Strategies/     Database-specific query strategies (SQLite, Postgres, MySQL)
    Factories/      Strategy factory
    Middleware/      MCP context and response middleware
    Models/         Configuration and request models
    Enums/          SqlAgentToolType enum
```

## License

[Apache License](LICENSE)
