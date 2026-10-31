using Common.Models;
using HsSqlAgent.Server.Services;
using HsSqlAgent.Server.Tools;
using HsSqlAgent.SqlCore.Enums;
using Moq;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

/// <summary>
/// RED G3: PostgreSQL DDL has a dedicated, default-disabled Host boundary
/// and must not inherit DML tool/approval permissions.
/// </summary>
public sealed class PostgresDdlRuntimeTests
{
    [Fact]
    public void DdlIsDisabledByDefault_AndAllOperationsRequireSeparateOptIn()
    {
        var options = new PostgresDdlExecutionOptions();
        Assert.False(options.Enabled);
        Assert.Empty(options.AllowedSchemas);
        Assert.False(options.AllowCreateTable);
        Assert.False(options.AllowAddColumn);
        Assert.False(options.AllowCreateIndex);
    }

    [Fact]
    public async Task DefaultRuntime_CannotOpenConnectionOrRequestApproval()
    {
        var provider = new Mock<ISqlProvider>(MockBehavior.Strict);
        var result = await new PostgresDdlRuntime().RunAsync(
            provider.Object, "Host=unused", "CREATE TABLE app.widgets (id INTEGER)",
            new DdlActorContext("mcp-key:1", "db-management:2", "appdb"),
            previewOnly: false, approvalProvider: null,
            TestContext.Current.CancellationToken);
        Assert.Equal(DdlRuntimeStatus.Rejected, result.Status);
        Assert.Null(result.Preview);
        provider.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("CREATE TABLE widgets (id INTEGER)")]
    [InlineData("DROP TABLE app.widgets")]
    [InlineData("CREATE TABLE app.widgets (id INTEGER); DELETE FROM app.widgets")]
    [InlineData("CREATE TABLE app.widgets (id INTEGER PRIMARY KEY)")]
    public async Task UnsafeSqlRejectedBeforeDbOpen(string sql)
    {
        var options = new PostgresDdlExecutionOptions
        {
            Enabled = true,
            AllowedSchemas = new HashSet<string>(StringComparer.Ordinal) { "app" },
            AllowCreateTable = true
        };
        var provider = new Mock<ISqlProvider>(MockBehavior.Strict);
        provider.SetupGet(x => x.Type).Returns(SqlAgentToolType.Postgres);
        var result = await new PostgresDdlRuntime(options).RunAsync(
            provider.Object, "Host=unused", sql,
            new DdlActorContext("mcp-key:1", "db-management:2", "appdb"),
            previewOnly: false, approvalProvider: null,
            TestContext.Current.CancellationToken);
        Assert.Equal(DdlRuntimeStatus.Rejected, result.Status);
        provider.VerifyGet(x => x.Type, Times.AtLeastOnce);
        provider.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DdlRequiresAnIndependentApprovalProviderBeforeExecution()
    {
        var options = new PostgresDdlExecutionOptions
        {
            Enabled = true,
            AllowedSchemas = new HashSet<string>(StringComparer.Ordinal) { "app" },
            AllowCreateTable = true
        };
        var provider = new Mock<ISqlProvider>(MockBehavior.Strict);
        provider.SetupGet(x => x.Type).Returns(SqlAgentToolType.Postgres);
        var result = await new PostgresDdlRuntime(options).RunAsync(
            provider.Object, "Host=unused",
            "CREATE TABLE app.widgets (id INTEGER)", 
            new DdlActorContext("mcp-key:1", "db-management:2", "appdb"),
            previewOnly: false, approvalProvider: null,
            TestContext.Current.CancellationToken);
        Assert.Equal(DdlRuntimeStatus.Rejected, result.Status);
        provider.VerifyGet(x => x.Type, Times.Once);
        provider.VerifyNoOtherCalls();
    }

    [Fact]
    public void ToolCatalog_DdlNeverSelectedAndExistingFourDefaultsUnchanged()
    {
        Assert.Equal(4, McpBuiltInTools.DefaultNames.Count);
        Assert.Contains(McpBuiltInTools.ExecuteDdlSql, McpBuiltInTools.Names.AsEnumerable());
        Assert.DoesNotContain(McpBuiltInTools.ExecuteDdlSql, McpBuiltInTools.DefaultNames.AsEnumerable());
        var ddl = Assert.Single(McpBuiltInTools.Catalog,
            item => item.Name == McpBuiltInTools.ExecuteDdlSql);
        Assert.False(ddl.DefaultSelected);
        Assert.Equal("critical", ddl.Risk);
    }
}
