using HsSqlAgent.Approvals;
using HsSqlAgent.Server.Tools;
using Xunit;

namespace HsSqlAgent.Server.Test.Tools;

public sealed class DmlApprovalRequestFactoryTests
{
    [Fact]
    public void EnsureBoundResult_RejectsDecisionForDifferentEvidence()
    {
        var request = CreateRequest("APPROVED-EVIDENCE");
        var mismatched = new DmlApprovalResult(
            DmlApprovalDecision.Approved,
            "DIFFERENT-EVIDENCE",
            ApproverIdentity: "external-reviewer");

        var error = Assert.Throws<InvalidOperationException>(() =>
            DmlApprovalRequestFactory.EnsureBoundResult(request, mismatched));

        Assert.Contains("different approval evidence", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnsureBoundResult_AcceptsDecisionBoundToExactEvidence()
    {
        var request = CreateRequest("APPROVED-EVIDENCE");
        var approved = DmlApprovalResult.Approve(
            request,
            approverIdentity: "external-reviewer",
            externalReference: "CHG001234");

        DmlApprovalRequestFactory.EnsureBoundResult(request, approved);

        Assert.Equal(request.ApprovalFingerprint, approved.ApprovalFingerprint);
        Assert.Equal("external-reviewer", approved.ApproverIdentity);
        Assert.Equal("CHG001234", approved.ExternalReference);
    }

    private static DmlApprovalRequest CreateRequest(string fingerprint) =>
        new(
            RequestId: "dml_test",
            Title: "Delete order",
            RequesterIdentity: "mcp-key:7",
            TargetIdentity: "db-management:42",
            DatabaseProvider: "Postgres",
            DatabaseIdentity: "orders",
            Statements:
            [
                new DmlApprovalStatement(
                    Index: 1,
                    Operation: "DELETE",
                    TableName: "orders",
                    AffectedRows: 1,
                    PreviewJson: "[]")
            ],
            TotalAffectedRows: 1,
            ApprovalFingerprint: fingerprint,
            IssuedAt: DateTimeOffset.UtcNow,
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(5));
}
