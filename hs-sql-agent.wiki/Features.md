# ✨ Features

## 🚀 High-Speed Core Engine

### Instant Interaction
An optimized C# backend (ASP.NET Core on .NET 10.0) ensures ultra-low latency for schema discovery and query execution. The MCP protocol transport runs over HTTP with minimal overhead.

### Universal Database Support
One agent to rule them all — natively supports:

| Database     | Strategy Class    | Integration Tests |
| ------------ | ----------------- | ----------------- |
| SQLite       | `SqliteStrategy`  | ✅ In-memory      |
| PostgreSQL   | `PostgresStrategy`| ✅ Testcontainers |
| MySQL        | `MySqlStrategy`   | ✅ Testcontainers |
| SQL Server   | `MsSqlServerStrategy`| ✅ Testcontainers|
| Oracle       | `OracleStrategy`  | ✅ Testcontainers |
| FireBird     | `FirebirdStrategy`| ✅ Testcontainers |

### Deterministic Accuracy
Rather than letting LLMs generate raw SQL (which can lead to hallucinated syntax), the LLM extracts structured parameters (tables, columns, and conditions) and passes them to a deterministic SQL generation engine. Powered by a **Metadata Tool**, the LLM can dynamically query and map the perfect parameters every time. For maximally deterministic and token-efficient queries, see [Custom Tools Strategy](MCP-Tools-Reference#custom-tools-strategy).

### Robust Security
Fully powered by [SqlKata](https://sqlkata.com) to enforce automatic parameterization on all inputs, strongly mitigating LLM-driven SQL injection risks at the source.

### Domain Knowledge & Semantic Layer
Define a robust DB semantic layer and customizable SQL tools to enhance AI reasoning, aligning the agent perfectly with your specific business logic.

## 🛡️ Enterprise-Grade Governance

### Built-in Admin Web UI
Manage your SQL Agent visually through an intuitive dashboard. No more wrestling with manual JSON configuration files.

### Granular Access Control

- **Key-Level Mapping**: Securely assign specific database connections and scopes to individual API keys.
- **Lifecycle Management**: Effortlessly issue, list, or revoke access keys in real-time.

### Production Guardrails

- **Table Whitelisting**: Restrict AI access to authorized tables only, ensuring sensitive data remains untouched.
- **Global Rate Limiting**: Prevent your production database from being overwhelmed by infinite AI loops or excessive traffic.
- **Comprehensive Audit Logs**: Track every single query with daily summaries and detailed execution history for compliance.

## 🛠️ MCP Tools

### Data Query (DQL) Tools

| Tool                    | Description                                              | Read-only |
| ----------------------- | -------------------------------------------------------- | --------- |
| `execute_query_safe`    | Execute complex queries (joins, grouping, CTEs, etc.)    | ✅        |
| `get_columns`           | Get column names and data types for a specific table     | ✅        |
| `get_tables`            | Get tables in the database (respects whitelist)          | ✅        |
| `get_schemas`           | Get schemas in the database                              | ✅        |

### Data Manipulation (DML) Tools

| Tool                 | Description                                                    | Read-only |
| -------------------- | -------------------------------------------------------------- | --------- |
| `execute_dml_safe`   | Execute INSERT, UPDATE, DELETE with two-step confirmation      | ❌        |

### Custom Tools
Administrators can define domain-specific SQL operations directly from the Admin Panel:

- Create parameterized queries with `{{parameterName}}` syntax
- Choose between Query (SELECT) or DML (INSERT/UPDATE/DELETE) operations
- Tools are automatically exposed as new MCP functions to the AI agent
- Dynamic parameter injection allows context-aware arguments

## 🖥️ Admin Panel Features

| Feature              | Description                                             |
| -------------------- | ------------------------------------------------------- |
| Dashboard            | Real-time operational monitoring of keys and audit      |
| Key Management       | Issue, list, and revoke MCP API keys                    |
| DB Management        | Add and manage database connections with testing        |
| Allowed Tools        | Fine-grained tool access per API key                    |
| Table Whitelist      | Restrict table access per API key                       |
| Semantic Layer       | Define display names and descriptions for tables/columns|
| Custom SQL Tools     | Low-code tool plugin system                             |
| Audit Logging        | Daily summaries and detailed execution history          |
| Rate Limiting        | Global IP-based rate limiting                           |
| User Management      | Admin account registration and authentication           |

## 🔐 Security Features

- **AesGcm Encryption**: Database connection strings encrypted at rest
- **Parameterized Queries**: All inputs parameterized via SqlKata — strongly mitigates SQL injection risks
- **JWT Authentication**: Admin panel access secured with JWT bearer tokens
- **HMAC Key Validation**: MCP access keys validated with HMAC-SHA256
- **Rate Limiting**: Global IP-bucketed rate limiter
- **Audit Trail**: Every query execution is logged with full context
- **Table Whitelist**: Per-key table-level access control
- **Cache Security**: SHA256 hashed cache keys with stripe locking
