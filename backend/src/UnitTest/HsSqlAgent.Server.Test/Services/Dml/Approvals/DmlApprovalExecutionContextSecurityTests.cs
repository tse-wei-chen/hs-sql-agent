using System.Security.Claims;
using Common.Models;
using HsSqlAgent.Server.Services;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

public class DmlApprovalExecutionContextSecurityTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ComputeApprovalContextFingerprint_RejectsMissingDatabaseIdentity(
        string? databaseIdentity)
    {
        var context = new DmlApprovalExecutionContext(
            "mcp-key:7",
            "db-management:11",
            SqlAgentToolType.Postgres,
            databaseIdentity!);

        Assert.ThrowsAny<ArgumentException>(() =>
            TypedDmlRuntime.ComputeApprovalContextFingerprint(context));
    }

    [Fact]
    public void FromMcp_RejectsMissingDatabaseName()
    {
        var context = new DefaultHttpContext();
        context.Items[McpContextItemKeys.AccessKeyId] = 7;
        context.Items[McpContextItemKeys.DbManagementId] = 11;

        var error = Assert.Throws<UnauthorizedAccessException>(() =>
            DmlApprovalExecutionContextResolver.FromMcp(
                context,
                SqlAgentToolType.Postgres));

        Assert.Contains("database name", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FromAdmin_RejectsMissingDatabaseName()
    {
        var principal = new ClaimsPrincipal(
            new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "admin-1")],
                "test"));

        var error = Assert.Throws<UnauthorizedAccessException>(() =>
            DmlApprovalExecutionContextResolver.FromAdmin(
                principal,
                11,
                SqlAgentToolType.Postgres,
                null));

        Assert.Contains("database name", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
