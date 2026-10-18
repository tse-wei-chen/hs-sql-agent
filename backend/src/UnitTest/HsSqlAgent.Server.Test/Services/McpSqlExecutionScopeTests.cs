using Common.Models;
using HsSqlAgent.Server.Services;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

public sealed class McpSqlExecutionScopeTests
{
    private static DefaultHttpContext AuthorizedContext()
    {
        var context = new DefaultHttpContext();
        context.Items[McpContextItemKeys.AllowedTools] = "execute_query_sql,execute_dml_sql,orders_tool";
        context.Items[McpContextItemKeys.TableWhitelist] = "public.orders, public.order_items";
        context.Items[McpContextItemKeys.AllowedQueryColumns] = null;
        context.Items[McpContextItemKeys.AccessKeyId] = 7;
        context.Items[McpContextItemKeys.DbManagementId] = 42;
        context.Items[McpContextItemKeys.DatabaseName] = "orders";
        context.Items[McpContextItemKeys.SqlProvider] = "Postgres";
        return context;
    }

    [Theory]
    [InlineData(McpContextItemKeys.AllowedQueryColumns)]
    [InlineData(McpContextItemKeys.AllowedTools)]
    [InlineData(McpContextItemKeys.TableWhitelist)]
    [InlineData(McpContextItemKeys.AccessKeyId)]
    [InlineData(McpContextItemKeys.DbManagementId)]
    [InlineData(McpContextItemKeys.DatabaseName)]
    [InlineData(McpContextItemKeys.SqlProvider)]
    public void MissingTrustedContext_FailsClosed(string missingField)
    {
        var context = AuthorizedContext();
        context.Items.Remove(missingField);
        Assert.Throws<UnauthorizedAccessException>(() =>
            McpSqlExecutionScope.Capture(context, "execute_query_sql", SqlAgentToolType.Postgres));
    }

    [Fact]
    public void PreflightToolAuthorization_IsSharedAndFailsClosed()
    {
        var context = AuthorizedContext();
        McpSqlExecutionScope.EnsureToolAllowed(context, "orders_tool");
        Assert.Throws<UnauthorizedAccessException>(() =>
            McpSqlExecutionScope.EnsureToolAllowed(context, "unpublished_tool"));
        context.Items.Remove(McpContextItemKeys.AllowedTools);
        Assert.Throws<UnauthorizedAccessException>(() =>
            McpSqlExecutionScope.EnsureToolAllowed(context, "orders_tool"));
    }

    [Fact]
    public void WrongToolOrProvider_FailsClosed()
    {
        var context = AuthorizedContext();
        Assert.Throws<UnauthorizedAccessException>(() =>
            McpSqlExecutionScope.Capture(context, "get_secrets", SqlAgentToolType.Postgres));
        Assert.Throws<UnauthorizedAccessException>(() =>
            McpSqlExecutionScope.Capture(context, "execute_query_sql", SqlAgentToolType.MySQL));
    }

    [Fact]
    public void ScopedTables_AreSnapshotCaseInsensitiveAndNotMutable()
    {
        var context = AuthorizedContext();
        var scope = McpSqlExecutionScope.Capture(context, "execute_query_sql", SqlAgentToolType.Postgres);
        Assert.NotNull(scope.AllowedTables);
        Assert.Contains("PUBLIC.ORDERS", scope.AllowedTables);
        context.Items[McpContextItemKeys.TableWhitelist] = "public.secrets";
        Assert.Contains("public.orders", scope.AllowedTables);
        Assert.DoesNotContain("public.secrets", scope.AllowedTables);
        Assert.Equal("public.secrets",
            Assert.Single(scope.RefreshAllowedTables(context)!));
    }

    [Fact]
    public void ExplicitEmptyWhitelist_RetainsUnrestrictedLegacyContract()
    {
        var context = AuthorizedContext();
        context.Items[McpContextItemKeys.TableWhitelist] = string.Empty;
        var scope = McpSqlExecutionScope.Capture(context, "execute_query_sql", SqlAgentToolType.Postgres);
        Assert.Null(scope.AllowedTables);
    }

    [Fact]
    public void PendingDml_CannotChangePrincipalDatabaseOrToolAccess()
    {
        var context = AuthorizedContext();
        var scope = McpSqlExecutionScope.Capture(context, "orders_tool", SqlAgentToolType.Postgres);
        var approval = scope.ToApprovalContext();
        Assert.Equal("mcp-key:7", approval.PrincipalIdentity);
        Assert.Equal("db-management:42", approval.TargetIdentity);
        Assert.Equal("orders", approval.DatabaseIdentity);

        context.Items[McpContextItemKeys.AccessKeyId] = 9;
        Assert.Throws<UnauthorizedAccessException>(() => scope.RefreshAllowedTables(context));
        context.Items[McpContextItemKeys.AccessKeyId] = 7;
        context.Items[McpContextItemKeys.AllowedTools] = "execute_query_sql";
        Assert.Throws<UnauthorizedAccessException>(() => scope.RefreshAllowedTables(context));
        context.Items[McpContextItemKeys.AllowedTools] = "orders_tool";
        context.Items[McpContextItemKeys.DbManagementId] = 99;
        Assert.Throws<UnauthorizedAccessException>(() => scope.RefreshAllowedTables(context));
    }

    [Fact]
    public void RestrictedColumns_AreCapturedAndDmlDenied()
    {
        var context = AuthorizedContext();
        context.Items[McpContextItemKeys.AllowedQueryColumns] = "id,status";
        var scope = McpSqlExecutionScope.Capture(context, "execute_query_sql", SqlAgentToolType.Postgres);
        Assert.Contains("id", scope.AllowedQueryColumns!);
        context.Items[McpContextItemKeys.AllowedQueryColumns] = "secret";
        Assert.DoesNotContain("secret", scope.AllowedQueryColumns!);
        Assert.Throws<UnauthorizedAccessException>(() => scope.EnsureDmlColumnPolicySupported());
        var original = McpSqlExecutionScope.Capture(
            AuthorizedContext(), "execute_dml_sql", SqlAgentToolType.Postgres);
        Assert.Throws<UnauthorizedAccessException>(() => original.RefreshAllowedTables(context));
        context.Items[McpContextItemKeys.AllowedQueryColumns] = string.Empty;
        Assert.Empty(McpSqlExecutionScope.Capture(
            context, "execute_query_sql", SqlAgentToolType.Postgres).AllowedQueryColumns!);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidKeyId_IsRejected(int value)
    {
        var context = AuthorizedContext();
        context.Items[McpContextItemKeys.AccessKeyId] = value;
        Assert.Throws<UnauthorizedAccessException>(() =>
            McpSqlExecutionScope.Capture(context, "execute_query_sql", SqlAgentToolType.Postgres));
    }
}
