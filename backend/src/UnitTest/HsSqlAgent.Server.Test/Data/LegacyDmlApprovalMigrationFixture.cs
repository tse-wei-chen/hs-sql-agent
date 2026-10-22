using Admin.Service.Data;
using Admin.Service.Data.Entites;
using Microsoft.EntityFrameworkCore;

namespace HsSqlAgent.Server.Test.Data;

/// <summary>
/// Writes historical v2 approval rows using their original physical schema.
/// The current EF entity has a v3-only AdmissionProtocolVersion property, so
/// using Add/SaveChanges while testing a pre-v3 database would be incorrect.
/// </summary>
internal static class LegacyDmlApprovalMigrationFixture
{
    internal static int Insert(AdminContext context, DmlApprovalRequestState row) =>
        context.Database.ExecuteSqlInterpolated($"""
            INSERT INTO "DmlApprovalRequests" (
                "RequestId", "ApprovalFingerprint", "EvidenceFingerprint", "Status",
                "ProtectedExecutionPayload", "RequesterIdentity", "TargetIdentity",
                "DatabaseProvider", "DatabaseIdentity", "AccessKeyId",
                "DbManagementId", "RequiredToolName", "StatementCount",
                "TotalAffectedRows", "CreatedAt", "UpdatedAt", "ExpiresAt")
            VALUES (
                {row.RequestId}, {row.ApprovalFingerprint}, {row.EvidenceFingerprint}, {row.Status},
                {row.ProtectedExecutionPayload}, {row.RequesterIdentity}, {row.TargetIdentity},
                {row.DatabaseProvider}, {row.DatabaseIdentity}, {row.AccessKeyId},
                {row.DbManagementId}, {row.RequiredToolName}, {row.StatementCount},
                {row.TotalAffectedRows}, {row.CreatedAt}, {row.UpdatedAt}, {row.ExpiresAt})
            """);

    internal static Task<int> InsertAsync(AdminContext context, DmlApprovalRequestState row,
        CancellationToken cancellationToken = default) =>
        context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "DmlApprovalRequests" (
                "RequestId", "ApprovalFingerprint", "EvidenceFingerprint", "Status",
                "ProtectedExecutionPayload", "RequesterIdentity", "TargetIdentity",
                "DatabaseProvider", "DatabaseIdentity", "AccessKeyId",
                "DbManagementId", "RequiredToolName", "StatementCount",
                "TotalAffectedRows", "CreatedAt", "UpdatedAt", "ExpiresAt")
            VALUES (
                {row.RequestId}, {row.ApprovalFingerprint}, {row.EvidenceFingerprint}, {row.Status},
                {row.ProtectedExecutionPayload}, {row.RequesterIdentity}, {row.TargetIdentity},
                {row.DatabaseProvider}, {row.DatabaseIdentity}, {row.AccessKeyId},
                {row.DbManagementId}, {row.RequiredToolName}, {row.StatementCount},
                {row.TotalAffectedRows}, {row.CreatedAt}, {row.UpdatedAt}, {row.ExpiresAt})
            """, cancellationToken);
}
