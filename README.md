# hs-sql-agent

> **A high-performance MCP server for secure SQL access and enterprise governance.**

<img src="miscellaneous/coverImage.png" alt="hs-sql-agent" />

[![License: Apache 2.0](https://img.shields.io/badge/license-Apache--2.0-green?logo=apache)](https://github.com/tse-wei-chen/hs-sql-agent/blob/main/LICENSE) [![Docker](https://github.com/tse-wei-chen/hs-sql-agent/actions/workflows/docker-publish.yml/badge.svg?event=release)](https://github.com/tse-wei-chen/hs-sql-agent/actions/workflows/docker-publish.yml) [![NuGet](https://img.shields.io/badge/NuGet-Install-0956cc?logo=nuget)](https://www.nuget.org/packages/HsSqlAgent.Server) [![CodeQL Advanced](https://github.com/tse-wei-chen/hs-sql-agent/actions/workflows/codeql.yml/badge.svg?event=release)](https://github.com/tse-wei-chen/hs-sql-agent/actions/workflows/codeql.yml) [![Tests](https://github.com/tse-wei-chen/hs-sql-agent/actions/workflows/test.yml/badge.svg)](https://github.com/tse-wei-chen/hs-sql-agent/actions/workflows/test.yml) [![Deploy on Zeabur](https://img.shields.io/badge/Deploy_on-Zeabur-blueviolet?logo=zeabur)](https://zeabur.com/templates/RFPWDU)

`hs-sql-agent` connects MCP clients to SQLite, PostgreSQL, MySQL, SQL Server, Oracle, and Firebird through an HTTP MCP endpoint and a built-in Admin Panel.

## Why hs-sql-agent?

Instead of executing unrestricted LLM-generated SQL, the server parses supported SQL into structured definitions, validates it, and rebuilds the final statement through a provider-specific SQL compiler.

- **Six database providers** — SQLite, PostgreSQL, MySQL, SQL Server, Oracle, and Firebird.
- **Governed access** — Per-key database binding, table whitelisting, CORS, rate limits, and execution policies.
- **Safe DML** — Transactional dry-run followed by MCP Elicitation for explicit human approval.
- **Admin Panel** — Manage databases, keys, roles, custom tools, audit records, and runtime policies.
- **Enterprise ready** — OIDC SSO, TOTP MFA, audit retention, Prometheus metrics, OTLP, and webhook/SIEM delivery.
- **Semantic metadata** — Table and column synonyms, relationships, and scoped metric metadata for schema discovery.

SQL support is intentionally bounded: unsupported syntax is rejected instead of silently changing its meaning. See the [MCP Tools Reference](https://github.com/tse-wei-chen/hs-sql-agent/wiki/MCP-Tools-Reference) for the supported SQL contract.

## Quick Start

```bash
cp .env.example .env
# Set HMAC_KEY and JWT_KEY to unique secrets of at least 32 bytes.
docker compose up -d
```

Open the Admin Panel at <http://localhost:8080>. Configuration options and production deployment guidance are documented in the [Wiki](https://github.com/tse-wei-chen/hs-sql-agent/wiki).

## Use with an MCP client

Create an MCP key in the Admin Panel. The key dialog displays the plaintext secret once and generates configuration for Claude Desktop, Cursor, and generic Streamable HTTP clients.

Set `MCP_PUBLIC_ENDPOINT` to the externally reachable MCP URL, including `/mcp`. For client compatibility, onboarding, and DML Elicitation requirements, see [MCP client onboarding](docs/mcp-onboarding.md).

## NuGet for existing .NET APIs

Embed the MCP SQL Agent and optional Admin UI in an ASP.NET Core application:

```bash
dotnet add package HsSqlAgent.Server
```

```csharp
builder.Services.AddHsSqlAgent(options => { ... });
app.UseHsSqlAgent();                    // API only
// app.UseHsSqlAgent().ServeAdminUi();  // API and Admin UI
```

See the [NuGet Package guide](https://github.com/tse-wei-chen/hs-sql-agent/wiki/NuGet-Package) for configuration and deployment details.

## How SQL execution works

1. Authenticate the MCP key and apply its database, table, and policy scope.
2. Parse supported SQL into a structured definition.
3. Validate the definition and compile it for the configured database provider.
4. Execute queries within configured limits.
5. For DML, dry-run in a transaction and require human approval through MCP Elicitation before commit.

Custom SQL tools pass through the same parser, validation, access policy, and execution limits as built-in tools. Lifecycle, parameter, and publishing rules are documented in the [Admin Panel guide](https://github.com/tse-wei-chen/hs-sql-agent/wiki/Admin-Panel).

## Documentation

| Topic | Documentation |
|---|---|
| Getting started | [Getting Started](https://github.com/tse-wei-chen/hs-sql-agent/wiki/Getting-Started) |
| Configuration | [Configuration](https://github.com/tse-wei-chen/hs-sql-agent/wiki/Configuration) |
| Admin Panel | [Admin Panel](https://github.com/tse-wei-chen/hs-sql-agent/wiki/Admin-Panel) |
| MCP tools and SQL support | [MCP Tools Reference](https://github.com/tse-wei-chen/hs-sql-agent/wiki/MCP-Tools-Reference) |
| Security, OIDC, and MFA | [Security Governance](https://github.com/tse-wei-chen/hs-sql-agent/wiki/Security-Governance) |
| Deployment and observability | [Deployment](https://github.com/tse-wei-chen/hs-sql-agent/wiki/Deployment) · [Distributed Deployment](https://github.com/tse-wei-chen/hs-sql-agent/wiki/Distributed-Deployment) |
| API | [API Reference](https://github.com/tse-wei-chen/hs-sql-agent/wiki/API-Reference) |
| Troubleshooting | [Troubleshooting](https://github.com/tse-wei-chen/hs-sql-agent/wiki/Troubleshooting) |
| Development | [Development](https://github.com/tse-wei-chen/hs-sql-agent/wiki/Development) |


## SQL Execution Flow

```mermaid
%%{init: { 'theme': 'neutral' }}%%
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

    classDef client fill:#f3f4f6,stroke:#374151,stroke-width:1px,color:#111827;
    classDef server fill:#e5e7eb,stroke:#4b5563,stroke-width:1px,color:#1f2937;
    classDef auth fill:#f9fafb,stroke:#9ca3af,stroke-width:1px,color:#4b5563;
    classDef cond fill:#ffffff,stroke:#111827,stroke-width:1.5px,color:#111827;
    classDef danger fill:#fee2e2,stroke:#ef4444,stroke-width:1px,color:#991b1b;
    classDef success fill:#dcfce7,stroke:#22c55e,stroke-width:1px,color:#166534;

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

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) and the [Development guide](https://github.com/tse-wei-chen/hs-sql-agent/wiki/Development).

## License

[Apache License 2.0](LICENSE)
