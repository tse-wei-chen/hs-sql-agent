using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Moq;
using SqlAgent.Service.Interfaces;
using SqlAgent.Service.Models;
using SqlAgent.Service.Services;
using SqlAgent.Service.Strategies;
using Testcontainers.PostgreSql;
using Xunit;

namespace SqlAgent.Test.Strategies;

public class PostgresFixture : IDbFixture
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

        var parser = new QueryValueParserService();
        var strategy = new PostgresStrategy(parser, new Mock<IConfiguration>().Object);

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
            INSERT INTO public.orders (user_id, amount, order_date) VALUES 
            (1, 150.0, '2023-01-10'),
            (1, 200.0, '2023-02-15'),
            (2, 50.0, '2023-03-20');
        ";
        await cmd.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync() => await Container.DisposeAsync();
}


public class PostgresStrategyTests(PostgresFixture fixture) : BaseStrategyTests<PostgresStrategy, PostgresFixture>(fixture)
{
    protected override PostgresStrategy CreateStrategy(IQueryValueParserService parser, IConfiguration configuration) 
        => new PostgresStrategy(parser, configuration);

    protected override string TestTableName => "users";
    protected override string TestSchemaName => "public";

    protected override string TableNotFoundErrorCode => "42P01";
    protected override string ColumnNotFoundErrorCode => "42703";

    [Fact]
    public override async Task GetSchemasAsync_ShouldReturnAvailableSchemas()
    {
        var schemas = await Strategy.GetSchemasAsync(Fixture.ConnectionString, TestContext.Current.CancellationToken);
        Assert.Contains("public", schemas);
        Assert.Contains("custom_schema", schemas);
    }

    [Fact]
    public override async Task GetColumnsAsync_ShouldReturnColumnTypes()
    {
        await base.GetColumnsAsync_ShouldReturnColumnTypes();
        var columns = await Strategy.GetColumnsAsync(Fixture.ConnectionString, TestSchemaName, TestTableName, TestContext.Current.CancellationToken);
        Assert.Equal("integer", columns.First(c => c.Column == "id").Type, ignoreCase: true);
        Assert.Equal("boolean", columns.First(c => c.Column == "active").Type, ignoreCase: true);
    }

    protected override DmlDefinition CreateInsertDml() => new DmlDefinition
    {
        Operation = "insert",
        TableName = TestTableName,
        Values = [
            new NameValuePair { Name = "name", Value = "David" },
            new NameValuePair { Name = "age", Value = 40 },
            new NameValuePair { Name = "active", Value = true }
        ]
    };

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
        var connStr = Strategy.BuildConnectionString(model);

        Assert.Contains("Host=localhost", connStr);
        Assert.Contains("Port=5432", connStr);
    }

    [Fact]
    public async Task ExecuteQueryAsync_ShouldTrigger22P02Hint_WhenValueFormatIsInvalid()
    {
        var res = await Strategy.ExecuteQueryAsync(Fixture.ConnectionString, TestTableName,
            whereConditions: [new WhereCondition { Field = "created_date", Operator = "=", Value = "this-is-not-a-date" }],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(res.Contains("code=22P02") || res.Contains("code=42883"), $"Result was: {res}");
    }

    [Fact]
    public async Task ExecuteQueryAsync_ShouldTrigger42883Hint_WhenOperatorOrTypeMismatch()
    {
        var res = await Strategy.ExecuteQueryAsync(Fixture.ConnectionString, TestTableName,
            whereConditions: [new WhereCondition { Field = "active", Operator = ">", Value = 1 }],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("code=42883", res);
        Assert.Contains("Operator", res);
    }

    [Fact]
    public async Task ExecuteQueryAsync_ShouldTrigger42702Hint_WhenColumnIsAmbiguous()
    {
        var res = await Strategy.ExecuteQueryAsync(Fixture.ConnectionString, TestTableName,
            joins: [new JoinCondition { Table = "orders", First = "users.id", Second = "orders.user_id" }],
            selectColumns: [new SelectCondition { Field = "id" }],
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("code=42702", res);
        Assert.Contains("Ambiguous column", res);
    }

    [Fact]
    public async Task ExecuteQueryAsync_ShouldSupportParameterizedArithmeticConstants()
    {
        using var constantJson = JsonDocument.Parse("1");

        var json = await Strategy.ExecuteQueryAsync(Fixture.ConnectionString, TestTableName,
            selectColumns:
            [
                new SelectCondition
                {
                    Arithmetic = new SelectArithmeticCondition
                    {
                        Left = new SelectArithmeticCondition { Constant = constantJson.RootElement.Clone() },
                        Operator = "-",
                        Right = new SelectArithmeticCondition { FieldName = "age" }
                    },
                    Alias = "delta"
                }
            ],
            whereConditions: [new WhereCondition { Field = "name", Operator = "=", Value = "Alice" }],
            cancellationToken: TestContext.Current.CancellationToken);

        var rows = JsonSerializer.Deserialize<List<JsonElement>>(json);

        Assert.NotNull(rows);
        Assert.Single(rows);
        Assert.Equal(-29, rows[0].GetProperty("delta").GetInt32());
    }
}
