# hs-sql-agent

> **The high-performance MCP server for instant SQL interaction and secure enterprise governance.**

[![License: Apache 2.0](https://img.shields.io/badge/license-Apache--2.0-green)](https://github.com/tse-wei-chen/hs-sql-agent/blob/main/LICENSE) [![Docker](https://github.com/tse-wei-chen/hs-sql-agent/actions/workflows/docker-publish.yml/badge.svg?event=release)](https://github.com/tse-wei-chen/hs-sql-agent/actions/workflows/docker-publish.yml) [![CodeQL Advanced](https://github.com/tse-wei-chen/hs-sql-agent/actions/workflows/codeql.yml/badge.svg?event=release)](https://github.com/tse-wei-chen/hs-sql-agent/actions/workflows/codeql.yml) [![Tests](https://github.com/tse-wei-chen/hs-sql-agent/actions/workflows/test.yml/badge.svg)](https://github.com/tse-wei-chen/hs-sql-agent/actions/workflows/test.yml)

`hs-sql-agent` is an HTTP MCP server for relational databases (SQLite, PostgreSQL, MySQL, SQL Server, Oracle, Firebird) with a built-in Admin Panel for governance.

## 🤔 Why hs-sql-agent?

Most "Chat with your Data" tools ask the LLM to write raw SQL — a recipe for hallucinations, dialect confusion, and injection risks. **hs-sql-agent flips the model**: the LLM only extracts logical parameters (tables, columns, conditions), and a deterministic engine ([SqlKata](https://sqlkata.com)) constructs the final SQL. Zero hallucinated syntax, zero injection surface.

- **Deterministic Accuracy** — The LLM never writes raw SQL. No made-up tables, no wrong functions, no dialect mix-ups between PostgreSQL and Oracle.
- **Universal DB Support** — One agent for SQLite, PostgreSQL, MySQL, SQL Server, Oracle, and Firebird. The same MCP endpoint switches engines transparently.
- **Enterprise Governance** — Built-in Admin Web UI, key-level connection mapping, table whitelisting, per-key CORS, rate limiting, and full audit logs.
- **Semantic Layer** — Map cryptic legacy column names to business-friendly labels so the LLM understands your schema.

### Where to use it

| Use case | Description |
|----------|-------------|
| **Cursor / Claude Desktop** | Let devs query dev/test DBs in natural language from their AI IDE. |
| **Multi-DB agents** | One MCP server per database, each secured with its own API key. The agent aggregates multiple MCP connections to seamlessly orchestrate workflows across PostgreSQL, MySQL, and Oracle. |
| **Enterprise chatbots** | Connect internal AI agents to ERP/CRM systems with table-level permission isolation. |
| **Legacy modernization** | Bridge modern AI to decades-old databases via the semantic layer. |

## 🚀 Quick Start

```bash
cp .env.example .env      # set HMAC_KEY and JWT_KEY (32+ bytes)
docker compose up -d       # http://localhost:8080
```

## 📦 NuGet for Existing .NET APIs

Already have an ASP.NET Core API? Embed the full MCP SQL Agent + Admin UI in minutes:

```bash
dotnet add package HsSqlAgent.Server
```

```csharp
builder.Services.AddHsSqlAgent(options => { ... });
app.UseHsSqlAgent();                    // API-only
// app.UseHsSqlAgent().ServeAdminUi();  // with Admin UI
```

> The Admin UI is embedded in the DLL — no external files to deploy. See the [NuGet Package guide](https://github.com/tse-wei-chen/hs-sql-agent/wiki/NuGet-Package) for details.

## 📖 Documentation

Detailed docs are on the [Wiki](https://github.com/tse-wei-chen/hs-sql-agent/wiki):

| Topic | Link |
|-------|------|
| 🚀 Getting Started | [Getting-Started](https://github.com/tse-wei-chen/hs-sql-agent/wiki/Getting-Started) |
| ✨ Features | [Features](https://github.com/tse-wei-chen/hs-sql-agent/wiki/Features) |
| 📘 MCP Tools | [MCP-Tools-Reference](https://github.com/tse-wei-chen/hs-sql-agent/wiki/MCP-Tools-Reference) |
| 🖥️ Admin Panel | [Admin-Panel](https://github.com/tse-wei-chen/hs-sql-agent/wiki/Admin-Panel) |
| ⚙️ Configuration | [Configuration](https://github.com/tse-wei-chen/hs-sql-agent/wiki/Configuration) |
| 🐳 Deployment | [Deployment](https://github.com/tse-wei-chen/hs-sql-agent/wiki/Deployment) |
| 🏠 Development | [Development](https://github.com/tse-wei-chen/hs-sql-agent/wiki/Development) |
| 📡 API Reference | [API-Reference](https://github.com/tse-wei-chen/hs-sql-agent/wiki/API-Reference) |
| ❓ FAQ | [FAQ](https://github.com/tse-wei-chen/hs-sql-agent/wiki/FAQ) |

## 🤝 Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) and the [Development](https://github.com/tse-wei-chen/hs-sql-agent/wiki/Development) wiki page.

## 📜 License

[Apache License 2.0](LICENSE)
