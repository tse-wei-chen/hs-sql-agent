using System.Text.Json;
using DotNet.Testcontainers.Builders;
using MySql.Data.MySqlClient;
using SqlAgent.Service.Core.Compilation;
using SqlAgent.Service.Core.Providers;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Models;
using SqlAgent.Service.SqlParsing;
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

        var strategy = new MySqlStrategy();

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
    protected override bool SupportsOffsetTimestamp => false;
    protected override MySqlStrategy CreateStrategy() => new();

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
        var ex = await Assert.ThrowsAsync<ProviderExecutionException>(() => Strategy.ExecuteQueryAsync(
            new QueryDefinition
            {
                TableName = TestTableName,
                WhereColumnsAndValues = [new BasicWhereCondition { FieldName = "created_date", Operator = "=", Value = "not-a-date" }]
            },
            Fixture.ConnectionString,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(SqlAgentToolType.MySQL, ex.ProviderType);
        Assert.Equal("query", ex.Operation);
        Assert.True(ex.Code is "1292" or "1525", $"Result was: {ex.Message}");
    }

    [Fact]
    public async Task ExecuteQueryAsync_ShouldFailClosedBeforeDb_WhenOperatorIsUnsupported()
    {
        var ex = await Assert.ThrowsAsync<SqlCompilationException>(() => Strategy.ExecuteQueryAsync(
            new QueryDefinition
            {
                TableName = TestTableName,
                WhereColumnsAndValues = [new BasicWhereCondition { FieldName = "name", Operator = "ILIKE", Value = "test" }]
            },
            Fixture.ConnectionString,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("PostgreSQL-specific", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MySQL", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteQueryAsync_PostgresStringAggCustomSeparator_ExecutesOnMySql()
    {
        var definition = SqlDefinitionParser.ParseQuery(
            "SELECT STRING_AGG(name, '|') AS names FROM users");
        definition.SourceDialect = SqlAgentToolType.Postgres;

        var json = await Strategy.ExecuteQueryAsync(
            definition,
            Fixture.ConnectionString,
            cancellationToken: TestContext.Current.CancellationToken);

        using var document = JsonDocument.Parse(json);
        var names = document.RootElement[0]
            .EnumerateObject()
            .Single()
            .Value
            .GetString();
        Assert.NotNull(names);
        Assert.Equal(
            new[] { "Alice", "Bob", "Charlie" },
            names.Split('|').OrderBy(value => value, StringComparer.Ordinal).ToArray());
    }
}
