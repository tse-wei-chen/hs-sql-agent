using System.Text.Json;
using DotNet.Testcontainers.Builders;
using Microsoft.Extensions.Configuration;
using Moq;
using MySql.Data.MySqlClient;
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
            ('Bob', 25, true, '2023-02-01 10:00:00');
        ";
        await cmd.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync() => await Container.DisposeAsync();
}


public class MySqlStrategyTests(MySqlFixture fixture) : BaseStrategyTests<MySqlStrategy, MySqlFixture>(fixture)
{
    protected override MySqlStrategy CreateStrategy(IQueryValueParserService parser, IConfiguration configuration) 
        => new MySqlStrategy(parser, configuration);

    protected override string TestTableName => "users";
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
    public async Task ExecuteQueryAsync_ShouldTrigger1292Hint_WhenValueFormatIsIncorrect()
    {
        var res = await Strategy.ExecuteQueryAsync(Fixture.ConnectionString, TestTableName,
            whereConditions: [new WhereCondition { Field = "created_date", Operator = "=", Value = "not-a-date" }],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(res.Contains("code=1292") || res.Contains("Error"), $"Result was: {res}");
    }

    [Fact]
    public async Task ExecuteQueryAsync_ShouldTrigger1064Hint_WhenSyntaxIsInvalid()
    {
        var res = await Strategy.ExecuteQueryAsync(Fixture.ConnectionString, TestTableName,
            selectColumns: [new SelectCondition { Arithmetic = new SelectArithmeticCondition { FieldName = "name", Operator = "INVALID", Constant = 1 } }],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("code=1064", res);
        Assert.Contains("SQL syntax error", res);
    }
}
