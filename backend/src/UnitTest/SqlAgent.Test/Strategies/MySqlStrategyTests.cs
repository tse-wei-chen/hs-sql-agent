using System.Text.Json;
using DotNet.Testcontainers.Builders;
using Microsoft.Extensions.Configuration;
using Moq;
using MySql.Data.MySqlClient;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Interfaces;
using SqlAgent.Service.Models;
using SqlAgent.Service.Services;
using SqlAgent.Service.Strategies;
using Testcontainers.MySql;
using Xunit;

namespace SqlAgent.Test.Strategies;

public class MySqlFixture : IDbFixture
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
            ('Bob', 25, true, '2023-02-01 10:00:00'),
            ('Charlie', 35, false, '2023-03-01 10:00:00');

            CREATE TABLE IF NOT EXISTS orders (
                id INT AUTO_INCREMENT PRIMARY KEY,
                user_id INT,
                amount DECIMAL(10,2),
                order_date DATE
            );
            INSERT INTO orders (user_id, amount, order_date) VALUES
            (1, 150.0, '2023-01-10'),
            (1, 200.0, '2023-02-15'),
            (2, 50.0, '2023-03-20');

            CREATE TABLE IF NOT EXISTS order_details (
                id INT AUTO_INCREMENT PRIMARY KEY,
                unit_price DOUBLE,
                quantity INT,
                discount DOUBLE
            );
            INSERT INTO order_details (unit_price, quantity, discount) VALUES
            (10.123, 2, 0.1),
            (20.456, 1, 0.05);
        ";
        await cmd.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync() => await Container.DisposeAsync();
}


public class MySqlStrategyTests(MySqlFixture fixture) : BaseStrategyTests<MySqlStrategy, MySqlFixture>(fixture)
{
    protected override MySqlStrategy CreateStrategy(IQueryValueParserService parser, IConfiguration configuration)
        => new(parser, configuration);

    protected override string TestTableName => "users";
    protected override string TestOrdersTableName => "orders";
    protected override string TestSchemaName => "test_db";

    protected override string TableNotFoundErrorCode => "1146";
    protected override string ColumnNotFoundErrorCode => "1054";

    [Fact]
    public override async Task GetColumnsAsync_ShouldReturnColumnTypes()
    {
        await base.GetColumnsAsync_ShouldReturnColumnTypes();
        var columns = await Strategy.GetColumnsAsync(Fixture.ConnectionString, TestSchemaName, TestTableName, TestContext.Current.CancellationToken);
        Assert.Contains(columns, c => c.Column == "id" && c.Type.Contains("int", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(columns, c => c.Column == "active" && c.Type.Contains("tinyint", StringComparison.OrdinalIgnoreCase));
    }

    protected override DmlDefinition CreateInsertDml() => new()
    {
        Operation = DmlOperation.Insert,
        TableName = TestTableName,
        Values = [
            new NameValuePair { FieldName = "name", Value = "David" },
            new NameValuePair { FieldName = "age", Value = 40 },
            new NameValuePair { FieldName = "active", Value = true }
        ]
    };

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
        var connStr = Strategy.BuildConnectionString(model);
        var builder = new MySqlConnectionStringBuilder(connStr);
        Assert.Equal("localhost", builder.Server);
        Assert.Equal("mydb", builder.Database);
        Assert.Equal("user", builder.UserID);
        Assert.Equal("pw", builder.Password);
        Assert.Equal(uint.Parse("3306"), builder.Port);
    }

    [Fact]
    public async Task ExecuteQueryAsync_ShouldReturnDbError_WhenValueFormatIsIncorrect()
    {
        var ex = await Assert.ThrowsAsync<Exception>(() => Strategy.ExecuteQueryAsync(
            new QueryDefinition
            {
                TableName = TestTableName,
                WhereColumnsAndValues = [new BasicWhereCondition { FieldName = "created_date", Operator = "=", Value = "not-a-date" }]
            },
            Fixture.ConnectionString,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.True(ex.Message.Contains("code=1292") || ex.Message.Contains("Error"), $"Result was: {ex.Message}");
    }

    [Fact]
    public async Task ExecuteQueryAsync_ShouldReturnDbError_WhenSyntaxIsInvalid()
    {
        var ex = await Assert.ThrowsAsync<Exception>(() => Strategy.ExecuteQueryAsync(
            new QueryDefinition
            {
                TableName = TestTableName,
                WhereColumnsAndValues = [new BasicWhereCondition { FieldName = "name", Operator = "ILIKE", Value = "test" }]
            },
            Fixture.ConnectionString,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("code=1064", ex.Message);
        Assert.Contains("message=", ex.Message);
        Assert.Contains("syntax", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
