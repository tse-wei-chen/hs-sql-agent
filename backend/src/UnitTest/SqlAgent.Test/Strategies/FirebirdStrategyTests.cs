using System.Text.Json;
using DotNet.Testcontainers.Builders;
using FirebirdSql.Data.FirebirdClient;
using Microsoft.Extensions.Configuration;
using Moq;
using SqlAgent.Service.Models;
using SqlAgent.Service.Services;
using SqlAgent.Service.Strategies;
using Testcontainers.FirebirdSql;
using Xunit;

namespace SqlAgent.Test.Strategies;

public class FirebirdFixture : IAsyncLifetime
{
    public FirebirdSqlContainer Container { get; }
    public string ConnectionString => Container.GetConnectionString();

    public FirebirdFixture()
    {
        Container = new FirebirdSqlBuilder("firebirdsql/firebird:latest")
            .WithPassword("TestPass123!")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Firebird TCP/IP server version"))
            .Build();
    }

    public async ValueTask InitializeAsync()
    {
        await Container.StartAsync();
        FbConnection.CreateDatabase(ConnectionString);
        var strategy = new FirebirdStrategy(new QueryValueParserService(), new Mock<IConfiguration>().Object);
        using var conn = strategy.CreateConnection(ConnectionString);
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        // Firebird table creation
        cmd.CommandText = @"
            CREATE TABLE USERS (
                ID INTEGER PRIMARY KEY,
                NAME VARCHAR(100),
                AGE INTEGER
            )
        ";
        await cmd.ExecuteNonQueryAsync();

        cmd.CommandText = "INSERT INTO USERS (ID, NAME, AGE) VALUES (1, 'Alice', 30)";
        await cmd.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await Container.DisposeAsync();
    }
}


public class FirebirdStrategyTests : IClassFixture<FirebirdFixture>
{
    private readonly FirebirdFixture _fixture;
    private readonly FirebirdStrategy _strategy;

    public FirebirdStrategyTests(FirebirdFixture fixture)
    {
        _fixture = fixture;
        var configMock = new Mock<IConfiguration>();
        configMock.Setup(c => c["McpKeySettings:HmacSecretKey"]).Returns("TestSecretKey12345678901234567890");
        _strategy = new FirebirdStrategy(new QueryValueParserService(), configMock.Object);
    }

    [Fact]
    public async Task GetTablesAsync_ShouldReturnTables()
    {
        var tables = await _strategy.GetTablesAsync(_fixture.ConnectionString, "Default", TestContext.Current.CancellationToken);
        Assert.Contains("USERS", tables);
    }

    [Fact]
    public async Task ExecuteQueryAsync_ShouldTriggerTableUnknownHint_WhenTableNotFound()
    {
        var res = await _strategy.ExecuteQueryAsync(_fixture.ConnectionString, "NON_EXISTENT", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("Table does not exist", res);
    }

    [Fact]
    public async Task ExecuteQueryAsync_ShouldTriggerColumnUnknownHint_WhenColumnNotFound()
    {
        var res = await _strategy.ExecuteQueryAsync(_fixture.ConnectionString, "USERS",
            selectColumns: [new SelectCondition { Field = "FAKE_COL" }],
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("Invalid column name", res);
    }

    [Fact]
    public void BuildConnectionString_ShouldGenerateValidFirebirdFormat()
    {
        var model = new BuildDbConnectionModel
        {
            Provider = "Firebird",
            Host = "localhost",
            Port = "3050",
            Database = "/path/to/db.fdb",
            Username = "sysdba",
            Password = "pw"
        };
        var connStr = _strategy.BuildConnectionString(model);

        Assert.Contains("data source=localhost", connStr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("port number=3050", connStr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("user id=sysdba", connStr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetColumnsAsync_ShouldReturnColumnTypes()
    {
        var columns = await _strategy.GetColumnsAsync(_fixture.ConnectionString, "Default", "USERS", TestContext.Current.CancellationToken);
        Assert.Contains(columns, c => c.Column == "ID" && c.Type.Contains("INTEGER", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(columns, c => c.Column == "NAME" && c.Type.Contains("VARCHAR", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExecuteQueryAsync_ShouldReturnValidJson()
    {
        var json = await _strategy.ExecuteQueryAsync(_fixture.ConnectionString, "USERS",
            whereConditions: [new WhereCondition { Field = "AGE", Operator = ">", Value = 20 }],
            orderByColumns: [new OrderByCondition { Field = "AGE", Direction = "asc" }],
            cancellationToken: TestContext.Current.CancellationToken);

        var res = JsonSerializer.Deserialize<List<JsonElement>>(json);
        Assert.NotNull(res);
        Assert.True(res.Count >= 1);
    }

    [Fact]
    public async Task ExecuteQueryAsync_ShouldHandleDistinctAndLimit()
    {
        var json = await _strategy.ExecuteQueryAsync(_fixture.ConnectionString, "USERS",
            selectColumns: [new SelectCondition { Field = "AGE" }],
            distinct: true,
            limit: 1,
            cancellationToken: TestContext.Current.CancellationToken);

        var res = JsonSerializer.Deserialize<List<JsonElement>>(json);
        Assert.NotNull(res);
        Assert.Single(res);
    }

    [Fact]
    public async Task ExecuteDmlAsync_ShouldPerformValidInsert()
    {
        var dml = new DmlDefinition
        {
            Operation = "insert",
            TableName = "USERS",
            Values = [
                new NameValuePair { Name = "ID", Value = 2 },
                new NameValuePair { Name = "NAME", Value = "David" },
                new NameValuePair { Name = "AGE", Value = 40 }
            ]
        };

        var dryRun = await _strategy.ExecuteDmlAsync(_fixture.ConnectionString, dml, TestContext.Current.CancellationToken);
        var start = dryRun.IndexOf("TokenRequired=") + 14;
        var end = dryRun.IndexOf(" |", start);
        dml.ConfirmToken = dryRun[start..end];

        var final = await _strategy.ExecuteDmlAsync(_fixture.ConnectionString, dml, TestContext.Current.CancellationToken);
        Assert.Contains("Success", final);
    }
}
