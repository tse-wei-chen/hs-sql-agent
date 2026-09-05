# HsSqlAgent.Approvals.Abstractions

Transport-neutral contracts for replacing HsSqlAgent's default MCP Elicitation DML approval with a host-owned approval integration.

## Immediate approval

```csharp
using HsSqlAgent.Approvals;

public sealed class CompanyApprovalProvider : IDmlApprovalProvider
{
    public async ValueTask<DmlApprovalResult> RequestApprovalAsync(
        DmlApprovalRequest request,
        CancellationToken cancellationToken = default)
    {
        var approvedBy = await CompanyApprovalApi.WaitForDecisionAsync(request, cancellationToken);

        return approvedBy is null
            ? DmlApprovalResult.Reject(request, "Rejected by company approval workflow.")
            : DmlApprovalResult.Approve(request, approvedBy);
    }
}
```

Register it with the Server package:

```csharp
var hs = builder.Services.AddHsSqlAgentCore();
hs.AddHsSqlAgentRuntime();
hs.AddHsSqlAgentDmlApproval<CompanyApprovalProvider>();
```

## Durable external approval

An asynchronous adapter may create a ticket or workflow item and return `Pending` instead of keeping the MCP request open:

```csharp
public sealed class CompanyApprovalProvider : IDmlApprovalProvider
{
    public async ValueTask<DmlApprovalResult> RequestApprovalAsync(
        DmlApprovalRequest request,
        CancellationToken cancellationToken = default)
    {
        var externalId = await CompanyApprovalApi.CreateAsync(request, cancellationToken);
        return DmlApprovalResult.Pending(request, externalId);
    }
}
```

When the external system later reaches a final decision, the adapter passes that decision back to the host-provided completion sink:

```csharp
public sealed class CompanyApprovalCallback(IDmlApprovalCompletionSink completionSink)
{
    public ValueTask<DmlApprovalCompletionResult> ApproveAsync(
        string requestId,
        string approvalFingerprint,
        string approver,
        string externalReference,
        CancellationToken cancellationToken = default) =>
        completionSink.CompleteAsync(
            DmlApprovalCompletion.Approve(
                requestId,
                approvalFingerprint,
                approver,
                externalReference),
            cancellationToken);
}
```

Durable pending approvals require the HsSqlAgent Admin Store. The current Server runtime persists the protected resume intent for up to `DmlApprovalRequest.DurableUntil`. A completion does **not** execute the old preview session: HsSqlAgent reloads current authorization and database configuration, reparses and previews the DML, compares the approved evidence, and creates a fresh short-lived execution challenge. Changed or revoked authorization, database configuration, custom-tool revision, policy, plan, row set, or affected-row evidence makes the request stale instead of committing it.

The approval provider and callback adapter never receive a database connection, transaction, validated execution plan, or commit primitive. HsSqlAgent retains ownership of SQL validation, evidence binding, commit-time revalidation, and atomic execution.
