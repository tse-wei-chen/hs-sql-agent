# hs-sql-agent

> **A fail-closed SQL execution and governance boundary for AI agents.**

<img width="1000" height="500" alt="coverImage" src="https://github.com/user-attachments/assets/e317cee2-7bf3-4b11-94b9-4fdeedb29689" />

[![License: Apache 2.0](https://img.shields.io/badge/license-Apache--2.0-green?logo=apache)](https://github.com/tse-wei-chen/hs-sql-agent/blob/main/LICENSE) [![Docker](https://github.com/tse-wei-chen/hs-sql-agent/actions/workflows/docker-publish.yml/badge.svg?event=release)](https://github.com/tse-wei-chen/hs-sql-agent/actions/workflows/docker-publish.yml) [![NuGet](https://img.shields.io/badge/NuGet-Install-0956cc?logo=nuget)](https://www.nuget.org/packages/HsSqlAgent.Hosting) [![CodeQL Advanced](https://github.com/tse-wei-chen/hs-sql-agent/actions/workflows/codeql.yml/badge.svg?event=release)](https://github.com/tse-wei-chen/hs-sql-agent/actions/workflows/codeql.yml) [![Tests](https://github.com/tse-wei-chen/hs-sql-agent/actions/workflows/test.yml/badge.svg)](https://github.com/tse-wei-chen/hs-sql-agent/actions/workflows/test.yml)

`hs-sql-agent` sits between AI-generated SQL and your databases. It accepts raw SQL through MCP, parses it into a structured compiler model, validates source and target capabilities, applies access and execution policy, and only then renders SQL for the target provider.

It supports **PostgreSQL, MySQL, SQL Server, Oracle, SQLite, and Firebird** and can run as the complete first-party server with its Admin UI or be embedded into an existing ASP.NET Core application.

## Why hs-sql-agent?

- **Fail-closed SQL compiler** — Unsupported or unproven syntax is rejected instead of being silently rewritten with different semantics.
- **Closed F# compiler core** — SQL enters a closed discriminated-union AST and advances through unforgeable `parsed → bound → canonical → validated → executable` compiler stages.
- **Six database providers** — PostgreSQL, MySQL, SQL Server, Oracle, SQLite, and Firebird with provider-aware validation and lowering.
- **Safe DML** — Read-only impact preview, one-time approval challenge, commit-time row-set revalidation, and explicit human approval through MCP Elicitation or an approval provider.
- **Governed access** — Per-key database binding, table whitelisting, rate limits, execution limits, roles, policies, and audit records.
- **Flexible hosting** — Run the packaged server and Admin UI, use the standard ASP.NET Core host, or compose advanced integrations from modular capabilities.
- **Production observability** — Prometheus metrics, OpenTelemetry/OTLP, audit retention, and webhook/SIEM delivery.

SQL support is intentionally bounded by proven semantics. See the [SQL Support Reference](https://sql-agent.net/en/docs/sql-compiler/sql-support) for the current contract.

## Quick Start

```bash
cp .env.example .env
# Set HMAC_KEY and JWT_KEY to unique secrets of at least 32 bytes.
docker compose up -d
```

Open the Admin UI at <http://localhost:8080>.

For production settings and deployment options, use the [Configuration Reference](https://sql-agent.net/en/docs/operations/configuration) and [Deployment Guide](https://sql-agent.net/en/docs/operations/deployment).

## Use with an MCP client

Set `MCP_PUBLIC_ENDPOINT` to the externally reachable MCP URL, including `/mcp`, before issuing production keys.

Then open **Runtime → MCP Keys** in the Admin UI and issue a key. The one-time **Save and connect** dialog generates ready-to-paste configuration for **Claude Desktop, Cursor, Visual Studio Code, and generic Streamable HTTP clients**.

The plaintext secret is shown only once. See [MCP Client Onboarding](https://sql-agent.net/en/docs/mcp/client-onboarding) for client setup, compatibility, and DML Elicitation requirements.

## Use from .NET

For the same batteries-included composition as the official Docker host, install `HsSqlAgent.Hosting`:

```bash
dotnet add package HsSqlAgent.Hosting
```

```csharp
using HsSqlAgent.Hosting;

var builder = WebApplication.CreateBuilder(args);
builder.AddHsSqlAgentStandardHost();

var app = builder.Build();
app.UseHsSqlAgentStandardHost();

await app.RunAsync();
```

Use `HsSqlAgent.Server` directly only when you need custom authentication, middleware ordering, approval providers, UI, or capability composition.

See the [ASP.NET Core Integration Guide](https://sql-agent.net/en/docs/integration/aspnet-core) and the [`HsSqlAgent.Hosting` package README](backend/src/Modules/HsSqlAgent.Hosting/README.md) for the full integration contract.

## How SQL execution works

1. Authenticate the MCP key and establish its database, table, and execution-policy scope.
2. Parse SQL into the closed compiler model and bind source semantics.
3. Normalize and validate syntax, semantics, capabilities, and policy.
4. Render only an executable typestate into provider-specific SQL and parameters.
5. Execute within configured runtime limits.

The compiler core is provider-driver-free: parsing, validation, normalization, capability proof, lowering, and rendering are kept separate from database drivers and runtime execution.

For DML, hs-sql-agent first builds a read-only impact preview, binds approval to the validated plan and matched row set, requires explicit human approval, and revalidates inside the commit transaction before applying the mutation.

Custom SQL tools pass through the same compiler, access policy, and execution limits as built-in tools.

## SQL Execution Flow

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="miscellaneous/diagram-black.png">
  <source media="(prefers-color-scheme: light)" srcset="miscellaneous/diagram-light.png">
  <img alt="SQL Execution Flow" src="miscellaneous/diagram-light.png">
</picture>

### DML Approval Prompt

<img width="1199" height="890" alt="dml-approval-prompt" src="https://github.com/user-attachments/assets/ebd40519-e0b3-43d6-9e83-24238f3c00d6" />

## Documentation

The documentation site is the source of truth for detailed configuration, integration, SQL capability, security, and operations guidance:

- [Documentation Home](https://sql-agent.net/en/docs/)
- [Quick Start](https://sql-agent.net/en/docs/getting-started/quick-start)
- [ASP.NET Core Integration](https://sql-agent.net/en/docs/integration/aspnet-core)
- [SQL Support Reference](https://sql-agent.net/en/docs/sql-compiler/sql-support)
- [Security Overview](https://sql-agent.net/en/docs/security/overview)

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) and the [Architecture and Contribution Flow](https://sql-agent.net/en/docs/development/architecture).

## License

[Apache License 2.0](LICENSE)
