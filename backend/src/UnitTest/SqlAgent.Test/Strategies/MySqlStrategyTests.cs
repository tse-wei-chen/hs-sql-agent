using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MySql.Data.MySqlClient;
using Microsoft.Extensions.Configuration;
using Moq;
using SqlAgent.Service.Interfaces;
using SqlAgent.Service.Models;
using SqlAgent.Service.Strategies;
using SqlAgent.Service.Services;
using Xunit;
using System.Linq;
using Dapper;
using System.Text.Json;

namespace SqlAgent.Test.Strategies;

public class MySqlStrategyTests : IDisposable
{
	private readonly IConfiguration _configuration;
	private readonly MySqlConnection _masterConnection;
	private readonly string _connectionString;
	private readonly Mock<IConfiguration> _configMock;
	private readonly QueryValueParserService _parser;
	private readonly MySqlStrategy _strategy;

	public MySqlStrategyTests()
	{
		_configuration = new ConfigurationBuilder()
			.SetBasePath(AppContext.BaseDirectory)
			.AddJsonFile("appsettings.json")
			.Build();

		_connectionString = _configuration.GetSection("Test:MySqlConnectionString").Value ?? "";

		_masterConnection = new MySqlConnection(_connectionString);
		_masterConnection.Open();

		// Cleanup and Setup
		_masterConnection.Execute("DROP TABLE IF EXISTS Orders;");
		_masterConnection.Execute("DROP TABLE IF EXISTS Users;");
		_masterConnection.Execute("CREATE TABLE Users (Id INT PRIMARY KEY, Name VARCHAR(100), Age INT, Active TINYINT(1));");
		_masterConnection.Execute("INSERT INTO Users (Id, Name, Age, Active) VALUES (1, 'Alice', 30, 1), (2, 'Bob', 25, 1), (3, 'Charlie', 35, 0);");

		_masterConnection.Execute("CREATE TABLE Orders (Id INT PRIMARY KEY, UserId INT, Amount DECIMAL(10,2), OrderDate DATETIME);");
		_masterConnection.Execute("INSERT INTO Orders (Id, UserId, Amount, OrderDate) VALUES (101, 1, 150.0, '2023-01-10'), (102, 1, 200.0, '2023-02-15'), (103, 2, 50.0, '2023-03-20');");

		_configMock = new Mock<IConfiguration>();
		_configMock.Setup(c => c["McpKeySettings:HmacSecretKey"]).Returns("TestSecretKey12345678901234567890");

		_parser = new QueryValueParserService();
		_strategy = new MySqlStrategy(_parser, _configMock.Object);
	}

	[Fact]
	public async Task ExecuteQueryAsync_FilteredJoin_ShouldWork()
	{
		var resultJson = await _strategy.ExecuteQueryAsync(_connectionString, "Users", alias: "u", joins: new List<JoinCondition> { new() { Table = "Orders", Alias = "o", First = "u.Id", Second = "o.UserId", Type = "INNER" } }, whereConditions: new List<WhereCondition> { new() { Field = "o.Amount", Operator = ">", Value = 100 } }, cancellationToken: TestContext.Current.CancellationToken);
		var result = JsonSerializer.Deserialize<List<JsonElement>>(resultJson);
		Assert.Equal(2, result?.Count);
	}

	[Fact]
	public async Task ExecuteDmlAsync_Update_ShouldWork()
	{
		var dml = new DmlDefinition { Operation = "update", TableName = "Users", Values = new List<NameValuePair> { new() { Name = "Active", Value = 0 } }, WhereConditions = new List<WhereCondition> { new() { Field = "Age", Operator = ">", Value = 28 } } };
		var dryRun = await _strategy.ExecuteDmlAsync(_connectionString, dml, TestContext.Current.CancellationToken);
		var token = ExtractToken(dryRun);
		dml.ConfirmToken = token;
		await _strategy.ExecuteDmlAsync(_connectionString, dml, TestContext.Current.CancellationToken);
		var activeStatus = _masterConnection.ExecuteScalar<int>("SELECT Active FROM Users WHERE Name = 'Alice'");
		Assert.Equal(0, activeStatus);
	}

	[Fact]
	public async Task ExecuteQueryAsync_ArithmeticAndCaseWhen_ShouldWork()
	{
		var select = new List<SelectCondition>
		{
			new() { Field = "Id" },
			new() { Alias = "Disc", Arithmetic = new SelectArithmeticCondition { Left = new SelectArithmeticCondition { FieldName = "Amount" }, Operator = "*", Constant = 0.9 } },
			new() { Alias = "Cat", CaseWhen = new List<SqlAgent.Service.Models.CaseWhenClause> { new() { Condition = new WhereCondition { Field = "Amount", Operator = ">", Value = 100 }, Value = "High" } }, ElseValue = "Low" }
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
		var dmlMismatch = new DmlDefinition { Operation = "delete", TableName = "Users", ConfirmToken = "BadToken" };
		var resMismatch = await _strategy.ExecuteDmlAsync(_connectionString, dmlMismatch, TestContext.Current.CancellationToken);
		Assert.Contains("Dry Run Result", resMismatch);
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
		_masterConnection?.Close();
		_masterConnection?.Dispose();
	}
}
