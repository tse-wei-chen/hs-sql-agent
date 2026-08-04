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

## Enterprise authentication

The Admin Panel supports optional OIDC SSO with PKCE, verified-email account linking, configurable claim/role mapping, and auto-provisioning. The callback returns a short-lived one-time exchange code; access and refresh tokens are never placed in redirect URLs. Local password login remains available for a break-glass administrator.

TOTP MFA and one-time recovery codes are managed from **Account**. Roles listed in `EnterpriseIdentity:RequireMfaForRoles` (by default `SuperUser`) cannot use the Admin Panel until MFA is enrolled. Persist `EnterpriseIdentity:DataProtectionKeyPath` across deployments so existing TOTP secrets remain decryptable.

For Active Directory or LDAP environments, connect the directory to an OIDC/SAML identity provider and configure that provider here. Direct LDAP binding is intentionally a separate future integration, not part of the OIDC settings.

## Operability and audit

The optional Operability page reports scheduled database health, Query/DML success and latency, slow operations, per-key MCP activity, and IP/key rate-limit rejections. IP rejection metrics are aggregated in memory and flushed in batches, so the pre-auth IP limiter never performs a governance-database lookup for each rejected request.

Audit results can be exported as filtered CSV or JSON through a separate `audit.export` permission. Retention supports dry-run estimates and scheduled purge or JSONL archive; a completed retention run writes its own audit event. Set `Operability:AuditRetentionDays` to `0` to disable scheduled retention.

`Operability:AlertWebhookUrl` receives deduplicated database-unhealthy events. `Operability:SiemWebhookUrl` receives redacted audit events through the durable delivery outbox. Both integrations require a 32-byte webhook secret, use an `X-Hs-Signature: sha256=...` HMAC header, retry failed delivery, and expose pending/delivered/dead-letter status in the Admin Panel. Leave their URLs empty to disable them.

## Custom SQL tools

Custom tools are saved as database-bound SQL templates. Saving creates or updates a draft; only an explicit Publish makes an immutable revision available to new MCP sessions, and only keys bound to the same database can discover or execute it. Disable removes it from new sessions, while rollback republishes an earlier snapshot as a new revision.

Use unquoted `{{parameterName}}` placeholders for scalar values declared as string, number, or boolean. The server converts values to escaped SQL literals, then runs the resulting statement through the same runtime parser, AST validation, table whitelist, security policy, and concurrency limit as the built-in SQL tools. Parameters cannot substitute identifiers or arbitrary SQL fragments. DML test execution always rolls back; published DML still requires MCP Elicitation before commit.

## 📖 Documentation

Detailed docs are on the [Wiki](https://github.com/tse-wei-chen/hs-sql-agent/wiki):

| Topic | Link |
|-------|------|
| 🚀 Getting Started | [Getting-Started](https://github.com/tse-wei-chen/hs-sql-agent/wiki/Getting-Started) |
| 🖥️ Admin Panel | [Admin-Panel](https://github.com/tse-wei-chen/hs-sql-agent/wiki/Admin-Panel) |
| 🔐 Security Governance | [Security-Governance](https://github.com/tse-wei-chen/hs-sql-agent/wiki/Security-Governance) |
| 🛠️ Troubleshooting | [Troubleshooting](https://github.com/tse-wei-chen/hs-sql-agent/wiki/Troubleshooting) |
| ⚙️ Configuration | [Configuration](https://github.com/tse-wei-chen/hs-sql-agent/wiki/Configuration) |
| 🐳 Deployment | [Deployment](https://github.com/tse-wei-chen/hs-sql-agent/wiki/Deployment) |
| 🌐 Distributed Deployment | [Distributed-Deployment](https://github.com/tse-wei-chen/hs-sql-agent/wiki/Distributed-Deployment) |
| 📘 MCP Tools | [MCP-Tools-Reference](https://github.com/tse-wei-chen/hs-sql-agent/wiki/MCP-Tools-Reference) |
| 📡 API Reference | [API-Reference](https://github.com/tse-wei-chen/hs-sql-agent/wiki/API-Reference) |
| 🏠 Development | [Development](https://github.com/tse-wei-chen/hs-sql-agent/wiki/Development) |

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

## 🤝 Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) and the [Development](https://github.com/tse-wei-chen/hs-sql-agent/wiki/Development) wiki page.

## 📜 License

[Apache License 2.0](LICENSE)
