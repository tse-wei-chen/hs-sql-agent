# HsSqlAgent.Approvals.Webhook

Official generic webhook adapter for HsSqlAgent DML approvals. It sends transport-neutral approval evidence to an external HTTP workflow and receives a signed asynchronous completion callback. HsSqlAgent still owns SQL validation, approval evidence binding, commit-time revalidation, and atomic execution.

## Choose a consumption path

HsSqlAgent has one standard first-party composition and one modular composition path.

### Standard host / official Docker

Use `HsSqlAgent.Hosting` when a .NET application should behave like the official Docker image:

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

The official `ToolBox` / Docker image uses that same Hosting composition. Both select DML approval through the same ASP.NET Core configuration contract. MCP Elicitation is the default; enable Webhook with:

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

For Docker Compose, the repository maps the ergonomic `.env` variables below onto those same configuration keys:

```env
DML_APPROVAL_PROVIDER=Webhook
DML_APPROVAL_WEBHOOK_ENDPOINT=https://approval.example.com/hssqlagent/requests
DML_APPROVAL_WEBHOOK_CALLBACK_URL=https://sql-agent.example.com/api/hs-sql-agent/approvals/webhook
DML_APPROVAL_WEBHOOK_SIGNING_SECRET=replace-with-a-unique-secret-at-least-32-bytes
```

Set the provider to `McpElicitation` or omit it to keep the built-in MCP approval flow.

### Modular `HsSqlAgent.Server` host

Use this package directly alongside `HsSqlAgent.Server` only when the application intentionally owns its HsSqlAgent composition (for example, existing authentication, custom middleware ordering, or a custom approval provider):

```bash
dotnet add package HsSqlAgent.Server
dotnet add package HsSqlAgent.Approvals.Webhook
```

```csharp
using HsSqlAgent.Approvals.Webhook;
using HsSqlAgent.Server.Extensions;

var hs = builder.Services.AddHsSqlAgentCore();
hs.AddHsSqlAgentRuntime();

builder.Services.AddHsSqlAgentWebhookApproval(options =>
{
    options.Endpoint = new Uri("https://approval.example.com/hssqlagent/requests");
    options.CallbackUrl = new Uri("https://sql-agent.example.com/api/hs-sql-agent/approvals/webhook");
    options.SigningSecret = builder.Configuration["HsSqlAgent:WebhookApproval:SigningSecret"]!;
});

// Register the remaining HsSqlAgent capabilities required by this host.

var app = builder.Build();
app.MapHsSqlAgentWebhookApprovalCallback();
```

In the modular path the registration call itself selects the provider; `DmlApproval:Provider` is a standard-Hosting selector and is not required.

`Endpoint` is the external workflow receiver. `CallbackUrl` is included in every approval request so the external workflow knows where to return an `Approved` or `Rejected` decision. HTTPS is required by default. Set `RequireHttps = false` only for controlled local development.

## Outbound request

HsSqlAgent sends `POST Endpoint` with a JSON `WebhookApprovalRequestEnvelope` containing schema version `1`, the configured callback URL, and the complete `DmlApprovalRequest` evidence.

Headers:

- `X-HsSqlAgent-Webhook-Event: dml.approval.requested`
- `X-HsSqlAgent-Webhook-Timestamp: <unix-seconds>`
- `X-HsSqlAgent-Webhook-Signature: v1=<base64-hmac>`

A `2xx` response accepts the request for asynchronous review. The optional response body is:

```json
{ "externalReference": "CHG001234" }
```

Transport failures are not treated as human rejection; they fail closed and no DML is committed.

## Callback

The external workflow sends `POST CallbackUrl` with:

```json
{
  "requestId": "...",
  "approvalFingerprint": "...",
  "decision": "Approved",
  "approverIdentity": "alice@example.com",
  "externalReference": "CHG001234"
}
```

or `decision: "Rejected"` with an optional `reason`.

Callback headers use the same timestamp/signature format and must set:

- `X-HsSqlAgent-Webhook-Event: dml.approval.completed`

The signature is HMAC-SHA256 over the exact bytes:

```text
UTF8("<unix-seconds>.<event-name>.") || raw-http-body
```

using `SigningSecret`, encoded as `v1=<base64 digest>`. The event name is cryptographically bound so a valid `dml.approval.requested` message cannot be replayed as `dml.approval.completed`. `WebhookApprovalSignature` is public so .NET integrations can generate or verify the protocol without copying cryptographic code.

Callbacks outside the configured timestamp tolerance are rejected. Duplicate valid callbacks are safe: the durable approval lifecycle claims the request once and returns `AlreadyCompleted` or `AlreadyProcessing` instead of executing DML twice.

## Security boundary

This adapter never receives a database connection, transaction, validated execution plan, or commit primitive. An approved callback authorizes only the exact stored approval fingerprint. HsSqlAgent reloads the durable request, revalidates the current access key, database/tool binding, policy, server profile, row evidence and affected-row counts, then creates a fresh short-lived execution challenge before any commit.
