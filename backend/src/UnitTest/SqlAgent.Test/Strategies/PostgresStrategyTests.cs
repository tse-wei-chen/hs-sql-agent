using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Npgsql;
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
using System.Threading;

namespace SqlAgent.Test.Strategies;

public class PostgresStrategyTests : IDisposable
{
	private readonly IConfiguration _configuration;
	private readonly NpgsqlConnection? _masterConnection;
	private readonly string _connectionString;
	private readonly Mock<IConfiguration> _configMock;
	private readonly QueryValueParserService _parser;
	private readonly PostgresStrategy _strategy;

	public PostgresStrategyTests()
	{
		_configuration = new ConfigurationBuilder()
			.SetBasePath(AppContext.BaseDirectory)
			.AddJsonFile("appsettings.json")
			.Build();

		_connectionString = _configuration.GetSection("Test:PostgresConnectionString").Value ?? "";

		_masterConnection = new NpgsqlConnection(_connectionString);
		_masterConnection.Open();

		// Cleanup and Setup
		_masterConnection.Execute("DROP TABLE IF EXISTS \"Orders\" CASCADE;");
		_masterConnection.Execute("DROP TABLE IF EXISTS \"Users\" CASCADE;");
		_masterConnection.Execute("CREATE TABLE \"Users\" (\"Id\" INTEGER PRIMARY KEY, \"Name\" TEXT, \"Age\" INTEGER, \"Active\" BOOLEAN);");
		_masterConnection.Execute("INSERT INTO \"Users\" (\"Id\", \"Name\", \"Age\", \"Active\") VALUES (1, 'Alice', 30, true), (2, 'Bob', 25, true), (3, 'Charlie', 35, false);");

		_masterConnection.Execute("CREATE TABLE \"Orders\" (\"Id\" INTEGER PRIMARY KEY, \"UserId\" INTEGER, \"Amount\" DECIMAL, \"OrderDate\" TIMESTAMP);");
		_masterConnection.Execute("INSERT INTO \"Orders\" (\"Id\", \"UserId\", \"Amount\", \"OrderDate\") VALUES (101, 1, 150.0, '2023-01-10'), (102, 1, 200.0, '2023-02-15'), (103, 2, 50.0, '2023-03-20');");

		_configMock = new Mock<IConfiguration>();
		_configMock.Setup(c => c["McpKeySettings:HmacSecretKey"]).Returns("TestSecretKey12345678901234567890");

		_parser = new QueryValueParserService();
		_strategy = new PostgresStrategy(_parser, _configMock.Object);
	}

	[Fact]
	public async Task ExecuteQueryAsync_FilteredJoin_ShouldWork()
	{
		var resultJson = await _strategy.ExecuteQueryAsync(_connectionString, "Users", alias: "u",
			joins: new List<JoinCondition> { new JoinCondition { Table = "Orders", Alias = "o", First = "u.Id", Second = "o.UserId", Type = "INNER" } },
			whereConditions: new List<WhereCondition> { new WhereCondition { Field = "o.Amount", Operator = ">", Value = 100 } },
			cancellationToken: CancellationToken.None);

		if (resultJson.StartsWith("Error")) throw new Exception(resultJson);

		var result = JsonSerializer.Deserialize<List<JsonElement>>(resultJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
		Assert.Equal(2, result.Count);
	}

	[Fact]
	public async Task ExecuteDmlAsync_Update_ShouldWork()
	{
		var dml = new DmlDefinition { Operation = "update", TableName = "Users", Values = new List<NameValuePair> { new NameValuePair { Name = "Active", Value = false } }, WhereConditions = new List<WhereCondition> { new WhereCondition { Field = "Age", Operator = ">", Value = 28 } } };
		var dryRun = await _strategy.ExecuteDmlAsync(_connectionString, dml, CancellationToken.None);
		var token = ExtractToken(dryRun);
		dml.ConfirmToken = token;
		var finalResult = await _strategy.ExecuteDmlAsync(_connectionString, dml, CancellationToken.None);

		Assert.Contains("Success", finalResult);
		var activeStatus = _masterConnection.ExecuteScalar<bool>("SELECT \"Active\" FROM \"Users\" WHERE \"Name\" = 'Alice'");
		Assert.False(activeStatus);
	}

	[Fact]
	public async Task ExecuteQueryAsync_ArithmeticAndCaseWhen_ShouldWork()
	{
		var select = new List<SelectCondition>
		{
			new SelectCondition { Field = "Id" },
			new SelectCondition { Alias = "Disc", Arithmetic = new SelectArithmeticCondition { Left = new SelectArithmeticCondition { FieldName = "Amount" }, Operator = "*", Constant = 0.9 } },
			new SelectCondition { Alias = "Cat", CaseWhen = new List<CaseWhenClause> { new CaseWhenClause { Condition = new WhereCondition { Field = "Amount", Operator = ">", Value = 100 }, Value = "High" } }, ElseValue = "Low" }
		};
		var resultJson = await _strategy.ExecuteQueryAsync(_connectionString, "Orders", selectColumns: select, cancellationToken: CancellationToken.None);

		if (resultJson.StartsWith("Error")) throw new Exception(resultJson);

		var result = JsonSerializer.Deserialize<List<JsonElement>>(resultJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
		var o101 = result.First(r => r.GetProperty("Id").GetInt32() == 101);
		Assert.Equal(135.0, o101.GetProperty("Disc").GetDouble(), 1);
		Assert.Equal("High", o101.GetProperty("Cat").GetString());
	}

	[Fact]
	public async Task ExecuteDmlAsync_RollbackAndMismatch_ShouldWork()
	{
		var dmlMismatch = new DmlDefinition { Operation = "delete", TableName = "Users", ConfirmToken = "BadToken" };
		var resMismatch = await _strategy.ExecuteDmlAsync(_connectionString, dmlMismatch, CancellationToken.None);
		Assert.Contains("Dry Run Result", resMismatch);
	}

	private string ExtractToken(string result)
	{
		var marker = "TokenRequired=";
		var start = result.IndexOf(marker) + marker.Length;
		var end = result.IndexOf(" |", start);
		return result.Substring(start, end - start);
	}

	public void Dispose()
	{
		_masterConnection?.Close();
		_masterConnection?.Dispose();
	}
}
