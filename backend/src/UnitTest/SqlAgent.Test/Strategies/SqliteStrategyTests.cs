using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Moq;
using SqlAgent.Service.Models;
using SqlAgent.Service.Strategies;
using SqlAgent.Service.Services;
using Xunit;
using Dapper;
using System.Text.Json;

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

        _masterConnection.Execute("CREATE TABLE Users (Id INTEGER PRIMARY KEY, Name TEXT, Age INTEGER, Active BOOLEAN);");
        _masterConnection.Execute("INSERT INTO Users (Id, Name, Age, Active) VALUES (1, 'Alice', 30, 1), (2, 'Bob', 25, 1), (3, 'Charlie', 35, 0);");

        _masterConnection.Execute("CREATE TABLE Orders (Id INTEGER PRIMARY KEY, UserId INTEGER, Amount DECIMAL, OrderDate TEXT);");
        _masterConnection.Execute("INSERT INTO Orders (Id, UserId, Amount, OrderDate) VALUES (101, 1, 150.0, '2023-01-10'), (102, 1, 200.0, '2023-02-15'), (103, 2, 50.0, '2023-03-20');");

        _configMock = new Mock<IConfiguration>();
        _configMock.Setup(c => c["McpKeySettings:HmacSecretKey"]).Returns("TestSecretKey12345678901234567890");

        _parser = new QueryValueParserService();
        _strategy = new SqliteStrategy(_parser, _configMock.Object);
    }

    [Fact]
    public async Task ExecuteQueryAsync_FilteredJoin_ShouldReturnCorrectJson()
    {
        var resultJson = await _strategy.ExecuteQueryAsync(_connectionString, "Users", alias: "u", joins: [new JoinCondition { Table = "Orders", Alias = "o", First = "u.Id", Second = "o.UserId", Type = "INNER" }], whereConditions: [new WhereCondition { Field = "o.Amount", Operator = ">", Value = 100 }], cancellationToken: TestContext.Current.CancellationToken);
        var result = JsonSerializer.Deserialize<List<JsonElement>>(resultJson);
        Assert.Equal(2, result?.Count);
    }

    [Fact]
    public async Task ExecuteDmlAsync_Update_ShouldFollowTwoStepConfirmation()
    {
        var dml = new DmlDefinition { Operation = "update", TableName = "Users", Values = [new NameValuePair { Name = "Active", Value = 0 }], WhereConditions = [new WhereCondition { Field = "Age", Operator = ">", Value = 28 }] };
        var dryRun = await _strategy.ExecuteDmlAsync(_connectionString, dml, TestContext.Current.CancellationToken);
        var token = ExtractToken(dryRun);
        dml.ConfirmToken = token;
        await _strategy.ExecuteDmlAsync(_connectionString, dml, TestContext.Current.CancellationToken);
        var activeStatus = _masterConnection.ExecuteScalar<int>("SELECT Active FROM Users WHERE Name = 'Alice'");
        Assert.Equal(0, activeStatus);
    }

    [Fact]
    public async Task ExecuteQueryAsync_GroupingAndHaving_ShouldWork()
    {
        var resultJson = await _strategy.ExecuteQueryAsync(_connectionString, "Orders",
            selectColumns: [new SelectCondition { Field = "UserId" }, new SelectCondition { Field = "Amount", Aggregation = "AVG", Alias = "Avg" }],
            groupByConditions: [new GroupByCondition { Field = "UserId" }],
            havingConditions: [new HavingCondition { Field = "Amount", Operator = ">", Value = 100, Aggregation = "AVG" }],
            cancellationToken: TestContext.Current.CancellationToken);
        var result = JsonSerializer.Deserialize<List<JsonElement>>(resultJson);
        Assert.NotNull(result);
        Assert.Single(result);
    }

    [Fact]
    public async Task ExecuteQueryAsync_ArithmeticAndCaseWhen_ShouldWork()
    {
        var select = new List<SelectCondition>
        {
            new() { Field = "Id" },
            new() { Alias = "Disc", Arithmetic = new SelectArithmeticCondition { Left = new SelectArithmeticCondition { FieldName = "Amount" }, Operator = "*", Constant = 0.9 } },
            new() { Alias = "Cat", CaseWhen = [new CaseWhenClause { Condition = new WhereCondition { Field = "Amount", Operator = ">", Value = 100 }, Value = "High" }], ElseValue = "Low" }
        };
        var resultJson = await _strategy.ExecuteQueryAsync(_connectionString, "Orders", selectColumns: select, cancellationToken: TestContext.Current.CancellationToken);
        var result = JsonSerializer.Deserialize<List<JsonElement>>(resultJson);
        Assert.NotNull(result);
        var o101 = result.First(r => r.GetProperty("Id").GetInt32() == 101);
        Assert.Equal(135.0, o101.GetProperty("Disc").GetDouble(), 1);
        Assert.Equal("High", o101.GetProperty("Cat").GetString());
    }

    [Fact]
    public async Task ExecuteDmlAsync_RollbackAndMismatch_ShouldWork()
    {
        // 1. Mismatch check
        var dmlMismatch = new DmlDefinition { Operation = "delete", TableName = "Users", ConfirmToken = "BadToken" };
        var resMismatch = await _strategy.ExecuteDmlAsync(_connectionString, dmlMismatch, TestContext.Current.CancellationToken);
        Assert.Contains("Dry Run Result", resMismatch);

        // 2. Rollback check (error during execution)
        var dmlError = new DmlDefinition { Operation = "insert", TableName = "Users", Values = [new NameValuePair { Name = "Id", Value = 1 }, new NameValuePair { Name = "Name", Value = "Duplicate" }] };
        var dryRun = await _strategy.ExecuteDmlAsync(_connectionString, dmlError, TestContext.Current.CancellationToken);
        var token = ExtractToken(dryRun);
        dmlError.ConfirmToken = token;
        var resError = await _strategy.ExecuteDmlAsync(_connectionString, dmlError, TestContext.Current.CancellationToken);
        Assert.Contains("Error", resError);
    }

    [Fact]
    public async Task ExecuteQueryAsync_FilterPermutations_ShouldWork()
    {
        // starts, ends, contains
        var resStr = await _strategy.ExecuteQueryAsync(_connectionString, "Users", whereConditions: [new WhereCondition { Field = "Name", Operator = "contains", Value = "ar" }], cancellationToken: TestContext.Current.CancellationToken);
        var resultStr = JsonSerializer.Deserialize<List<JsonElement>>(resStr);
        Assert.NotNull(resultStr);
        Assert.Single(resultStr);

        // IsNot, IsOr, isnull
        var resLogic = await _strategy.ExecuteQueryAsync(_connectionString, "Users", whereConditions: [new WhereCondition { Field = "Active", Operator = "isnull", IsNot = true }, new WhereCondition { Field = "Id", Operator = "=", Value = 999, IsOr = true }], cancellationToken: TestContext.Current.CancellationToken);
        var resultLogic = JsonSerializer.Deserialize<List<JsonElement>>(resLogic);
        Assert.NotNull(resultLogic);
        Assert.Equal(3, resultLogic.Count);
    }

    [Fact]
    public async Task ExecuteQueryAsync_AdvancedFeatures_ShouldWork()
    {
        // CTE & Union
        var q1 = new QueryDefinition { TableName = "Users", SelectColumns = [new SelectCondition { Field = "Id" }], WhereColumnsAndValues = [new WhereCondition { Field = "Id", Value = 1 }] };
        var resUnion = await _strategy.ExecuteQueryAsync(_connectionString, "Users", selectColumns: [new SelectCondition { Field = "Id" }], combineConditions: [new CombineCondition { Type = "union", Query = q1 }], cancellationToken: TestContext.Current.CancellationToken);
        var resultUnion = JsonSerializer.Deserialize<List<JsonElement>>(resUnion);
        Assert.NotNull(resultUnion);
        Assert.Equal(3, resultUnion.Count);

        // Subquery in Where
        var resSub = await _strategy.ExecuteQueryAsync(_connectionString, "Users", whereConditions: [new WhereCondition { Field = "Id", Operator = "in", SubQuery = q1 }], cancellationToken: TestContext.Current.CancellationToken);
        var resultSub = JsonSerializer.Deserialize<List<JsonElement>>(resSub);
        Assert.NotNull(resultSub);
        Assert.Single(resultSub);
    }

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
