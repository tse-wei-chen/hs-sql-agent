# HsSqlAgent.Approvals.Webhook

Official generic webhook adapter for HsSqlAgent DML approvals. It sends the transport-neutral approval evidence to an external HTTP workflow and receives a signed asynchronous completion callback. HsSqlAgent still owns SQL validation, approval evidence binding, commit-time revalidation, and atomic execution.

## Registration

```csharp
using HsSqlAgent.Approvals.Webhook;

builder.Services.AddHsSqlAgentWebhookApproval(options =>
{
    options.Endpoint = new Uri("https://approval.example.com/hssqlagent/requests");
    options.CallbackUrl = new Uri("https://sql-agent.example.com/api/hs-sql-agent/approvals/webhook");
    options.SigningSecret = builder.Configuration["HsSqlAgent:WebhookApproval:SigningSecret"]!;
});

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
UTF8("<unix-seconds>.") || raw-http-body
```

using `SigningSecret`, encoded as `v1=<base64 digest>`. `WebhookApprovalSignature` is public so .NET integrations can generate or verify the protocol without copying cryptographic code.

Callbacks outside the configured timestamp tolerance are rejected. Duplicate valid callbacks are safe: the durable approval lifecycle claims the request once and returns `AlreadyCompleted` or `AlreadyProcessing` instead of executing DML twice.

## Security boundary

This adapter never receives a database connection, transaction, validated execution plan, or commit primitive. An approved callback authorizes only the exact stored approval fingerprint. HsSqlAgent reloads the durable request, revalidates the current access key, database/tool binding, policy, server profile, row evidence and affected-row counts, then creates a fresh short-lived execution challenge before any commit.
