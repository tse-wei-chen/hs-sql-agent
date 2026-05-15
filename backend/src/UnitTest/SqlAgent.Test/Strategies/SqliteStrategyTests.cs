using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Moq;
using SqlAgent.Service.Interfaces;
using SqlAgent.Service.Models;
using SqlAgent.Service.Services;
using SqlAgent.Service.Strategies;
using Xunit;

namespace SqlAgent.Test.Strategies;

public class SqliteFixture : IDbFixture
{
    private SqliteConnection? _masterConnection;
    public string ConnectionString { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        var dbName = Guid.NewGuid().ToString();
        ConnectionString = $"Data Source={dbName};Mode=Memory;Cache=Shared";

        _masterConnection = new SqliteConnection(ConnectionString);
        await _masterConnection.OpenAsync();

        await _masterConnection.ExecuteAsync("CREATE TABLE Users (Id INTEGER PRIMARY KEY, Name TEXT, Age INTEGER, Active BOOLEAN, CreatedDate TEXT);");
        await _masterConnection.ExecuteAsync("INSERT INTO Users (Id, Name, Age, Active, CreatedDate) VALUES " +
                                  "(1, 'Alice', 30, 1, '2023-01-01'), " +
                                  "(2, 'Bob', 25, 1, '2023-02-01'), " +
                                  "(3, 'Charlie', 35, 0, '2023-03-01');");

        await _masterConnection.ExecuteAsync("CREATE TABLE Orders (Id INTEGER PRIMARY KEY, UserId INTEGER, Amount DECIMAL, OrderDate TEXT);");
        await _masterConnection.ExecuteAsync("INSERT INTO Orders (Id, UserId, Amount, OrderDate) VALUES " +
                                  "(101, 1, 150.0, '2023-01-10'), " +
                                  "(102, 1, 200.0, '2023-02-15'), " +
                                  "(103, 2, 50.0, '2023-03-20');");
    }

    public async ValueTask DisposeAsync()
    {
        _masterConnection?.Close();
        _masterConnection?.Dispose();
        await ValueTask.CompletedTask;
    }
}


public class SqliteStrategyTests(SqliteFixture fixture) : BaseStrategyTests<SqliteStrategy, SqliteFixture>(fixture)
{
    protected override SqliteStrategy CreateStrategy(IQueryValueParserService parser, IConfiguration configuration)
        => new SqliteStrategy(parser, configuration);

    protected override string TestTableName => "Users";
    protected override string TestSchemaName => "";

    protected override string TableNotFoundErrorCode => "SQLITE_1"; 
    protected override string ColumnNotFoundErrorCode => "SQLITE_1";

    [Fact]
    public override async Task GetSchemasAsync_ShouldReturnAvailableSchemas()
    {
        var schemas = await Strategy.GetSchemasAsync(Fixture.ConnectionString, TestContext.Current.CancellationToken);
        Assert.Single(schemas);
        Assert.Contains("sqlite does not support schemas", schemas[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public override async Task GetColumnsAsync_ShouldReturnColumnTypes()
    {
        await base.GetColumnsAsync_ShouldReturnColumnTypes();
        var columns = await Strategy.GetColumnsAsync(Fixture.ConnectionString, TestSchemaName, TestTableName, TestContext.Current.CancellationToken);
        Assert.Equal("INTEGER", columns.First(c => c.Column == "Id").Type, ignoreCase: true);
        Assert.Equal("TEXT", columns.First(c => c.Column == "Name").Type, ignoreCase: true);
    }


    protected override DmlDefinition CreateInsertDml() => new DmlDefinition
    {
        Operation = "insert",
        TableName = TestTableName,
        Values = [
            new NameValuePair { Name = "Name", Value = "David" },
            new NameValuePair { Name = "Age", Value = 40 },
            new NameValuePair { Name = "Active", Value = true }
        ]
    };

    [Fact]
    public void BuildConnectionString_ShouldGenerateValidSqliteFormat()
    {
        var model = new BuildDbConnectionModel { Provider = "Sqlite", Database = "TestDB.sqlite", Password = "mypassword" };
        var connStr = Strategy.BuildConnectionString(model);

        Assert.Contains("Data Source=TestDB.sqlite", connStr);
        Assert.Contains("Password=mypassword", connStr);
    }

    [Fact]
    public async Task ExecuteQueryAsync_ShouldWrapInSubquery_WhenCombiningAndLimitIsSet()
    {
        var queryDef2 = new QueryDefinition { TableName = "Orders", SelectColumns = [new SelectCondition { Field = "Amount" }] };

        var json = await Strategy.ExecuteQueryAsync(Fixture.ConnectionString, "Users",
            selectColumns: [new SelectCondition { Field = "Name" }],
            combineConditions: [new CombineCondition { Type = "union", Query = queryDef2 }],
            limit: 2,
            orderByColumns: [new OrderByCondition { Field = "Name", Direction = "desc" }],
            cancellationToken: TestContext.Current.CancellationToken);

        var res = JsonSerializer.Deserialize<List<JsonElement>>(json);
        Assert.NotNull(res);
        Assert.Equal(2, res.Count);
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
                        Right = new SelectArithmeticCondition { FieldName = "Age" }
                    },
                    Alias = "Delta"
                }
            ],
            whereConditions: [new WhereCondition { Field = "Name", Operator = "=", Value = "Alice" }],
            cancellationToken: TestContext.Current.CancellationToken);

        var rows = JsonSerializer.Deserialize<List<JsonElement>>(json);

        Assert.NotNull(rows);
        Assert.Single(rows);
        Assert.Equal(-29, rows[0].GetProperty("Delta").GetInt32());
    }

    [Fact]
    public async Task ExecuteQueryAsync_ShouldSupportFunctionExpressionsInGrouping()
    {
        var yearFunction = new SqlFunctionCondition
        {
            Name = "SUBSTR",
            Arguments =
            [
                new SqlFunctionArgument { FieldName = "CreatedDate" },
                new SqlFunctionArgument { Constant = 1 },
                new SqlFunctionArgument { Constant = 4 }
            ]
        };

        var json = await Strategy.ExecuteQueryAsync(Fixture.ConnectionString, TestTableName,
            selectColumns:
            [
                new SelectCondition { Function = yearFunction, Alias = "YearPart" },
                new SelectCondition { Field = "Id", Aggregation = "COUNT", Alias = "UserCount" }
            ],
            groupByConditions:
            [
                new GroupByCondition { Function = yearFunction }
            ],
            orderByColumns:
            [
                new OrderByCondition { Function = yearFunction, Direction = "asc" }
            ],
            cancellationToken: TestContext.Current.CancellationToken);

        var rows = JsonSerializer.Deserialize<List<JsonElement>>(json);

        Assert.NotNull(rows);
        Assert.Single(rows);
        Assert.Equal("2023", rows[0].GetProperty("YearPart").GetString());
        Assert.Equal(3, rows[0].GetProperty("UserCount").GetInt32());
    }
}
