using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HsSqlAgent.Approvals;
using HsSqlAgent.Server.Services;
using SqlAgent.Service.Core.Execution;

namespace HsSqlAgent.Server.Tools;

internal static class DmlApprovalRequestFactory
{
    internal static DmlApprovalRequest Create(
        string title,
        DmlApprovalExecutionContext approvalContext,
        TypedDmlApprovalSession session) =>
        CreateCore(
            title,
            approvalContext,
            [ToStatement(session, 1)],
            session.Preview.Challenge);

    internal static DmlApprovalRequest Create(
        string title,
        DmlApprovalExecutionContext approvalContext,
        TypedDmlTransactionApprovalSession session) =>
        CreateCore(
            title,
            approvalContext,
            session.Statements.Select((statement, index) => ToStatement(statement, index + 1)).ToArray(),
            session.Challenge);

    internal static string ComputeEvidenceFingerprint(TypedDmlApprovalSession session) =>
        ComputeEvidenceFingerprint(session.Preview.Challenge);

    internal static string ComputeEvidenceFingerprint(TypedDmlTransactionApprovalSession session) =>
        ComputeEvidenceFingerprint(session.Challenge);

    internal static void EnsureBoundResult(
        DmlApprovalRequest request,
        DmlApprovalResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!string.Equals(
                request.ApprovalFingerprint,
                result.ApprovalFingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "DML approval provider returned a decision for different approval evidence.");
        }
    }

    private static DmlApprovalRequest CreateCore(
        string title,
        DmlApprovalExecutionContext approvalContext,
        IReadOnlyList<DmlApprovalStatement> statements,
        DmlApprovalChallenge challenge) =>
        new(
            RequestId: "dml_" + Guid.NewGuid().ToString("N"),
            Title: title,
            RequesterIdentity: approvalContext.PrincipalIdentity,
            TargetIdentity: approvalContext.TargetIdentity,
            DatabaseProvider: approvalContext.Provider.ToString(),
            DatabaseIdentity: approvalContext.DatabaseIdentity,
            Statements: statements,
            TotalAffectedRows: challenge.AffectedRows,
            ApprovalFingerprint: ComputeApprovalFingerprint(challenge),
            IssuedAt: challenge.IssuedAt,
            ExpiresAt: challenge.ExpiresAt);

    private static DmlApprovalStatement ToStatement(
        TypedDmlApprovalSession statement,
        int index) =>
        new(
            Index: index,
            Operation: statement.Plan.Operation.ToString().ToUpperInvariant(),
            TableName: statement.Plan.TableName,
            AffectedRows: statement.Preview.AffectedRows,
            PreviewJson: JsonSerializer.Serialize(statement.Preview.Rows));

    private static string ComputeApprovalFingerprint(DmlApprovalChallenge challenge)
    {
        var material =
            "v1|" +
            StableEvidenceMaterial(challenge) + "|" +
            challenge.IssuedAt.ToUnixTimeMilliseconds() + "|" +
            challenge.ExpiresAt.ToUnixTimeMilliseconds() + "|" +
            Component(challenge.Nonce);
        return Hash(material);
    }

    private static string ComputeEvidenceFingerprint(DmlApprovalChallenge challenge) =>
        Hash("durable-v1|" + StableEvidenceMaterial(challenge));

    private static string StableEvidenceMaterial(DmlApprovalChallenge challenge) =>
        Component(challenge.PlanFingerprint) + "|" +
        Component(challenge.RowSetFingerprint ?? string.Empty) + "|" +
        challenge.AffectedRows + "|" +
        Component(challenge.PolicyVersion) + "|" +
        Component(challenge.ApprovalContextFingerprint);

    private static string Hash(string material) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));

    private static string Component(string value) =>
        Encoding.UTF8.GetByteCount(value) + ":" + value;
}
