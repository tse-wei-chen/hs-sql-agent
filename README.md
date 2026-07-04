# hs-sql-agent
> **The high-performance MCP server for instant SQL interaction and secure enterprise governance.**
<img src="miscellaneous\coverImage.png" />

[![License: Apache 2.0](https://img.shields.io/badge/license-Apache--2.0-green?logo=apache)](https://github.com/tse-wei-chen/hs-sql-agent/blob/main/LICENSE) [![Docker](https://github.com/tse-wei-chen/hs-sql-agent/actions/workflows/docker-publish.yml/badge.svg?event=release)](https://github.com/tse-wei-chen/hs-sql-agent/actions/workflows/docker-publish.yml) [![NuGet](https://img.shields.io/badge/NuGet-Install-0956cc?logo=nuget)](https://www.nuget.org/packages/HsSqlAgent.Server) [![CodeQL Advanced](https://github.com/tse-wei-chen/hs-sql-agent/actions/workflows/codeql.yml/badge.svg?event=release)](https://github.com/tse-wei-chen/hs-sql-agent/actions/workflows/codeql.yml) [![Tests](https://github.com/tse-wei-chen/hs-sql-agent/actions/workflows/test.yml/badge.svg)](https://github.com/tse-wei-chen/hs-sql-agent/actions/workflows/test.yml) [![Deploy on Zeabur](https://img.shields.io/badge/Deploy_on-Zeabur-blueviolet?logo=zeabur)](https://zeabur.com/templates/RFPWDU)

`hs-sql-agent` is an HTTP MCP server for relational databases (SQLite, PostgreSQL, MySQL, SQL Server, Oracle, Firebird) with a built-in Admin Panel for governance.

## 🤔 Why hs-sql-agent?

Most "Chat with your Data" tools ask the LLM to write raw SQL — a recipe for hallucinations, dialect confusion, and injection risks. **hs-sql-agent takes a structured approach**: the AI can write SQL, the server parses it into structured definitions, validates the result, and rebuilds the final query through the SQL builder before execution. Zero hallucinated syntax, zero direct string injection into the database.

- **Structured SQL Pipeline** — The AI can write SQL, but the server parses it into structured definitions, validates it, and rebuilds the final query through the SQL builder before execution.
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

## SQL Execution Flow

```mermaid
%%{init: {
  'theme': 'base',
  'themeVariables': {
    'primaryColor': '#18181B',
    'primaryTextColor': '#FAFAFA',
    'primaryBorderColor': '#27272A',
    'lineColor': '#52525B',
    'secondaryColor': '#27272A',
    'tertiaryColor': '#09090B',
    'mainBkg': '#09090B'
  }
}}%%

flowchart TD
    LLM(["Client :: LLM / MCP Client"])
    MCP["Server :: HsSqlAgent"]
    AUTH["Middleware :: Authentication<br/>Access Key | DB Binding | Whitelist"]
    ROUTE{"Gateway :: Router"}

    LLM -->|Call tool with SQL| MCP
    MCP --> AUTH
    AUTH --> ROUTE

    subgraph Query_Flow [" 🔍 Query Pipeline (SELECT) "]
        QPARSE["Parse Query SQL<br/>SqlDefinitionParser.ParseQuery"]
        QDEF["QueryDefinition<br/>AST Structure Data"]
        QVALID["DefinitionValidator<br/>Rule Verification"]
        QBUILD["SQL Strategy Compiler<br/>Compile Strategy"]
        QEXEC["Execution Engine<br/>Execute SELECT"]
        QRESULT(["Result :: Rows / JSON"])
        
        QPARSE --> QDEF
        QDEF --> QVALID
        QVALID --> QBUILD
        QBUILD --> QEXEC
        QEXEC --> QRESULT
    end

    subgraph DML_Flow [" ✏️ Mutation Pipeline (DML) "]
        DPARSE["Parse Mutation SQL<br/>SqlDefinitionParser.ParseDml"]
        DDEF["DmlDefinition<br/>AST Structure Data"]
        DVALID["DefinitionValidator<br/>Rule Verification"]
        DRYRUN["Transaction Dry-run<br/>Uncommitted State"]
        ELICIT["MCP Elicitation<br/>User Approval Prompt"]
        DECIDE{" Action :: Decision"}
        DEXEC["Transaction :: Commit<br/>Apply Changes"]
        DROLLBACK["Transaction :: Rollback<br/>Discard Changes"]
        
        DPARSE --> DDEF
        DDEF --> DVALID
        DVALID --> DRYRUN
        DRYRUN --> ELICIT
        ELICIT --> DECIDE
        
        DECIDE -->|Allowed| DEXEC
        DECIDE -->|Denied| DROLLBACK
    end

    ROUTE -->|execute_query_sql| QPARSE
    ROUTE -->|execute_dml_sql| DPARSE

    AUDIT[("Storage :: Async Audit Log")]
    
    QRESULT --> AUDIT
    DEXEC --> AUDIT
    DROLLBACK --> AUDIT

    classDef default fill:#18181B,stroke:#27272A,stroke-width:1px,color:#E4E4E7;
    classDef client fill:#FAFAFA,stroke:#FAFAFA,stroke-width:1px,color:#09090B;
    classDef server fill:#27272A,stroke:#3F3F46,stroke-width:1px,color:#F4F4F5;
    classDef auth fill:#09090B,stroke:#27272A,stroke-width:1px,color:#A1A1AA;
    classDef cond fill:#18181B,stroke:#FAFAFA,stroke-width:1.5px,color:#FAFAFA;
    
    classDef danger fill:#451A03,stroke:#7F1D1D,stroke-width:1px,color:#FCA5A5;
    classDef success fill:#022C22,stroke:#064E3B,stroke-width:1px,color:#86EFAC;

    class LLM client;
    class MCP,AUDIT server;
    class AUTH auth;
    class ROUTE,DECIDE cond;
    class QRESULT,DEXEC success;
    class DROLLBACK danger;
```

### DML Approval Prompt

This is what the human-in-the-loop approval step looks like during `execute_dml_sql`:

<img src="miscellaneous/dml-approval-prompt.png" />

## 🤝 Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) and the [Development](https://github.com/tse-wei-chen/hs-sql-agent/wiki/Development) wiki page.

## 📜 License

[Apache License 2.0](LICENSE)
