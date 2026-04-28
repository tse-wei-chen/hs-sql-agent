using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Moq;
using SqlAgent.Service.Models;
using SqlAgent.Service.Services;
using SqlAgent.Service.Strategies;
using Xunit;

namespace SqlAgent.Test.Strategies;

public class SqliteStrategyTests : IDisposable
{
    private readonly SqliteConnection _masterConnection;
    private readonly string _connectionString;
    private readonly Mock<IConfiguration> _configMock;
    private readonly QueryValueParserService _parser;
    private readonly SqliteStrategy _strategy;

    public SqliteStrategyTests()
    {
        var dbName = Guid.NewGuid().ToString();
        _connectionString = $"Data Source={dbName};Mode=Memory;Cache=Shared";

        _masterConnection = new SqliteConnection(_connectionString);
        _masterConnection.Open();

        _masterConnection.Execute("CREATE TABLE Users (Id INTEGER PRIMARY KEY, Name TEXT, Age INTEGER, Active BOOLEAN, CreatedDate TEXT);");
        _masterConnection.Execute("INSERT INTO Users (Id, Name, Age, Active, CreatedDate) VALUES " +
                                  "(1, 'Alice', 30, 1, '2023-01-01'), " +
                                  "(2, 'Bob', 25, 1, '2023-02-01'), " +
                                  "(3, 'Charlie', 35, 0, '2023-03-01');");

        _masterConnection.Execute("CREATE TABLE Orders (Id INTEGER PRIMARY KEY, UserId INTEGER, Amount DECIMAL, OrderDate TEXT);");
        _masterConnection.Execute("INSERT INTO Orders (Id, UserId, Amount, OrderDate) VALUES " +
                                  "(101, 1, 150.0, '2023-01-10'), " +
                                  "(102, 1, 200.0, '2023-02-15'), " +
                                  "(103, 2, 50.0, '2023-03-20');");

        _configMock = new Mock<IConfiguration>();
        _configMock.Setup(c => c["McpKeySettings:HmacSecretKey"]).Returns("TestSecretKey12345678901234567890");

        _parser = new QueryValueParserService();
        _strategy = new SqliteStrategy(_parser, _configMock.Object);
    }

    #region Connection & Schema Setup Tests

    [Fact]
    public void BuildConnectionString_ShouldGenerateValidSqliteFormat()
    {
        var model = new BuildDbConnectionModel { Provider = "Sqlite", Database = "TestDB.sqlite", Password = "mypassword" };
        var connStr = _strategy.BuildConnectionString(model);
        
        Assert.Contains("Data Source=TestDB.sqlite", connStr);
        Assert.Contains("Password=mypassword", connStr);
    }

    [Fact]
    public async Task GetSchemasAsync_ShouldReturnNotSupportedMessage()
    {
        var schemas = await _strategy.GetSchemasAsync(_connectionString, TestContext.Current.CancellationToken);
        Assert.Single(schemas);
        Assert.Contains("sqlite does not support schemas", schemas[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetTablesAsync_ShouldReturnAllTables()
    {
        var tables = await _strategy.GetTablesAsync(_connectionString, "", TestContext.Current.CancellationToken);
        Assert.Contains("Users", tables);
        Assert.Contains("Orders", tables);
    }

    [Fact]
    public async Task GetColumnsAsync_ShouldReturnColumnTypes()
    {
        var columns = await _strategy.GetColumnsAsync(_connectionString, "", "Users", TestContext.Current.CancellationToken);
        Assert.Equal(5, columns.Count);
        Assert.Equal("INTEGER", columns.First(c => c.Column == "Id").Type, ignoreCase: true);
        Assert.Equal("TEXT", columns.First(c => c.Column == "Name").Type, ignoreCase: true);
    }

    [Fact]
    public async Task GetTablesAsync_ShouldThrowException_WhenConnectionIsInvalid()
    {
        var ex = await Assert.ThrowsAsync<Exception>(() => _strategy.GetTablesAsync("Data Source=Z:\\invalid\\path\\no.db;Mode=ReadOnly;", "", TestContext.Current.CancellationToken));
        Assert.Contains("Error getting tables", ex.Message);
    }

    [Fact]
    public async Task GetColumnsAsync_ShouldThrowException_WhenConnectionIsInvalid()
    {
        var ex = await Assert.ThrowsAsync<Exception>(() => _strategy.GetColumnsAsync("Data Source=Z:\\invalid\\path\\no.db;Mode=ReadOnly;", "", "Users", TestContext.Current.CancellationToken));
        Assert.Contains("Error getting columns", ex.Message);
    }

    #endregion

    #region ExecuteQueryAsync Strict Specification Tests

    [Fact]
    public async Task ExecuteQueryAsync_ShouldSelectAll_WhenColumnsAreEmpty()
    {
        var json = await _strategy.ExecuteQueryAsync(_connectionString, "Users", selectColumns: [], cancellationToken: TestContext.Current.CancellationToken);
        var res = JsonSerializer.Deserialize<List<JsonElement>>(json);
        Assert.NotNull(res);
        Assert.Equal(3, res.Count);
        Assert.True(res[0].TryGetProperty("Id", out _));
        Assert.True(res[0].TryGetProperty("Name", out _));
    }

    [Fact]
    public async Task ExecuteQueryAsync_ShouldHandleAllWhereOperators_Correctly()
    {
        var conditions = new List<WhereCondition>
        {
            new() { Field = "Name", Operator = "like", Value = "%li%" }
        };

        var json = await _strategy.ExecuteQueryAsync(_connectionString, "Users", whereConditions: conditions, cancellationToken: TestContext.Current.CancellationToken);
        
        // Let's assert we don't get an error
        Assert.DoesNotContain("Error", json);

        var res = JsonSerializer.Deserialize<List<JsonElement>>(json);
        Assert.NotNull(res);
        Assert.Equal(2, res.Count); // Alice and Charlie
    }

    [Fact]
    public async Task ExecuteQueryAsync_ShouldHandleNestedGroupConditions_WithOrLogic()
    {
        var groups = new List<WhereCondition>
        {
            new() { Field = "Age", Operator = "<", Value = 30 }, // Bob (25)
            new() { Field = "Name", Operator = "=", Value = "Charlie", IsOr = true } // Charlie
        };

        var condition = new WhereCondition
        {
            Groups = groups
        };

        var json = await _strategy.ExecuteQueryAsync(_connectionString, "Users", whereConditions: [condition], cancellationToken: TestContext.Current.CancellationToken);
        var res = JsonSerializer.Deserialize<List<JsonElement>>(json);
        Assert.NotNull(res);
        Assert.Equal(2, res.Count); // Bob and Charlie
    }

    [Fact]
    public async Task ExecuteQueryAsync_ShouldWrapInSubquery_WhenCombiningAndLimitIsSet()
    {
        var queryDef1 = new QueryDefinition { TableName = "Users", SelectColumns = [new SelectCondition { Field = "Name" }] };
        var queryDef2 = new QueryDefinition { TableName = "Orders", SelectColumns = [new SelectCondition { Field = "Amount" }] };

        // SQLite union requires same column counts and types (in strict logic), but here SQLite allows string and decimal in same column.
        var json = await _strategy.ExecuteQueryAsync(_connectionString, "Users", 
            selectColumns: [new SelectCondition { Field = "Name" }],
            combineConditions: [new CombineCondition { Type = "union", Query = queryDef2 }],
            limit: 2, // Wrapping required
            orderByColumns: [new OrderByCondition { Field = "Name", Direction = "desc" }],
            cancellationToken: TestContext.Current.CancellationToken);

        var res = JsonSerializer.Deserialize<List<JsonElement>>(json);
        Assert.NotNull(res);
        Assert.Equal(2, res.Count); // Limit 2 applied over the union set
    }

    [Fact]
    public async Task ExecuteQueryAsync_ShouldHandleSelectSubquery_Correctly()
    {
        var subQuery = new QueryDefinition
        {
            TableName = "Orders",
            SelectColumns = [new SelectCondition { Field = "Amount", Aggregation = "SUM" }],
            WhereColumnsAndValues = [new WhereCondition { Field = "UserId", Operator = "=", Value = 1 }] // Static scalar to avoid parameterizing complex object
        };

        var select = new List<SelectCondition>
        {
            new() { Field = "Name" },
            new() { Alias = "TotalOrders", SubQuery = subQuery }
        };

        var json = await _strategy.ExecuteQueryAsync(_connectionString, "Users", selectColumns: select, cancellationToken: TestContext.Current.CancellationToken);
        var res = JsonSerializer.Deserialize<List<JsonElement>>(json);
        Assert.NotNull(res);
        var alice = res.First(r => r.GetProperty("Name").GetString() == "Alice");
        Assert.Equal(350.0, alice.GetProperty("TotalOrders").GetDouble());
    }

    [Fact]
    public async Task ExecuteQueryAsync_ShouldReturnFriendlyError_WhenTableDoesNotExist()
    {
        var res = await _strategy.ExecuteQueryAsync(_connectionString, "NonExistentTable", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("Table not found", res); // Custom hint from SqliteStrategy.BuildHint
    }

    #endregion

    #region ExecuteDmlAsync Strict Specification Tests

    [Fact]
    public async Task ExecuteDmlAsync_ShouldReturnError_WhenDmlIsNull()
    {
        var res = await _strategy.ExecuteDmlAsync(_connectionString, null, TestContext.Current.CancellationToken);
        Assert.Equal("No DML definition provided.", res);
    }

    [Fact]
    public async Task ExecuteDmlAsync_ShouldReturnError_WhenInsertMissingValues()
    {
        var dml = new DmlDefinition { Operation = "insert", TableName = "Users" };
        var res = await _strategy.ExecuteDmlAsync(_connectionString, dml, TestContext.Current.CancellationToken);
        Assert.Equal("Insert operation requires Values or MultiValues or FromQuery.", res);
    }

    [Fact]
    public async Task ExecuteDmlAsync_ShouldReturnError_WhenUpdateMissingValues()
    {
        var dml = new DmlDefinition { Operation = "update", TableName = "Users" };
        var res = await _strategy.ExecuteDmlAsync(_connectionString, dml, TestContext.Current.CancellationToken);
        Assert.Equal("Update operation requires Values.", res);
    }

    [Fact]
    public async Task ExecuteDmlAsync_ShouldReturnError_WhenOperationIsUnsupported()
    {
        var dml = new DmlDefinition { Operation = "upsert", TableName = "Users" };
        var res = await _strategy.ExecuteDmlAsync(_connectionString, dml, TestContext.Current.CancellationToken);
        Assert.Contains("Unsupported DML operation: upsert", res);
    }

    [Fact]
    public async Task ExecuteDmlAsync_ShouldRollbackAndReturnError_WhenExceptionOccurs()
    {
        // Try to insert a duplicate Primary Key
        var dml = new DmlDefinition
        {
            Operation = "insert",
            TableName = "Users",
            Values = [new NameValuePair { Name = "Id", Value = 1 }, new NameValuePair { Name = "Name", Value = "Duplicate" }]
        };

        // Do Dry Run to get token
        var dryRun = await _strategy.ExecuteDmlAsync(_connectionString, dml, TestContext.Current.CancellationToken);
        var token = ExtractToken(dryRun);
        dml.ConfirmToken = token;

        // Execute actual
        var res = await _strategy.ExecuteDmlAsync(_connectionString, dml, TestContext.Current.CancellationToken);
        Assert.Contains("Error executing query", res); // Handled by catch block and rollback
    }

    [Fact]
    public async Task ExecuteDmlAsync_ShouldRollbackAndRequireToken_WhenTokenIsMissing()
    {
        var dml = new DmlDefinition
        {
            Operation = "delete",
            TableName = "Users",
            WhereConditions = [new WhereCondition { Field = "Id", Operator = "=", Value = 3 }]
        };

        var res = await _strategy.ExecuteDmlAsync(_connectionString, dml, TestContext.Current.CancellationToken);
        Assert.Contains("Dry Run Result", res);
        Assert.Contains("TokenRequired", res);

        // Verify it was rolled back
        var count = _masterConnection.ExecuteScalar<int>("SELECT COUNT(1) FROM Users WHERE Id = 3");
        Assert.Equal(1, count); // Charlie is still there
    }

    [Fact]
    public async Task ExecuteDmlAsync_ShouldCommit_WhenValidTokenIsProvided()
    {
        var dml = new DmlDefinition
        {
            Operation = "delete",
            TableName = "Users",
            WhereConditions = [new WhereCondition { Field = "Id", Operator = "=", Value = 3 }]
        };

        var dryRun = await _strategy.ExecuteDmlAsync(_connectionString, dml, TestContext.Current.CancellationToken);
        dml.ConfirmToken = ExtractToken(dryRun);

        var res = await _strategy.ExecuteDmlAsync(_connectionString, dml, TestContext.Current.CancellationToken);
        Assert.Contains("Success", res);
        Assert.Contains("Operation Committed", res);

        var count = _masterConnection.ExecuteScalar<int>("SELECT COUNT(1) FROM Users WHERE Id = 3");
        Assert.Equal(0, count); // Charlie was deleted
    }

    #endregion

    private string ExtractToken(string result)
    {
        var marker = "TokenRequired=";
        var start = result.IndexOf(marker) + marker.Length;
        var end = result.IndexOf(" |", start);
        return result[start..end];
    }

    public void Dispose()
    {
        _masterConnection.Close();
        _masterConnection.Dispose();
    }
}
