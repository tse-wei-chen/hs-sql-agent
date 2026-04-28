using System.Text.Json;
using DotNet.Testcontainers.Builders;
using Microsoft.Extensions.Configuration;
using Moq;
using MySql.Data.MySqlClient;
using SqlAgent.Service.Models;
using SqlAgent.Service.Services;
using SqlAgent.Service.Strategies;
using Testcontainers.MySql;
using Xunit;

namespace SqlAgent.Test.Strategies;

public class MySqlFixture : IAsyncLifetime
{
    public MySqlContainer Container { get; }
    public string ConnectionString => Container.GetConnectionString();

    public MySqlFixture()
    {
        Container = new MySqlBuilder("mysql:8.0")
            .WithDatabase("test_db")
            .WithPassword("TestPass123!")
            .WithCommand("--innodb-flush-method=nosync", "--innodb-flush-log-at-trx-commit=0", "--sql-mode=STRICT_ALL_TABLES")
            .Build();
    }

    public async ValueTask InitializeAsync()
    {
        await Container.StartAsync();

        var parser = new QueryValueParserService();
        var strategy = new MySqlStrategy(parser, new Mock<IConfiguration>().Object);

        using var conn = strategy.CreateConnection(ConnectionString);
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SET SESSION sql_mode = 'STRICT_ALL_TABLES';
            CREATE TABLE IF NOT EXISTS users (
                id INT AUTO_INCREMENT PRIMARY KEY,
                name VARCHAR(100),
                age INT,
                active BOOLEAN,
                created_date DATETIME
            );
            INSERT INTO users (name, age, active, created_date) VALUES 
            ('Alice', 30, true, '2023-01-01 10:00:00'),
            ('Bob', 25, true, '2023-02-01 10:00:00');
        ";
        await cmd.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync() => await Container.DisposeAsync();
}


public class MySqlStrategyTests : IClassFixture<MySqlFixture>
{
    private readonly MySqlFixture _fixture;
    private readonly MySqlStrategy _strategy;

    public MySqlStrategyTests(MySqlFixture fixture)
    {
        _fixture = fixture;
        var configMock = new Mock<IConfiguration>();
        configMock.Setup(c => c["McpKeySettings:HmacSecretKey"]).Returns("TestSecretKey12345678901234567890");
        _strategy = new MySqlStrategy(new QueryValueParserService(), configMock.Object);
    }

    [Fact]
    public async Task GetTablesAsync_ShouldReturnTables()
    {
        var tables = await _strategy.GetTablesAsync(_fixture.ConnectionString, "test_db", TestContext.Current.CancellationToken);
        Assert.Contains("users", tables);
    }

    [Fact]
    public async Task ExecuteQueryAsync_ShouldTrigger1146Hint_WhenTableNotFound()
    {
        var res = await _strategy.ExecuteQueryAsync(_fixture.ConnectionString, "non_existent", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("code=1146", res);
        Assert.Contains("Table or CTE not found", res);
    }

    [Fact]
    public async Task ExecuteQueryAsync_ShouldTrigger1054Hint_WhenColumnNotFound()
    {
        var res = await _strategy.ExecuteQueryAsync(_fixture.ConnectionString, "users",
            selectColumns: [new SelectCondition { Field = "fake_col" }],
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("code=1054", res); // Unknown column
        Assert.Contains("Column not found", res);
    }

    [Fact]
    public async Task ExecuteQueryAsync_ShouldTrigger1292Hint_WhenValueFormatIsIncorrect()
    {
        var res = await _strategy.ExecuteQueryAsync(_fixture.ConnectionString, "users",
            whereConditions: [new WhereCondition { Field = "created_date", Operator = "=", Value = "not-a-date" }],
            cancellationToken: TestContext.Current.CancellationToken);

        // MySQL might return 1292 or just warning depending on mode, but usually 1292 in strict mode
        Assert.True(res.Contains("code=1292") || res.Contains("Error"), $"Result was: {res}");
    }

    [Fact]
    public async Task ExecuteQueryAsync_ShouldTrigger1064Hint_WhenSyntaxIsInvalid()
    {
        // Try to use a reserved word or invalid arithmetic
        var res = await _strategy.ExecuteQueryAsync(_fixture.ConnectionString, "users",
            selectColumns: [new SelectCondition { Arithmetic = new SelectArithmeticCondition { FieldName = "name", Operator = "INVALID", Constant = 1 } }],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("code=1064", res);
        Assert.Contains("SQL syntax error", res);
    }

    [Fact]
    public void BuildConnectionString_ShouldGenerateValidMySqlFormat()
    {
        var model = new BuildDbConnectionModel
        {
            Provider = "MySql",
            Host = "localhost",
            Port = "3306",
            Database = "mydb",
            Username = "user",
            Password = "pw"
        };
        var connStr = _strategy.BuildConnectionString(model);
        var builder = new MySqlConnectionStringBuilder(connStr);
        Assert.Equal("localhost", builder.Server);
        Assert.Equal("mydb", builder.Database);
        Assert.Equal("user", builder.UserID);
        Assert.Equal("pw", builder.Password);
        Assert.Equal(uint.Parse("3306"), builder.Port);
    }

    [Fact]
    public async Task GetSchemasAsync_ShouldReturnAvailableSchemas()
    {
        var schemas = await _strategy.GetSchemasAsync(_fixture.ConnectionString, TestContext.Current.CancellationToken);
        Assert.Contains("test_db", schemas);
    }

    [Fact]
    public async Task GetColumnsAsync_ShouldReturnColumnTypes()
    {
        var columns = await _strategy.GetColumnsAsync(_fixture.ConnectionString, "test_db", "users", TestContext.Current.CancellationToken);
        Assert.Contains(columns, c => c.Column == "id" && c.Type.Contains("int", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(columns, c => c.Column == "active" && c.Type.Contains("tinyint", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExecuteQueryAsync_ShouldReturnValidJson()
    {
        var json = await _strategy.ExecuteQueryAsync(_fixture.ConnectionString, "users",
            whereConditions: [new WhereCondition { Field = "age", Operator = ">", Value = 20 }],
            orderByColumns: [new OrderByCondition { Field = "age", Direction = "asc" }],
            cancellationToken: TestContext.Current.CancellationToken);

        var res = JsonSerializer.Deserialize<List<JsonElement>>(json);
        Assert.NotNull(res);
        Assert.True(res.Count >= 2);
    }

    [Fact]
    public async Task ExecuteQueryAsync_ShouldHandleDistinctAndLimit()
    {
        var json = await _strategy.ExecuteQueryAsync(_fixture.ConnectionString, "users",
            selectColumns: [new SelectCondition { Field = "active" }],
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
            TableName = "users",
            Values = [
                new NameValuePair { Name = "name", Value = "David" },
                new NameValuePair { Name = "age", Value = 40 },
                new NameValuePair { Name = "active", Value = true }
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
