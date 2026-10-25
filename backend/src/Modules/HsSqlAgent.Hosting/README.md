# HsSqlAgent.Hosting


Secret protection now requires independent Base64-encoded random 32-byte keys for database passwords, DML payloads, and DML connection fingerprints. The standard host binds the `SecretProtection` configuration section; custom runtime registrations must set `options.SecretProtection`. Legacy encrypted passwords must be re-entered and approvals drained before upgrade. See the [documentation website](https://sql-agent.net/en/docs/).

Official batteries-included ASP.NET Core composition for HsSqlAgent.

Use this package when you want the same capability composition and configuration contract as the first-party `ToolBox` / Docker image. Use `HsSqlAgent.Server` directly when you need to replace individual capabilities, authentication, approval providers, UI, or middleware ordering.

## Install

```bash
dotnet add package HsSqlAgent.Hosting
```

`HsSqlAgent.Hosting` brings in the standard `HsSqlAgent.Server` runtime plus the official generic `HsSqlAgent.Approvals.Webhook` adapter. It does not add runtime NuGet/DLL plugin loading.

## Use

```csharp
using HsSqlAgent.Hosting;

var builder = WebApplication.CreateBuilder(args);

builder.AddHsSqlAgentStandardHost();

var app = builder.Build();

app.UseHsSqlAgentStandardHost();

await app.RunAsync();
```

The standard host owns HsSqlAgent's built-in runtime, Admin Store, built-in identity, MCP endpoint, Admin API/UI, telemetry, exception handling, and DML approval provider selection. URL binding and logging remain normal host-owned ASP.NET Core concerns.

### Required secret protection configuration

Before calling `AddHsSqlAgentStandardHost()`, configure three **independent** randomly generated 32-byte Base64 secrets through the `SecretProtection` configuration section. Missing or invalid keys fail startup. Generate each key separately with `openssl rand -base64 32`, keep them stable across restarts, and use the same configuration on every node:

```json
{
  "SecretProtection": {
    "DatabasePasswords": { "ActiveKeyId": "db-1", "Keys": { "db-1": "<base64-32-byte-key-1>" } },
    "DmlPayloads": { "ActiveKeyId": "dml-1", "Keys": { "dml-1": "<base64-32-byte-key-2>" } },
    "DmlFingerprintKey": "<base64-32-byte-key-3>"
  }
}
```

The bracketed values are **invalid placeholders**, not usable key material. Supply production keys through protected configuration, environment variables or mounted secrets, never source control. The new runtime cannot decrypt legacy stored credentials; consult the [documentation website](https://sql-agent.net/en/docs/) before deploying.

## DML approval provider

The same configuration works in a .NET host and in the official Docker image.

MCP Elicitation is the default:

```json
{
  "DmlApproval": {
    "Provider": "McpElicitation"
  }
}
```

To use the official generic Webhook adapter:

```json
{
  "DmlApproval": {
    "Provider": "Webhook",
    "Webhook": {
      "Endpoint": "https://approval.example.com/hssqlagent/requests",
      "CallbackUrl": "https://sql-agent.example.com/api/hs-sql-agent/approvals/webhook",
      "SigningSecret": "replace-with-a-unique-secret-at-least-32-bytes"
    }
  }
}
```

Environment variables use normal ASP.NET Core configuration names, for example:

```text
DmlApproval__Provider=Webhook
DmlApproval__Webhook__Endpoint=https://approval.example.com/hssqlagent/requests
DmlApproval__Webhook__CallbackUrl=https://sql-agent.example.com/api/hs-sql-agent/approvals/webhook
DmlApproval__Webhook__SigningSecret=replace-with-a-unique-secret-at-least-32-bytes
```

Unknown provider names fail at startup. Standard hosting intentionally owns this selector; if you need a custom `IDmlApprovalProvider`, use the modular `HsSqlAgent.Server` package instead.

## Modular alternative

For an existing ASP.NET Core application that wants to keep its own authentication, authorization, frontend, exception handling, telemetry, or approval composition:

```bash
dotnet add package HsSqlAgent.Server
```

Then opt into only the capabilities you need with the `AddHsSqlAgent*` modular APIs. Optional integrations such as `HsSqlAgent.Approvals.Webhook` can be referenced separately.

This split keeps the library surface composable while giving Docker and batteries-included NuGet users one shared first-party composition.
