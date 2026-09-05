# HsSqlAgent.Approvals.Webhook

Official generic webhook adapter for HsSqlAgent DML approvals. It sends the transport-neutral approval evidence to an external HTTP workflow and receives a signed asynchronous completion callback. HsSqlAgent still owns SQL validation, approval evidence binding, commit-time revalidation, and atomic execution.

## How to use it

There are two supported consumption paths.

### Standalone Docker image

The first-party `ToolBox` host already composes this adapter into the official Docker image. No extra NuGet installation or custom image is required. MCP Elicitation remains the default; opt into Webhook with environment variables:

```env
DML_APPROVAL_PROVIDER=Webhook
DML_APPROVAL_WEBHOOK_ENDPOINT=https://approval.example.com/hssqlagent/requests
DML_APPROVAL_WEBHOOK_CALLBACK_URL=https://sql-agent.example.com/api/hs-sql-agent/approvals/webhook
DML_APPROVAL_WEBHOOK_SIGNING_SECRET=replace-with-a-unique-secret-at-least-32-bytes
```

The standard `docker-compose.yml` and distributed compose map these variables to the standalone host configuration. Set `DML_APPROVAL_PROVIDER=McpElicitation` or omit it to keep the built-in MCP approval flow.

### Embedded ASP.NET Core / `HsSqlAgent.Server` NuGet

Applications embedding `HsSqlAgent.Server` add the independent `HsSqlAgent.Approvals.Webhook` package alongside Server. No Server fork or source modification is required; the application remains the composition root:

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

// Register the remaining HsSqlAgent capabilities as usual.

var app = builder.Build();
app.MapHsSqlAgentWebhookApprovalCallback();
```

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
