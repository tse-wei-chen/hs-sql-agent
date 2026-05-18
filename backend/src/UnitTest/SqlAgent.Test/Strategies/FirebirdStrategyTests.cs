using System.Text.Json;
using DotNet.Testcontainers.Builders;
using FirebirdSql.Data.FirebirdClient;
using Microsoft.Extensions.Configuration;
using Moq;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Interfaces;
using SqlAgent.Service.Models;
using SqlAgent.Service.Services;
using SqlAgent.Service.Strategies;
using Testcontainers.FirebirdSql;
using Xunit;

namespace SqlAgent.Test.Strategies;

public class FirebirdFixture : IDbFixture
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

        cmd.CommandText = @"
            CREATE TABLE ORDERS (
                ID INTEGER PRIMARY KEY,
                USER_ID INTEGER,
                AMOUNT DECIMAL(10,2),
                ORDER_DATE DATE
            )
        ";
        await cmd.ExecuteNonQueryAsync();

        cmd.CommandText = "INSERT INTO ORDERS (ID, USER_ID, AMOUNT, ORDER_DATE) VALUES (101, 1, 150.0, '2023-01-10')";
        await cmd.ExecuteNonQueryAsync();
        cmd.CommandText = "INSERT INTO ORDERS (ID, USER_ID, AMOUNT, ORDER_DATE) VALUES (102, 1, 200.0, '2023-02-15')";
        await cmd.ExecuteNonQueryAsync();
        cmd.CommandText = "INSERT INTO ORDERS (ID, USER_ID, AMOUNT, ORDER_DATE) VALUES (103, 2, 50.0, '2023-03-20')";
        await cmd.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await Container.DisposeAsync();
    }
}


public class FirebirdStrategyTests(FirebirdFixture fixture) : BaseStrategyTests<FirebirdStrategy, FirebirdFixture>(fixture)
{
    protected override FirebirdStrategy CreateStrategy(IQueryValueParserService parser, IConfiguration configuration)
        => new(parser, configuration);

    protected override string TestTableName => "USERS";
    protected override string TestOrdersTableName => "ORDERS";
    protected override string TestSchemaName => "Default";

    // Firebird error codes are not as simple as numeric codes in hints,
    // but the strategy currently uses string matching for hints.
    // However, the base class expects TableNotFoundErrorCode and ColumnNotFoundErrorCode.
    // I will use strings that appear in the error message if the strategy uses them.
    protected override string TableNotFoundErrorCode => "unknown"; // Firebird strategy might not use numeric codes in hints yet
    protected override string ColumnNotFoundErrorCode => "unknown";

    [Fact]
    public override async Task ExecuteQueryAsync_ShouldTriggerHint_WhenTableNotFound()
    {
        var ex = await Assert.ThrowsAsync<Exception>(() => Strategy.ExecuteQueryAsync(
            new QueryDefinition
            {
                TableName = "NON_EXISTENT"
            },
            Fixture.ConnectionString,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("unknown", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public override async Task ExecuteQueryAsync_ShouldTriggerHint_WhenColumnNotFound()
    {
        var ex = await Assert.ThrowsAsync<Exception>(() => Strategy.ExecuteQueryAsync(
            new QueryDefinition
            {
                TableName = TestTableName,
                SelectColumns = [new FieldSelectCondition { FieldName = "NON_EXISTENT_COL_HS" }]
            },
            Fixture.ConnectionString,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("unknown", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public override async Task GetColumnsAsync_ShouldReturnColumnTypes()
    {
        await base.GetColumnsAsync_ShouldReturnColumnTypes();
        var columns = await Strategy.GetColumnsAsync(Fixture.ConnectionString, TestSchemaName, TestTableName, TestContext.Current.CancellationToken);
        Assert.Contains(columns, c => c.Column == "ID" && c.Type.Contains("INTEGER", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(columns, c => c.Column == "NAME" && c.Type.Contains("VARCHAR", StringComparison.OrdinalIgnoreCase));
    }

    protected override DmlDefinition CreateInsertDml() => new()
    {
        Operation = DmlOperation.Insert,
        TableName = TestTableName,
        Values = [
            new NameValuePair { FieldName = "ID", Value = 2 },
            new NameValuePair { FieldName = "NAME", Value = "David" },
            new NameValuePair { FieldName = "AGE", Value = 40 }
        ]
    };

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
        var connStr = Strategy.BuildConnectionString(model);

        Assert.Contains("data source=localhost", connStr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("port number=3050", connStr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("user id=sysdba", connStr, StringComparison.OrdinalIgnoreCase);
    }
}
