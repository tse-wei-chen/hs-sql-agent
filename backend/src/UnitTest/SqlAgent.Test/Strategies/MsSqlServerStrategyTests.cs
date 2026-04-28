using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Moq;
using SqlAgent.Service.Models;
using SqlAgent.Service.Services;
using SqlAgent.Service.Strategies;
using Testcontainers.MsSql;
using Xunit;

namespace SqlAgent.Test.Strategies;

public class MsSqlFixture : IAsyncLifetime
{
    public MsSqlContainer Container { get; }
    public string ConnectionString => Container.GetConnectionString();

    public MsSqlFixture()
    {
        Container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04")
            .WithPassword("StrongPassword123!")
            .WithEnvironment("ACCEPT_EULA", "Y")
            .Build();
    }

    public async ValueTask InitializeAsync()
    {
        await Container.StartAsync();

        var strategy = new MsSqlServerStrategy(new QueryValueParserService(), new Mock<IConfiguration>().Object);
        using var conn = strategy.CreateConnection(ConnectionString);
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE Users (
                Id INT IDENTITY(1,1) PRIMARY KEY,
                Name NVARCHAR(100),
                Age INT,
                Active BIT,
                CreatedDate DATETIME
            );
            INSERT INTO Users (Name, Age, Active, CreatedDate) VALUES 
            ('Alice', 30, 1, '2023-01-01'),
            ('Bob', 25, 1, '2023-02-01');
        ";
        await cmd.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync() => await Container.DisposeAsync();
}


public class MsSqlServerStrategyTests : IClassFixture<MsSqlFixture>
{
    private readonly MsSqlFixture _fixture;
    private readonly MsSqlServerStrategy _strategy;

    public MsSqlServerStrategyTests(MsSqlFixture fixture)
    {
        _fixture = fixture;
        var configMock = new Mock<IConfiguration>();
        configMock.Setup(c => c["McpKeySettings:HmacSecretKey"]).Returns("TestSecretKey12345678901234567890");
        _strategy = new MsSqlServerStrategy(new QueryValueParserService(), configMock.Object);
    }

    [Fact]
    public async Task GetTablesAsync_ShouldReturnTables()
    {
        var tables = await _strategy.GetTablesAsync(_fixture.ConnectionString, "dbo", TestContext.Current.CancellationToken);
        Assert.Contains("Users", tables);
    }

    [Fact]
    public async Task ExecuteQueryAsync_ShouldTrigger208Hint_WhenTableNotFound()
    {
        var res = await _strategy.ExecuteQueryAsync(_fixture.ConnectionString, "NonExistent", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("code=208", res);
        Assert.Contains("Invalid object name", res);
    }

    [Fact]
    public async Task ExecuteQueryAsync_ShouldTrigger207Hint_WhenColumnNotFound()
    {
        var res = await _strategy.ExecuteQueryAsync(_fixture.ConnectionString, "Users",
            selectColumns: [new SelectCondition { Field = "FakeCol" }],
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("code=207", res);
        Assert.Contains("Invalid column name", res);
    }

    [Fact]
    public async Task ExecuteQueryAsync_ShouldTrigger102Hint_WhenSyntaxIsInvalid()
    {
        var res = await _strategy.ExecuteQueryAsync(_fixture.ConnectionString, "Users",
            selectColumns: [new SelectCondition { Arithmetic = new SelectArithmeticCondition { FieldName = "Name", Operator = "!!!", Constant = 1 } }],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("code=102", res);
        Assert.Contains("Incorrect syntax near", res);
    }

    [Fact]
    public void BuildConnectionString_ShouldGenerateValidMsSqlFormat()
    {
        var model = new BuildDbConnectionModel
        {
            Provider = "SqlServer",
            Host = "localhost",
            Port = "1433",
            Database = "mydb",
            Username = "user",
            Password = "pw"
        };
        var connStr = _strategy.BuildConnectionString(model);

        var builder = new SqlConnectionStringBuilder(connStr);
        Assert.Equal("localhost,1433", builder.DataSource);
        Assert.Equal("mydb", builder.InitialCatalog);
        Assert.Equal("user", builder.UserID);
    }

    [Fact]
    public async Task GetSchemasAsync_ShouldReturnAvailableSchemas()
    {
        var schemas = await _strategy.GetSchemasAsync(_fixture.ConnectionString, TestContext.Current.CancellationToken);
        Assert.Contains("dbo", schemas);
    }

    [Fact]
    public async Task GetColumnsAsync_ShouldReturnColumnTypes()
    {
        var columns = await _strategy.GetColumnsAsync(_fixture.ConnectionString, "dbo", "Users", TestContext.Current.CancellationToken);
        Assert.Contains(columns, c => c.Column == "Id" && c.Type.Contains("int", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(columns, c => c.Column == "Active" && c.Type.Contains("bit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExecuteQueryAsync_ShouldReturnValidJson()
    {
        var json = await _strategy.ExecuteQueryAsync(_fixture.ConnectionString, "Users",
            whereConditions: [new WhereCondition { Field = "Age", Operator = ">", Value = 20 }],
            orderByColumns: [new OrderByCondition { Field = "Age", Direction = "asc" }],
            cancellationToken: TestContext.Current.CancellationToken);

        var res = JsonSerializer.Deserialize<List<JsonElement>>(json);
        Assert.NotNull(res);
        Assert.True(res.Count >= 2);
    }

    [Fact]
    public async Task ExecuteQueryAsync_ShouldHandleDistinctAndLimit()
    {
        var json = await _strategy.ExecuteQueryAsync(_fixture.ConnectionString, "Users",
            selectColumns: [new SelectCondition { Field = "Active" }],
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
            TableName = "Users",
            Values = [
                new NameValuePair { Name = "Name", Value = "David" },
                new NameValuePair { Name = "Age", Value = 40 },
                new NameValuePair { Name = "Active", Value = true }
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
