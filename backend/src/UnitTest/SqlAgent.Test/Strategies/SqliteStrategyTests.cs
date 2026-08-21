using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Moq;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Interfaces;
using SqlAgent.Service.Models;
using SqlAgent.Service.Services;
using SqlAgent.Service.SqlParsing;
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

        await _masterConnection.ExecuteAsync("CREATE TABLE OrderDetails (Id INTEGER PRIMARY KEY, UnitPrice REAL, Quantity INTEGER, Discount REAL);");
        await _masterConnection.ExecuteAsync("INSERT INTO OrderDetails (Id, UnitPrice, Quantity, Discount) VALUES " +
                                  "(1, 10.123, 2, 0.1), " +
                                  "(2, 20.456, 1, 0.05);");
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
        => new(parser, configuration);

    protected override string TestTableName => "Users";
    protected override string TestOrdersTableName => "Orders";
    protected override string TestOrderDetailsTableName => "OrderDetails";
    protected override string TestOrderDetailsUnitPriceColumn => "UnitPrice";
    protected override string TestOrderDetailsQuantityColumn => "Quantity";
    protected override string TestOrderDetailsDiscountColumn => "Discount";
    protected override string TestSchemaName => "";
    protected override string TestOrdersUserIdColumn => "UserId";

    protected override string TableNotFoundErrorCode => "SQLITE_1";
    protected override string ColumnNotFoundErrorCode => "SQLITE_1";

    [Fact]
    public async Task ExecuteQueryAsync_ShouldEnforcePolicyRowLimit()
    {
        var json = await Strategy.ExecuteQueryAsync(
            new QueryDefinition { TableName = TestTableName },
            Fixture.ConnectionString,
            new SqlExecutionPolicy { QueryMaxRows = 1, QueryTimeoutSeconds = 30 },
            TestContext.Current.CancellationToken);

        var rows = JsonSerializer.Deserialize<List<JsonElement>>(json);
        Assert.NotNull(rows);
        Assert.Single(rows);
    }

    [Fact]
    public async Task ExecuteQueryAsync_ShouldPreserveRequestedLimitBelowPolicyMaximum()
    {
        var json = await Strategy.ExecuteQueryAsync(
            new QueryDefinition { TableName = TestTableName, Limit = 1 },
            Fixture.ConnectionString,
            new SqlExecutionPolicy { QueryMaxRows = 1000, QueryTimeoutSeconds = 30 },
            TestContext.Current.CancellationToken);

        var rows = JsonSerializer.Deserialize<List<JsonElement>>(json);
        Assert.NotNull(rows);
        Assert.Single(rows);
    }

    [Fact]
    public async Task ExecuteQueryAsync_ShouldUsePolicyMaximumWhenRequestedLimitIsHigher()
    {
        var json = await Strategy.ExecuteQueryAsync(
            new QueryDefinition { TableName = TestTableName, Limit = 1000 },
            Fixture.ConnectionString,
            new SqlExecutionPolicy { QueryMaxRows = 2, QueryTimeoutSeconds = 30 },
            TestContext.Current.CancellationToken);

        var rows = JsonSerializer.Deserialize<List<JsonElement>>(json);
        Assert.NotNull(rows);
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public async Task ExecuteQueryAsync_ShouldPreserveParsedModuloComparisonAndBooleanOperators()
    {
        var definition = SqlDefinitionParser.ParseQuery(
            "SELECT Age % 10 AS remainder, Age >= 30 AS is_senior, " +
            "Age >= 30 AND Active = TRUE AS matches FROM Users ORDER BY Id LIMIT 1");

        var json = await Strategy.ExecuteQueryAsync(
            definition,
            Fixture.ConnectionString,
            cancellationToken: TestContext.Current.CancellationToken);

        var rows = JsonSerializer.Deserialize<List<JsonElement>>(json);
        var row = Assert.Single(rows!);
        Assert.Equal(0, row.GetProperty("remainder").GetInt32());
        Assert.Equal(1, row.GetProperty("is_senior").GetInt32());
        Assert.Equal(1, row.GetProperty("matches").GetInt32());
    }

    [Fact]
    public async Task ExecuteDmlAsync_ShouldRejectDeleteWithoutWhere_WhenPolicyRequiresWhere()
    {
        var result = await Strategy.ExecuteDmlAsync(
            Fixture.ConnectionString,
            new DmlDefinition
            {
                Operation = DmlOperation.Delete,
                TableName = TestTableName
            },
            new SqlExecutionPolicy
            {
                RequireWhereForDelete = true,
                AllowFullTableDelete = false,
                DmlMaxAffectedRows = 100
            },
            TestContext.Current.CancellationToken);

        Assert.Contains("Security policy denied", result);

        await using var connection = new SqliteConnection(Fixture.ConnectionString);
        Assert.Equal(3, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Users"));
    }

    [Fact]
    public async Task ExecuteDmlAsync_ShouldRollback_WhenAffectedRowsExceedPolicy()
    {
        var result = await Strategy.ExecuteDmlAsync(
            Fixture.ConnectionString,
            new DmlDefinition
            {
                Operation = DmlOperation.Update,
                TableName = TestTableName,
                Values = [new NameValuePair { FieldName = "Active", Value = false }]
            },
            new SqlExecutionPolicy
            {
                RequireWhereForUpdate = false,
                AllowFullTableUpdate = true,
                DmlMaxAffectedRows = 1
            },
            TestContext.Current.CancellationToken);

        Assert.Contains("affectedRows=3 exceeds", result);

        await using var connection = new SqliteConnection(Fixture.ConnectionString);
        Assert.Equal(2, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Users WHERE Active = 1"));
    }

    [Fact]
    public async Task ParsedDml_WithKeywordInsideString_ShouldCompileAndDryRunWithoutChangingData()
    {
        var definition = SqlDefinitionParser.ParseDml(
            "UPDATE Users SET Name = 'where, set' WHERE Id = 1",
            SqlAgentToolType.Sqlite);

        var result = await Strategy.ExecuteDmlAsync(
            Fixture.ConnectionString,
            definition,
            new SqlExecutionPolicy
            {
                RequireWhereForUpdate = true,
                AllowFullTableUpdate = false,
                DmlMaxAffectedRows = 10
            },
            TestContext.Current.CancellationToken);

        Assert.Contains("Dry Run Result | affectedRows=1", result);
        Assert.Contains("Preview=### UPDATE preview", result);
        Assert.Contains("```diff", result);
        Assert.Contains("Table: Users", result);
        Assert.Contains("@@ Id = 1 @@", result);
        Assert.Contains("-Name: Alice", result);
        Assert.Contains("+Name: where, set", result);
        Assert.Contains("read-only preview did not execute", result);
        await using var connection = new SqliteConnection(Fixture.ConnectionString);
        Assert.Equal("Alice", await connection.ExecuteScalarAsync<string>("SELECT Name FROM Users WHERE Id = 1"));
    }

    [Fact]
    public async Task ExecuteDmlAsync_Preview_ShouldNotFireUpdateTriggers()
    {
        await using var connection = new SqliteConnection(Fixture.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync("""
            CREATE TRIGGER RejectPreviewWrites
            BEFORE UPDATE ON Users
            BEGIN
                SELECT RAISE(ABORT, 'an UPDATE trigger was executed');
            END;
            """);

        try
        {
            var result = await Strategy.ExecuteDmlAsync(
                Fixture.ConnectionString,
                new DmlDefinition
                {
                    Operation = DmlOperation.Update,
                    TableName = TestTableName,
                    Values = [new NameValuePair { FieldName = "Active", Value = false }],
                    WhereConditions =
                    [
                        new BasicWhereCondition { FieldName = "Id", Operator = "=", Value = 1 }
                    ]
                },
                new SqlExecutionPolicy
                {
                    RequireWhereForUpdate = true,
                    AllowFullTableUpdate = false,
                    DmlMaxAffectedRows = 10
                },
                TestContext.Current.CancellationToken);

            Assert.Contains("Dry Run Result | affectedRows=1", result);
            Assert.Contains("read-only preview did not execute", result);
        }
        finally
        {
            await connection.ExecuteAsync("DROP TRIGGER RejectPreviewWrites");
        }
    }

    [Fact]
    public override async Task GetSchemasAsync_ShouldReturnAvailableSchemas()
    {
        var schemas = await Strategy.GetSchemasAsync(Fixture.ConnectionString, TestContext.Current.CancellationToken);
        Assert.Single(schemas);
        Assert.Equal(["main"], schemas);
    }

    [Fact]
    public override async Task GetColumnsAsync_ShouldReturnColumnTypes()
    {
        await base.GetColumnsAsync_ShouldReturnColumnTypes();
        var columns = await Strategy.GetColumnsAsync(Fixture.ConnectionString, TestSchemaName, TestTableName, TestContext.Current.CancellationToken);
        Assert.Equal("INTEGER", columns.First(c => c.Column == "Id").Type, ignoreCase: true);
        Assert.Equal("TEXT", columns.First(c => c.Column == "Name").Type, ignoreCase: true);
    }


    protected override DmlDefinition CreateInsertDml() => new()
    {
        Operation = DmlOperation.Insert,
        TableName = TestTableName,
        Values = [
            new NameValuePair { FieldName = "Name", Value = "David" },
            new NameValuePair { FieldName = "Age", Value = 40 },
            new NameValuePair { FieldName = "Active", Value = true }
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
        var queryDef2 = new QueryDefinition { TableName = "Orders", SelectColumns = [new FieldSelectCondition { FieldName = "Amount" }] };

        var json = await Strategy.ExecuteQueryAsync(
            new QueryDefinition
            {
                TableName = "Users",
                SelectColumns = [new FieldSelectCondition { FieldName = "Name" }],
                CombineConditions = [new CombineCondition { Type = CombineType.Union, Query = queryDef2 }],
                Limit = 2,
                OrderByColumns = [new FieldOrderByCondition { FieldName = "Name", Direction = SortDirection.Desc }]
            }, Fixture.ConnectionString,
            cancellationToken: TestContext.Current.CancellationToken);

        var res = JsonSerializer.Deserialize<List<JsonElement>>(json);
        Assert.NotNull(res);
        Assert.Equal(2, res.Count);
    }

    [Fact]
    public async Task ExecuteQueryAsync_ShouldSupportParameterizedArithmeticConstants()
    {
        using var constantJson = JsonDocument.Parse("1");

        var json = await Strategy.ExecuteQueryAsync(
            new QueryDefinition
            {
                TableName = TestTableName,
                SelectColumns =
                [
                    new OperationSelectCondition
                    {
                        Left = new ConstantSelectCondition { Constant = constantJson.RootElement.Clone() },
                        Operator = ArithmeticOperator.Subtract,
                        Right = new FieldSelectCondition { FieldName = "Age" },
                        Alias = "Delta"
                    }
                ],
                WhereColumnsAndValues = [new BasicWhereCondition { FieldName = "Name", Operator = "=", Value = "Alice" }]
            },
            Fixture.ConnectionString,
            cancellationToken: TestContext.Current.CancellationToken);

        var rows = JsonSerializer.Deserialize<List<JsonElement>>(json);

        Assert.NotNull(rows);
        Assert.Single(rows);
        Assert.Equal(-29, rows[0].GetProperty("Delta").GetInt32());
    }

    [Fact]
    public async Task ExecuteQueryAsync_ShouldSupportFunctionExpressionsInGrouping()
    {
        var json = await Strategy.ExecuteQueryAsync(
            new QueryDefinition
            {
                TableName = TestTableName,
                SelectColumns =
                [
                    new FunctionSelectCondition
                    {
                        FunctionName = "SUBSTR",
                        Arguments =
                        [
                            new FieldSelectCondition { FieldName = "CreatedDate" },
                            new ConstantSelectCondition { Constant = 1 },
                            new ConstantSelectCondition { Constant = 4 }
                        ],
                        Alias = "YearPart"
                    },
                    new FunctionSelectCondition
                    {
                        FunctionName = "COUNT",
                        Arguments = [new FieldSelectCondition { FieldName = "Id" }],
                        Alias = "UserCount"
                    }
                ],
                WhereColumnsAndValues =
                [
                    new BasicWhereCondition { FieldName = "Name", Operator = "in", Values = ["Alice", "Bob", "Charlie"] }
                ],
                GroupByConditions =
                [
                    new FunctionGroupByCondition
                    {
                        FunctionName = "SUBSTR",
                        Arguments =
                        [
                            new FieldSelectCondition { FieldName = "CreatedDate" },
                            new ConstantSelectCondition { Constant = 1 },
                            new ConstantSelectCondition { Constant = 4 }
                        ]
                    }
                ],
                OrderByColumns =
                [
                    new FunctionOrderByCondition
                    {
                        FunctionName = "SUBSTR",
                        Arguments =
                        [
                            new FieldSelectCondition { FieldName = "CreatedDate" },
                            new ConstantSelectCondition { Constant = 1 },
                            new ConstantSelectCondition { Constant = 4 }
                        ],
                        Direction = SortDirection.Asc
                    }
                ],
            },
            Fixture.ConnectionString,
            cancellationToken: TestContext.Current.CancellationToken);

        var rows = JsonSerializer.Deserialize<List<JsonElement>>(json);

        Assert.NotNull(rows);
        Assert.Single(rows);
        Assert.Equal("2023", rows[0].GetProperty("YearPart").GetString());
        Assert.Equal(3, rows[0].GetProperty("UserCount").GetInt32());
    }

    [Fact]
    public async Task ExecuteQueryAsync_ShouldSupportNestedRoundWithNumericConstantArgument()
    {
        var json = await Strategy.ExecuteQueryAsync(
            new QueryDefinition
            {
                TableName = "orders",
                SelectColumns =
                [
                    new FunctionSelectCondition
                    {
                        Alias = "RoundedAverage",
                        FunctionName = "ROUND",
                        Arguments =
                        [
                            new FunctionSelectCondition
                            {
                                FunctionName = "AVG",
                                Arguments =
                                [
                                    new FieldSelectCondition { FieldName = "Amount" }
                                ]
                            },
                            new ConstantSelectCondition { Constant = 2 }
                        ]
                    }
                ]
            },
            Fixture.ConnectionString,
            cancellationToken: TestContext.Current.CancellationToken);

        var rows = JsonSerializer.Deserialize<List<JsonElement>>(json);

        Assert.NotNull(rows);
        Assert.Single(rows);
        Assert.Equal(133.33m, rows[0].GetProperty("RoundedAverage").GetDecimal());
    }

    [Fact]
    public async Task ExecuteQueryAsync_ShouldSupportAggregatedNestedArithmeticExpression()
    {
        var json = await Strategy.ExecuteQueryAsync(
            new QueryDefinition
            {
                TableName = "orders",
                SelectColumns =
                [
                    new FieldSelectCondition { FieldName = "UserId", Alias = "UserId" },
                    new FunctionSelectCondition
                    {
                        Alias = "Revenue",
                        FunctionName = "SUM",
                        Arguments =
                        [
                            new OperationSelectCondition
                            {
                                Left = new OperationSelectCondition
                                {
                                    Left = new FieldSelectCondition { FieldName = "Orders.Amount" },
                                    Operator = ArithmeticOperator.Multiply,
                                    Right = new ConstantSelectCondition { Constant = 1 }
                                },
                                Operator = ArithmeticOperator.Multiply,
                                Right = new OperationSelectCondition
                                {
                                    Left = new ConstantSelectCondition { Constant = 1 },
                                    Operator = ArithmeticOperator.Subtract,
                                    Right = new ConstantSelectCondition { Constant = 0.1m }
                                }
                            }
                        ]
                    }
                ],
                GroupByConditions =
                [
                    new FieldGroupByCondition { FieldName = "UserId" }
                ],
                OrderByColumns =
                [
                    new FieldOrderByCondition { FieldName = "UserId", Direction = SortDirection.Asc }
                ]
            },


            Fixture.ConnectionString,
            cancellationToken: TestContext.Current.CancellationToken);

        var rows = JsonSerializer.Deserialize<List<JsonElement>>(json);

        Assert.NotNull(rows);
        Assert.Equal(2, rows.Count);
        Assert.Equal(1, rows[0].GetProperty("UserId").GetInt32());
        Assert.Equal(315m, rows[0].GetProperty("Revenue").GetDecimal());
        Assert.Equal(2, rows[1].GetProperty("UserId").GetInt32());
        Assert.Equal(45m, rows[1].GetProperty("Revenue").GetDecimal());
    }

    [Fact]
    public async Task ExecuteQueryAsync_ShouldSupportTopLevelAndJoinAliases()
    {
        var json = await Strategy.ExecuteQueryAsync(
            new QueryDefinition
            {
                TableName = "Orders",
                Alias = "o",
                SelectColumns =
                [
                    new FieldSelectCondition { FieldName = "u.Name", Alias = "CustomerName" },
                    new FunctionSelectCondition
                    {
                        FunctionName = "SUM",
                        Arguments = [new FieldSelectCondition { FieldName = "o.Amount" }],
                        Alias = "TotalAmount"
                    }
                ],
                Joins =
                [
                    new JoinCondition
                    {
                        Table = "Users",
                        Alias = "u",
                        Type = JoinType.Inner,
                        OnConditions =
                        [
                            new ColumnCompareWhereCondition
                            {
                                LeftFieldName = "o.UserId",
                                Operator = "=",
                                RightFieldName = "u.Id"
                            }
                        ]
                    }
                ],
                GroupByConditions =
                [
                    new FieldGroupByCondition { FieldName = "u.Name" }
                ],
                OrderByColumns =
                [
                    new FieldOrderByCondition { FieldName = "TotalAmount", Direction = SortDirection.Desc }
                ]
            },
            Fixture.ConnectionString,
            cancellationToken: TestContext.Current.CancellationToken);

        var rows = JsonSerializer.Deserialize<List<JsonElement>>(json);

        Assert.NotNull(rows);
        Assert.Equal(2, rows.Count);
        Assert.Equal("Alice", rows[0].GetProperty("CustomerName").GetString());
        Assert.Equal(350m, rows[0].GetProperty("TotalAmount").GetDecimal());
        Assert.Equal("Bob", rows[1].GetProperty("CustomerName").GetString());
        Assert.Equal(50m, rows[1].GetProperty("TotalAmount").GetDecimal());
    }

    [Fact]
    public async Task ExecuteQueryAsync_ShouldSupportCrossJoinWithoutOnConditions()
    {
        var qd = new QueryDefinition
        {
            TableName = "Users",
            Alias = "u",
            CteConditions =
            [
                new CteCondition
                {
                    CteAliasName = "SystemMax",
                    Query = new QueryDefinition
                    {
                        TableName = "Orders",
                        SelectColumns =
                        [
                            new FunctionSelectCondition
                            {
                                FunctionName = "MAX",
                                Arguments = [new FieldSelectCondition { FieldName = "OrderDate" }],
                                Alias = "MaxOrderDate"
                            }
                        ]
                    }
                }
            ],
            Joins =
            [
                new JoinCondition
                {
                    Table = "SystemMax",
                    Alias = "sm",
                    Type = JoinType.Cross,
                    OnConditions = []
                }
            ],
            SelectColumns =
            [
                new FieldSelectCondition { FieldName = "u.Name", Alias = "Name" },
                new FieldSelectCondition { FieldName = "sm.MaxOrderDate", Alias = "MaxOrderDate" }
            ],
            OrderByColumns =
            [
                new FieldOrderByCondition { FieldName = "u.Id", Direction = SortDirection.Asc }
            ]
        };

        ValidateQuery(qd);

        var json = await Strategy.ExecuteQueryAsync(
            qd,
            Fixture.ConnectionString,
            cancellationToken: TestContext.Current.CancellationToken);

        var rows = JsonSerializer.Deserialize<List<JsonElement>>(json);

        Assert.NotNull(rows);
        Assert.Equal(3, rows.Count);
        Assert.Equal("Alice", rows[0].GetProperty("Name").GetString());
        Assert.Equal("2023-03-20", rows[0].GetProperty("MaxOrderDate").GetString());
    }

    [Fact]
    public async Task ExecuteQueryAsync_ShouldSupportArithmeticInFunctionArguments()
    {
        var json = await Strategy.ExecuteQueryAsync(
            new QueryDefinition
            {
                TableName = "orders",
                SelectColumns =
                [
                    new FunctionSelectCondition
                    {
                        Alias = "TotalWithTax",
                        FunctionName = "SUM",
                        Arguments =
                        [
                            new OperationSelectCondition
                            {
                                Left = new FieldSelectCondition { FieldName = "Amount" },
                                Operator = ArithmeticOperator.Multiply,
                                Right = new ConstantSelectCondition { Constant = 1.05m }
                            }
                        ]
                    }
                ]
            },
            Fixture.ConnectionString,
            cancellationToken: TestContext.Current.CancellationToken);

        var rows = JsonSerializer.Deserialize<List<JsonElement>>(json);

        Assert.NotNull(rows);
        Assert.Single(rows);
        // All orders: 150.0 + 200.0 + 50.0 = 400.0. 400.0 * 1.05 = 420.0
        Assert.Equal(420m, rows[0].GetProperty("TotalWithTax").GetDecimal());
    }
}
