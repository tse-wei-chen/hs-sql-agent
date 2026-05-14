using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Moq;
using SqlAgent.Service.Interfaces;
using SqlAgent.Service.Models;
using SqlAgent.Service.Services;
using SqlAgent.Service.Strategies;
using Xunit;

namespace SqlAgent.Test.Strategies;

public interface IDbFixture : IAsyncLifetime
{
    string ConnectionString { get; }
}

public abstract class BaseStrategyTests<TStrategy, TFixture> : IClassFixture<TFixture>
    where TStrategy : ISqlStrategy
    where TFixture : class, IDbFixture
{
    protected readonly TFixture Fixture;
    protected readonly TStrategy Strategy;

    protected BaseStrategyTests(TFixture fixture)
    {
        Fixture = fixture;
        var configMock = new Mock<IConfiguration>();
        configMock.Setup(c => c["McpKeySettings:HmacSecretKey"]).Returns("TestSecretKey12345678901234567890");
        Strategy = CreateStrategy(new QueryValueParserService(), configMock.Object);
    }

    protected abstract TStrategy CreateStrategy(IQueryValueParserService parser, IConfiguration configuration);

    protected abstract string TestTableName { get; }
    protected abstract string TestSchemaName { get; }

    [Fact]
    public virtual async Task GetTablesAsync_ShouldReturnTables()
    {
        var tables = await Strategy.GetTablesAsync(Fixture.ConnectionString, TestSchemaName, TestContext.Current.CancellationToken);
        Assert.Contains(TestTableName, tables, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public virtual async Task GetSchemasAsync_ShouldReturnAvailableSchemas()
    {
        var schemas = await Strategy.GetSchemasAsync(Fixture.ConnectionString, TestContext.Current.CancellationToken);
        Assert.Contains(TestSchemaName, schemas, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public virtual async Task GetColumnsAsync_ShouldReturnColumnTypes()
    {
        var columns = await Strategy.GetColumnsAsync(Fixture.ConnectionString, TestSchemaName, TestTableName, TestContext.Current.CancellationToken);
        Assert.NotEmpty(columns);
    }

    [Fact]
    public virtual async Task ExecuteQueryAsync_ShouldReturnValidJson()
    {
        var json = await Strategy.ExecuteQueryAsync(Fixture.ConnectionString, TestTableName,
            limit: 1,
            cancellationToken: TestContext.Current.CancellationToken);

        var res = JsonSerializer.Deserialize<List<JsonElement>>(json);
        Assert.NotNull(res);
        Assert.NotEmpty(res);
    }

    [Fact]
    public virtual async Task ExecuteQueryAsync_ShouldTriggerHint_WhenTableNotFound()
    {
        var ex = await Assert.ThrowsAsync<Exception>(() => Strategy.ExecuteQueryAsync(Fixture.ConnectionString, "NON_EXISTENT_TABLE_HS", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains($"code={TableNotFoundErrorCode}", ex.Message);
    }

    [Fact]
    public virtual async Task ExecuteQueryAsync_ShouldTriggerHint_WhenColumnNotFound()
    {
        var ex = await Assert.ThrowsAsync<Exception>(() => Strategy.ExecuteQueryAsync(Fixture.ConnectionString, TestTableName,
            selectColumns: [new SelectCondition { Field = $"{TestTableName}.NON_EXISTENT_COL_HS" }],
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains($"code={ColumnNotFoundErrorCode}", ex.Message);
    }

    [Fact]
    public virtual async Task ExecuteDmlAsync_ShouldPerformValidInsert()
    {
        var dml = CreateInsertDml();
        var dryRun = await Strategy.ExecuteDmlAsync(Fixture.ConnectionString, dml, TestContext.Current.CancellationToken);

        var tokenStart = dryRun.IndexOf("TokenRequired=");
        if (tokenStart == -1)
        {
            Assert.Contains("Success", dryRun); // Maybe it didn't require a token?
            return;
        }

        var start = tokenStart + 14;
        var end = dryRun.IndexOf(" |", start);
        dml.ConfirmToken = dryRun[start..end];

        var final = await Strategy.ExecuteDmlAsync(Fixture.ConnectionString, dml, TestContext.Current.CancellationToken);
        Assert.Contains("Success", final);
    }

    protected abstract string TableNotFoundErrorCode { get; }
    protected abstract string ColumnNotFoundErrorCode { get; }
    protected abstract DmlDefinition CreateInsertDml();
}
