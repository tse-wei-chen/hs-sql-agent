using Admin.Service.Interfaces;
using Admin.Service.Models;
using Common.Models;
using HsSqlAgent.Provider.Abstractions;
using HsSqlAgent.Server.Services;
using HsSqlAgent.Server.Tools;
using HsSqlAgent.SqlCore;
using Microsoft.AspNetCore.Http;
using Moq;
using Xunit;

namespace HsSqlAgent.Server.Test.Tools;

public class SqlAgentToolMetadataTests
{
    [Fact]
    public async Task GetSchemas_ReturnsStructuredProviderAndSchemaList()
    {
        var (tool, metadata, _) = CreateTool(tableWhitelist: string.Empty);
        metadata
            .Setup(x => x.GetSchemasAsync("Host=localhost;Database=testdb", It.IsAny<CancellationToken>()))
            .ReturnsAsync(["public", "sales"]);

        var result = await tool.GetSchemas(TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal("Postgres", result.Provider);
        Assert.Equal(["public", "sales"], result.Schemas);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task GetTables_AppliesKeyWhitelistBeforeReturningStructuredItems()
    {
        var (tool, metadata, _) = CreateTool(tableWhitelist: "public.users");
        metadata
            .Setup(x => x.GetTablesAsync(
                "Host=localhost;Database=testdb",
                "public",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(["users", "secrets"]);

        var result = await tool.GetTables("public", TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        var table = Assert.Single(result.Tables);
        Assert.Equal("users", table.Name);
        Assert.Empty(table.Metrics);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task GetColumns_ReturnsPrimaryKeyMetadataAsStructuredFields()
    {
        var (tool, metadata, _) = CreateTool(tableWhitelist: "public.users");
        metadata
            .Setup(x => x.GetColumnsAsync(
                "Host=localhost;Database=testdb",
                "public",
                "users",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new DatabaseColumnMetadata("public", "users", "id", "integer", true, 1),
                new DatabaseColumnMetadata("public", "users", "email", "text", false)
            ]);

        var result = await tool.GetColumns("public", "users", TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(2, result.Columns.Count);
        var id = Assert.Single(result.Columns, column => column.Name == "id");
        Assert.True(id.IsPrimaryKey);
        Assert.Equal(1, id.PrimaryKeyOrdinal);
        Assert.Equal("integer", id.Type);
        Assert.Empty(id.Relationships);
        Assert.Null(result.Error);
    }

    private static (SqlAgentTool Tool, Mock<IProviderMetadataReader> Metadata, Mock<IAuditService> Audit) CreateTool(
        string tableWhitelist)
    {
        var httpContextAccessor = new Mock<IHttpContextAccessor>();
        var providerFactory = new Mock<ISqlProviderFactory>();
        var auditService = new Mock<IAuditService>();
        var semanticService = new Mock<IDbSemanticService>();
        var securityPolicyState = new Mock<ISecurityPolicyRuntimeState>();
        var concurrencyLimiter = new Mock<ISqlExecutionConcurrencyLimiter>();
        var typedQueryRuntime = new Mock<ITypedQueryRuntime>();
        var provider = new Mock<ISqlProvider>();
        var metadata = new Mock<IProviderMetadataReader>();

        var context = new DefaultHttpContext();
        context.Items[McpContextItemKeys.SqlProvider] = "Postgres";
        context.Items[McpContextItemKeys.SqlConnectionString] = "Host=localhost;Database=testdb";
        context.Items[McpContextItemKeys.AllowedTools] = string.Empty;
        context.Items[McpContextItemKeys.TableWhitelist] = tableWhitelist;
        httpContextAccessor.Setup(x => x.HttpContext).Returns(context);

        provider.SetupGet(x => x.Type).Returns(SqlAgentToolType.Postgres);
        provider.SetupGet(x => x.Metadata).Returns(metadata.Object);
        providerFactory.Setup(x => x.GetProvider(SqlAgentToolType.Postgres)).Returns(provider.Object);
        concurrencyLimiter
            .Setup(x => x.TryAcquireAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IAsyncDisposable>());

        return (
            new SqlAgentTool(
                httpContextAccessor.Object,
                providerFactory.Object,
                auditService.Object,
                semanticService.Object,
                securityPolicyState.Object,
                concurrencyLimiter.Object,
                typedQueryRuntime.Object),
            metadata,
            auditService);
    }
}
