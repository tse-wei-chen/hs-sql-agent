# HsSqlAgent.Approvals.Abstractions

Transport-neutral contracts for replacing HsSqlAgent's default MCP Elicitation DML approval with a host-owned approval integration.

```csharp
using HsSqlAgent.Approvals;

public sealed class CompanyApprovalProvider : IDmlApprovalProvider
{
    public async ValueTask<DmlApprovalResult> RequestApprovalAsync(
        DmlApprovalRequest request,
        CancellationToken cancellationToken = default)
    {
        // Send request to an internal approval service, ITSM platform, OA workflow, etc.
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

The approval provider never receives a database connection, transaction, validated execution plan, or commit primitive. HsSqlAgent creates the preview and approval fingerprint, then revalidates policy, execution context, server profile, and approved row evidence before committing.

`DmlApprovalDecision.Pending` is part of the contract so external workflow adapters do not need a future breaking API change. In the current Server runtime, a pending result does not commit and is not resumable; an adapter that needs immediate execution must return `Approved` only after its external workflow has completed. Durable pending/resume support is intentionally a separate feature.
