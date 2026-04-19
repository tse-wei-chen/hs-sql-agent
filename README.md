# ⚡ hs-sql-agent (High-Speed SQL Agent)

> **The high-performance MCP server designed for instant SQL interaction and secure enterprise governance.**

![GitHub License](https://img.shields.io/github/license/tse-wei-chen/hs-sql-agent) [![Docker](https://github.com/tse-wei-chen/hs-sql-agent/actions/workflows/docker-publish.yml/badge.svg?event=release)](https://github.com/tse-wei-chen/hs-sql-agent/actions/workflows/docker-publish.yml) [![CodeQL Advanced](https://github.com/tse-wei-chen/hs-sql-agent/actions/workflows/codeql.yml/badge.svg?event=release)](https://github.com/tse-wei-chen/hs-sql-agent/actions/workflows/codeql.yml)

### Why "hs"?

`hs` stands for **High Speed**. While generic SQL agents are often sluggish, complex to configure, or insecure, `hs-sql-agent` is built for:

- **High-Speed Execution**: Optimized C# backend for ultra-low latency.
- **High-Speed Deployment**: Docker-ready, up and running in 30 seconds.
- **High-Speed Governance**: Instantly manage keys and audit logs via a built-in UI.

`hs-sql-agent` is a robust HTTP MCP server for relational databases that bridges the gap between AI agents and your data. Unlike "black-box" generic SQL agents, `hs-sql-agent` provides a **High-Speed** execution engine with a **Built-in Admin Panel** to ensure every AI-generated query is managed, audited, and secure.

## ✨ Key Features

### High-Speed & Universal Access

- **Instant Interaction**: Optimized C# backend ensures ultra-low latency for schema discovery and query execution.
- **Universal Database Support**: One agent for all — supports `Sqlite`, `Postgres`, `Mysql`, `SqlServer`, `Oracle`, and `FireBird`.
- **Structured Querying**: Powered by [SqlKata](https://sqlkata.com) for reliable and safe SQL construction.

### Enterprise-Grade Governance

- **Built-in Admin Web UI**: Manage your SQL Agent visually. No more manual JSON configuration files.
- **Granular Security Control**:
  - **Key-Level Mapping**: Assign specific database connections to individual API keys.
  - **Lifecycle Management**: Effortlessly issue, list, or revoke access keys in real-time.
- **Guardrails & Safety**:
  - **Global Rate Limiting**: Prevent your database from being overwhelmed by AI loops or excessive traffic.
  - **Comprehensive Audit Logs**: Track every single query with daily summaries and detailed execution history.

## ⏳ Progress
### MCP Tools ( Ready for use, but still in the experimental stage.

| progress | Tool                 | Description                                                       |
| -------- | -------------------- | ----------------------------------------------------------------- |
| 🧪       | `execute_query_safe` | Execute a query (supports join, where, order by, group by, limit) |
| 🧪       | `get_columns`        | Get column names of a table                                       |
| 🧪       | `get_schemas`        | Get schemas in the database                                       |
| 🧪       | `get_tables`         | Get tables in the database                                        |
| 🧪       | `execute_dml_safe`   | Execute a DML statement (INSERT, UPDATE, DELETE)                  |

### Admin Panel API keys

| progress | Feature                           | Description                                                      |
| -------- | --------------------------------- | ---------------------------------------------------------------- |
| ✅       | `allowed Tools`                   | Manage tool access for the API key                               |
| ✅       | `Per-key SQL provider/connection` | Override SQL provider/connection settings for a specific API key |
| ✅       | `issue Key`                       | Issue a new API key with optional SQL override and allowed Tools |
| ✅       | `list Keys`                       | List all API keys with metadata (excluding secret values)        |
| ✅       | `revoke Key`                      | Revoke an API key by ID                                          |

### Audit logs

| progress | Feature     | Description                                                                                |
| -------- | ----------- | ------------------------------------------------------------------------------------------ |
| ✅       | `log Query` | Log each executed query with metadata (timestamp, key ID, execution time, success/failure) |
| 🚧       | `log`       | General logging feature for various events                                                 |

### Global settings

| progress | Feature            | Description                        |
| -------- | ------------------ | ---------------------------------- |
| ✅       | `global RateLimit` | Get global API rate limit settings |

### Architecture

- Backend: ASP.NET Core (`net10.0`) in `backend/src/ToolBox`
- Admin data store: SQLite via `AppConnectionString`
- Frontend: Nuxt 4 in `frontend`
- MCP endpoint: `http://localhost:8080/mcp`

## 🚀 Quick Start (Docker Compose)

The easiest way to run **HS SQL Agent** is using Docker Compose. This ensures your configuration is saved and your data persists across restarts.

### 1. Setup Configuration

Ensure a `docker-compose.yml` file in your project directory:

Copy the example env file:
```bash
cp .env.example .env
```

Then edit the `.env` file to set your secret keys:

```env
HMAC_KEY=YourMcpHmacSecretKeyHere-AtLeast32Bytes!
JWT_KEY=YourSuperSecretKeyHere-AtLeast32Bytes!
```

> ⚠️ **Important Security Notes** Never use the example keys in production. Replace `McpKeySettings__HmacSecretKey` and `JwtSettings__SecretKey` with unique, 32+ byte strings. You can generate secure random keys using tools like `openssl rand -base64 32` or online generators.

### 2. Launch the Application

Run the following command in your terminal:

```bash
docker-compose up -d
```

### 3. Access the Service

Once the container is running, the services will be available at:
- **Admin Panel:** `http://localhost:8080` (for managing API keys and viewing logs)
- **MCP Endpoint:** `http://localhost:8080/mcp`

## 🏠 Local Development

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

Copy the Example appsettings file:

```bash
cp backend/src/ToolBox/appsettings.Example.json backend/src/ToolBox/appsettings.json
```

Required settings:

- `AppConnectionString`
- `McpKeySettings.HmacSecretKey` (at least 32 bytes)
- `JwtSettings.SecretKey` (at least 32 bytes)
- `JwtSettings.Issuer`
- `JwtSettings.Audience`
- `RateLimiting` (global fallback rate limit. 0 means no limit)

Optional settings:

- `SqlConfig` (global SQL fallback if key override is not provided)

Minimal example:

```json
{
	"ASPNETCORE_URLS": "http://localhost:8080",
	"AppConnectionString": "Data Source=hsqlagent.db",
	"McpKeySettings": {
		"HmacSecretKey": "YourMcpHmacSecretKeyHere-AtLeast32Bytes!"
	},
	"JwtSettings": {
		"SecretKey": "YourSuperSecretKeyHere-AtLeast32Bytes!",
		"Issuer": "YourAppIssuer",
		"Audience": "YourAppAudience",
		"AccessTokenExpirationMinutes": 60,
		"RefreshTokenExpirationDays": 30
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

## 📒 How to Use

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

## 🖧 Project Structure

```text
backend/
  src/
    Common/      Shared models and utilities
    Modules/     Data access and domain services
    ToolBox/     ASP.NET host, MCP tools, middleware, and controllers
frontend/
  app/           Nuxt app (admin panel)
```

## 🛠️ Detailed information about the skills

<details>
<summary><b>DQL</b></summary>

- **execute_query_safe**
  - Title: Execute query safely
  - Description: Execute a query (supports join, where, group, having, combine, cte, order by, limit, offset, distinct, subqueries).
- Parameters:
  - `tableName` (string, optional): The main table name for the query (use schema-qualified name if needed). Can be null if `fromQuery` is provided.
  - `selectColumns` (array, optional): List of columns to select.
  - `whereConditions` (array, optional): List of where conditions.
  - `orderByColumns` (array, optional): List of columns to order by. Each item can include `Field`, `Aggregation` (e.g., COUNT, SUM), and `Direction` (ASC or DESC).
  - `limit` (integer, optional): Limit the number of results returned.
  - `offset` (integer, optional): Offset the number of results returned.
  - `joins` (array, optional): List of joins. Each join is a dictionary with keys: `Table`, `On`, and optional `Type` (default `INNER`).
  - `groupByConditions` (array, optional): List of group by conditions. Each condition includes `Table`, `Field`.
  - `havingConditions` (array, optional): List of having conditions.
  - `combineConditions` (array, optional): List of combine conditions (`union`, `union all`, `intersect`, `except`).
  - `cteConditions` (array, optional): List of CTE definitions.
  - `distinct` (boolean, optional): Whether to use `SELECT DISTINCT`.
  - `fromQuery` (object, optional): Source subquery definition. If provided, `tableName` is ignored.
  - `alias` (string, optional): Alias for the source table or subquery.
  - Read-only: **true**

- **get_columns**
  - Title: Get columns
  - Description: Get column names of a table.
  - Parameters:
    - `schemaName` (string): The schema name.
    - `tableName` (string): The table name.
  - Read-only: **true**

- **get_schemas**
  - Title: Get schemas
  - Description: Get list of schemas in the database.
  - Parameters: none
  - Read-only: **true**

- **get_tables**
  - Title: Get tables
  - Description: Get list of tables in a schema.
  - Parameters:
    - `schemaName` (string): The schema name.
  - Read-only: **true**

</details>
<details>
<summary><b>DML</b></summary>

- **execute_dml_safe**
  - Title: Execute DML safely
  - Description: Execute a DML operation (INSERT, UPDATE, DELETE). This tool uses a mandatory two-step safety mechanism:
    1. First call (without `ConfirmToken`): Performs a dry run, returns affected rows and a unique `ConfirmToken`.
    2. Second call (with `ConfirmToken`): Commits the operation if the token matches.
  - Parameters: - `dmlDefinition` (object): The DML definition, including operation type, table, values, and conditions. - `ConfirmToken` (string, optional): The confirmation token returned from the dry run (required for commit).
  - Read-only: **false**

</details>

## License

[Apache License](LICENSE)
