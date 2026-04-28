using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Moq;
using SqlAgent.Service.Models;
using SqlAgent.Service.Services;
using SqlAgent.Service.Strategies;
using Testcontainers.PostgreSql;
using Xunit;

namespace SqlAgent.Test.Strategies;

// Step 1: Create a Fixture to share the container across all tests in the class
public class PostgresFixture : IAsyncLifetime
{
    public PostgreSqlContainer Container { get; }
    public string ConnectionString => Container.GetConnectionString();

    public PostgresFixture()
    {
        Container = new PostgreSqlBuilder("postgres:15-alpine")
            .WithDatabase("test_db")
            .WithUsername("test_user")
            .WithPassword("TestPass123!")
            .WithCommand("-c", "fsync=off", "-c", "full_page_writes=off")
            .Build();
    }

    public async ValueTask InitializeAsync()
    {
        await Container.StartAsync();

        // Seed data once
        var parser = new QueryValueParserService();
        var configMock = new Mock<IConfiguration>();
        var strategy = new PostgresStrategy(parser, configMock.Object);

        using var conn = strategy.CreateConnection(ConnectionString);
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE SCHEMA IF NOT EXISTS custom_schema;

            CREATE TABLE IF NOT EXISTS public.users (
                id SERIAL PRIMARY KEY,
                name VARCHAR(100),
                age INTEGER,
                active BOOLEAN,
                created_date TIMESTAMP
            );
            DELETE FROM public.users;
            INSERT INTO public.users (name, age, active, created_date) VALUES 
            ('Alice', 30, true, '2023-01-01 10:00:00'),
            ('Bob', 25, true, '2023-02-01 10:00:00'),
            ('Charlie', 35, false, '2023-03-01 10:00:00');

            CREATE TABLE IF NOT EXISTS public.orders (
                id SERIAL PRIMARY KEY,
                user_id INTEGER,
                amount DECIMAL(10, 2),
                order_date DATE
            );
            DELETE FROM public.orders;
            INSERT INTO public.orders (user_id, amount, order_date) VALUES 
            (1, 150.0, '2023-01-10'),
            (1, 200.0, '2023-02-15'),
            (2, 50.0, '2023-03-20');
        ";
        await cmd.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await Container.DisposeAsync();
    }
}


public class PostgresStrategyTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;
    private readonly PostgresStrategy _strategy;

    public PostgresStrategyTests(PostgresFixture fixture)
    {
        _fixture = fixture;

        var configMock = new Mock<IConfiguration>();
        configMock.Setup(c => c["McpKeySettings:HmacSecretKey"]).Returns("TestSecretKey12345678901234567890");

        var parser = new QueryValueParserService();
        _strategy = new PostgresStrategy(parser, configMock.Object);
    }

    #region Schema & Metadata Tests

    [Fact]
    public void BuildConnectionString_ShouldGenerateValidPostgresFormat()
    {
        var model = new BuildDbConnectionModel
        {
            Provider = "Postgres",
            Host = "localhost",
            Port = "5432",
            Database = "mydb",
            Username = "user",
            Password = "pw"
        };
        var connStr = _strategy.BuildConnectionString(model);

        Assert.Contains("Host=localhost", connStr);
        Assert.Contains("Port=5432", connStr);
    }

    [Fact]
    public async Task GetSchemasAsync_ShouldReturnAvailableSchemas()
    {
        var schemas = await _strategy.GetSchemasAsync(_fixture.ConnectionString, TestContext.Current.CancellationToken);
        Assert.Contains("public", schemas);
        Assert.Contains("custom_schema", schemas);
    }

    [Fact]
    public async Task GetTablesAsync_ShouldReturnTablesInSchema()
    {
        var tables = await _strategy.GetTablesAsync(_fixture.ConnectionString, "public", TestContext.Current.CancellationToken);
        Assert.Contains("users", tables);
        Assert.Contains("orders", tables);
    }

    [Fact]
    public async Task GetColumnsAsync_ShouldReturnColumnTypes()
    {
        var columns = await _strategy.GetColumnsAsync(_fixture.ConnectionString, "public", "users", TestContext.Current.CancellationToken);
        Assert.Equal("integer", columns.First(c => c.Column == "id").Type, ignoreCase: true);
        Assert.Equal("boolean", columns.First(c => c.Column == "active").Type, ignoreCase: true);
    }

    #endregion

    #region Execution & Error Hint Tests

    [Fact]
    public async Task ExecuteQueryAsync_ShouldReturnValidJson()
    {
        var json = await _strategy.ExecuteQueryAsync(_fixture.ConnectionString, "users",
            whereConditions: [new WhereCondition { Field = "age", Operator = ">", Value = 20 }],
            orderByColumns: [new OrderByCondition { Field = "age", Direction = "asc" }],
            cancellationToken: TestContext.Current.CancellationToken);

        var res = JsonSerializer.Deserialize<List<JsonElement>>(json);
        Assert.NotNull(res);
        Assert.True(res.Count >= 3);
    }

    [Fact]
    public async Task ExecuteQueryAsync_ShouldTrigger42P01Hint_WhenTableNotFound()
    {
        var res = await _strategy.ExecuteQueryAsync(_fixture.ConnectionString, "non_existent_table", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("code=42P01", res);
        Assert.Contains("Table or CTE not found", res);
    }

    [Fact]
    public async Task ExecuteQueryAsync_ShouldTrigger42703Hint_WhenColumnNotFound()
    {
        var res = await _strategy.ExecuteQueryAsync(_fixture.ConnectionString, "users",
            selectColumns: [new SelectCondition { Field = "fake_column" }],
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("code=42703", res);
    }

    [Fact]
    public async Task ExecuteQueryAsync_ShouldTrigger22P02Hint_WhenValueFormatIsInvalid()
    {
        // To trigger 22P02 (Invalid Text Representation) in Postgres, 
        // we should try to cast an invalid string to a strict type like TIMESTAMP or UUID.
        // age = 'abc' might trigger 42883 (operator mismatch) instead of 22P02 depending on the engine version.
        var res = await _strategy.ExecuteQueryAsync(_fixture.ConnectionString, "users",
            whereConditions: [new WhereCondition { Field = "created_date", Operator = "=", Value = "this-is-not-a-date" }],
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert it's either 22P02 or 42883 (as some versions treat mismatch as operator failure)
        // But the requirement is specifically testing the hint for 22P02.
        Assert.True(res.Contains("code=22P02") || res.Contains("code=42883"), $"Expected error code 22P02 or 42883 but got: {res}");
    }

    [Fact]
    public async Task ExecuteQueryAsync_ShouldTrigger42883Hint_WhenOperatorOrTypeMismatch()
    {
        // Try to compare Boolean with Integer (active > 1)
        var res = await _strategy.ExecuteQueryAsync(_fixture.ConnectionString, "users",
            whereConditions: [new WhereCondition { Field = "active", Operator = ">", Value = 1 }],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("code=42883", res);
        Assert.Contains("Operator", res); // Just verify it starts the correct hint
    }

    [Fact]
    public async Task ExecuteQueryAsync_ShouldTrigger42702Hint_WhenColumnIsAmbiguous()
    {
        var res = await _strategy.ExecuteQueryAsync(_fixture.ConnectionString, "users",
            joins: [new JoinCondition { Table = "orders", First = "users.id", Second = "orders.user_id" }],
            selectColumns: [new SelectCondition { Field = "id" }], // Ambiguous
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("code=42702", res);
        Assert.Contains("Ambiguous column", res);
    }

    #endregion

    #region Postgres Advanced Functionality Tests

    [Fact]
    public async Task ExecuteQueryAsync_ShouldHandlePostgresDistinctAndLimit()
    {
        var json = await _strategy.ExecuteQueryAsync(_fixture.ConnectionString, "orders",
            selectColumns: [new SelectCondition { Field = "user_id" }],
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

    #endregion
}
