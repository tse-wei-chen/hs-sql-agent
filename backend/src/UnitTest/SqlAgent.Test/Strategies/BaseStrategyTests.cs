using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Moq;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Interfaces;
using SqlAgent.Service.Models;
using SqlAgent.Service.Services;
using SqlAgent.Service.Strategies;
using SqlAgent.Service.Validation;
using Xunit;

namespace SqlAgent.Test.Strategies;

public interface IDbFixture : IAsyncLifetime
{
    string ConnectionString { get; }
}

public abstract class BaseStrategyTests<TStrategy, TFixture> : IClassFixture<TFixture>
    where TStrategy : ISqlStrategy
    where TFixture : class, IDbFixture
{
    protected readonly TFixture Fixture;
    protected readonly TStrategy Strategy;

    protected BaseStrategyTests(TFixture fixture)
    {
        Fixture = fixture;
        var configMock = new Mock<IConfiguration>();
        configMock.Setup(c => c["McpKeySettings:HmacSecretKey"]).Returns("TestSecretKey12345678901234567890");
        Strategy = CreateStrategy(new QueryValueParserService(), configMock.Object);
    }

    protected abstract TStrategy CreateStrategy(IQueryValueParserService parser, IConfiguration configuration);

    protected abstract string TestTableName { get; }
    protected abstract string TestOrdersTableName { get; }
    protected virtual string TestOrderDetailsTableName => "order_details";
    protected virtual string TestOrderDetailsUnitPriceColumn => "unit_price";
    protected virtual string TestOrderDetailsQuantityColumn => "quantity";
    protected virtual string TestOrderDetailsDiscountColumn => "discount";
    protected abstract string TestSchemaName { get; }
    protected virtual string TestOrdersUserIdColumn => "user_id";

    [Fact]
    public virtual async Task GetTablesAsync_ShouldReturnTables()
    {
        var tables = await Strategy.GetTablesAsync(Fixture.ConnectionString, TestSchemaName, TestContext.Current.CancellationToken);
        Assert.Contains(TestTableName, tables, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public virtual async Task GetSchemasAsync_ShouldReturnAvailableSchemas()
    {
        var schemas = await Strategy.GetSchemasAsync(Fixture.ConnectionString, TestContext.Current.CancellationToken);
        Assert.Contains(TestSchemaName, schemas, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public virtual async Task GetColumnsAsync_ShouldReturnColumnTypes()
    {
        var columns = await Strategy.GetColumnsAsync(Fixture.ConnectionString, TestSchemaName, TestTableName, TestContext.Current.CancellationToken);
        Assert.NotEmpty(columns);
    }

    [Fact]
    public virtual async Task ExecuteQueryAsync_ShouldReturnValidJson()
    {
        var qd = new QueryDefinition
        {
            TableName = TestTableName,
            Limit = 1
        };
        ValidateQuery(qd);
        var json = await Strategy.ExecuteQueryAsync(qd,
            Fixture.ConnectionString,
            cancellationToken: TestContext.Current.CancellationToken);

        var res = JsonSerializer.Deserialize<List<JsonElement>>(json);
        Assert.NotNull(res);
        Assert.NotEmpty(res);
    }

    [Fact]
    public virtual async Task ExecuteQueryAsync_ShouldReturnDbError_WhenTableNotFound()
    {
        var qd = new QueryDefinition
        {
            TableName = "NON_EXISTENT_TABLE_HS"
        };
        ValidateQuery(qd);
        var ex = await Assert.ThrowsAsync<Exception>(() => Strategy.ExecuteQueryAsync(qd,
            Fixture.ConnectionString,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains($"code={TableNotFoundErrorCode}", ex.Message);
    }

    [Fact]
    public virtual async Task ExecuteQueryAsync_ShouldReturnDbError_WhenColumnNotFound()
    {
        var qd = new QueryDefinition
        {
            TableName = TestTableName,
            SelectColumns = [new FieldSelectCondition { FieldName = $"{TestTableName}.NON_EXISTENT_COL_HS" }]
        };
        ValidateQuery(qd);
        var ex = await Assert.ThrowsAsync<Exception>(() => Strategy.ExecuteQueryAsync(qd,
            Fixture.ConnectionString,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains($"code={ColumnNotFoundErrorCode}", ex.Message);
    }

    [Fact]
    public virtual async Task ExecuteDmlAsync_ShouldPerformValidInsert()
    {
        var dml = CreateInsertDml();
        ValidateDml(dml);
        var dryRun = await Strategy.ExecuteDmlAsync(Fixture.ConnectionString, dml, TestContext.Current.CancellationToken);

        var tokenStart = dryRun.IndexOf("TokenRequired=");
        if (tokenStart == -1)
        {
            Assert.Contains("Success", dryRun);
            return;
        }

        var start = tokenStart + 14;
        var end = dryRun.IndexOf(" |", start);
        dml.ConfirmToken = dryRun[start..end];

        var final = await Strategy.ExecuteDmlAsync(Fixture.ConnectionString, dml, TestContext.Current.CancellationToken);
        Assert.Contains("Success", final);

        // Cleanup: delete the inserted record to avoid polluting shared fixture DB
        await CleanupInsertedDmlRecord(dml);
    }

    [Fact]
    public async Task ExecuteDmlAsync_ShouldBindConfirmationTokenToCompleteDefinition()
    {
        var approvedDefinition = CreateInsertDml();
        ValidateDml(approvedDefinition);
        var dryRun = await Strategy.ExecuteDmlAsync(
            Fixture.ConnectionString,
            approvedDefinition,
            TestContext.Current.CancellationToken);
        var tokenStart = dryRun.IndexOf("TokenRequired=", StringComparison.Ordinal);
        if (tokenStart < 0) return;
        var start = tokenStart + 14;
        var end = dryRun.IndexOf(" |", start, StringComparison.Ordinal);

        var differentDefinition = CreateInsertDml();
        var mutableValue = differentDefinition.Values?.FirstOrDefault(value => value.Value is string);
        Assert.NotNull(mutableValue);
        mutableValue.Value = $"{mutableValue.Value}-different-{Guid.NewGuid():N}";
        differentDefinition.ConfirmToken = dryRun[start..end];

        var result = await Strategy.ExecuteDmlAsync(
            Fixture.ConnectionString,
            differentDefinition,
            TestContext.Current.CancellationToken);

        Assert.StartsWith("Dry Run Result", result);
    }

    private async Task CleanupInsertedDmlRecord(DmlDefinition dml)
    {
        if (dml.Operation != DmlOperation.Insert || dml.Values == null || dml.Values.Count == 0)
            return;

        var nameValue = dml.Values.FirstOrDefault(v =>
            string.Equals(v.FieldName, "Name", StringComparison.OrdinalIgnoreCase));
        if (nameValue == null)
            return;

        var deleteDml = new DmlDefinition
        {
            Operation = DmlOperation.Delete,
            TableName = dml.TableName,
            WhereConditions =
            [
                new BasicWhereCondition
                {
                    FieldName = nameValue.FieldName,
                    Operator = "=",
                    Value = nameValue.Value
                }
            ]
        };

        var deleteDryRun = await Strategy.ExecuteDmlAsync(Fixture.ConnectionString, deleteDml, TestContext.Current.CancellationToken);
        var deleteTokenStart = deleteDryRun.IndexOf("TokenRequired=");
        if (deleteTokenStart == -1)
            return;

        var deleteStart = deleteTokenStart + 14;
        var deleteEnd = deleteDryRun.IndexOf(" |", deleteStart);
        deleteDml.ConfirmToken = deleteDryRun[deleteStart..deleteEnd];
        await Strategy.ExecuteDmlAsync(Fixture.ConnectionString, deleteDml, TestContext.Current.CancellationToken);
    }

    [Fact]
    public virtual async Task ExecuteQueryAsync_ShouldSupportFullQueryDefinitionStructure()
    {
        var json = await Strategy.ExecuteQueryAsync(
            new QueryDefinition
            {
                CteConditions =
                [
                    new CteCondition
                    {
                        CteAliasName = "active_users",
                        Query = new QueryDefinition
                        {
                            TableName = TestTableName,
                            SelectColumns =
                            [
                                new FieldSelectCondition { FieldName = "id" }
                            ],
                            WhereColumnsAndValues =
                            [
                                new BasicWhereCondition { FieldName = "age", Operator = ">", Value = 0 }
                            ]
                        }
                    }
                ],
                Alias = "u",
                FromQuery = new QueryDefinition
                {
                    TableName = TestTableName,
                    WhereColumnsAndValues =
                    [
                        new BasicWhereCondition { FieldName = "age", Operator = ">", Value = 0 }
                    ]
                },
                Distinct = true,
                Joins =
                [
                    new JoinCondition
                    {
                        Table = TestOrdersTableName,
                        Alias = "o",
                        Type = JoinType.Left,
                        OnConditions =
                        [
                            new ColumnCompareWhereCondition
                            {
                                LeftFieldName = "u.id",
                                Operator = "=",
                                RightFieldName = $"o.{TestOrdersUserIdColumn}"
                            }
                        ]
                    }
                ],
                SelectColumns =
                [
                    new FieldSelectCondition { FieldName = "u.id", Alias = "user_id" },
                    new FieldSelectCondition { FieldName = "u.name", Alias = "username" },
                    new FunctionSelectCondition
                    {
                        FunctionName = "COUNT",
                        Arguments = [new FieldSelectCondition { FieldName = "o.id" }],
                        Alias = "order_count"
                    },
                    new OperationSelectCondition
                    {
                        Left = new FieldSelectCondition { FieldName = "u.age" },
                        Operator = ArithmeticOperator.Add,
                        Right = new ConstantSelectCondition { Constant = 0 },
                        Alias = "age_check"
                    },
                    new ConstantSelectCondition { Constant = "active_user", Alias = "user_type" },
                    new CaseWhenSelectCondition
                    {
                        CaseWhen =
                        [
                            new CaseWhenClause
                            {
                                Condition = new BasicWhereCondition
                                {
                                    FieldName = "u.age",
                                    Operator = ">",
                                    Value = 30
                                },
                                Value = "senior"
                            }
                        ],
                        ElseValue = "junior",
                        Alias = "age_group"
                    }
                ],
                WhereColumnsAndValues =
                [
                    new GroupWhereCondition
                    {
                        Groups =
                        [
                            new BasicWhereCondition { FieldName = "o.amount", Operator = ">", Value = 0 },
                            new BasicWhereCondition { FieldName = "o.id", Operator = "isnull", Value = null, IsOr = true }
                        ]
                    }
                ],
                GroupByConditions =
                [
                    new FieldGroupByCondition { FieldName = "u.id" },
                    new FieldGroupByCondition { FieldName = "u.name" },
                    new FieldGroupByCondition { FieldName = "u.age" }
                ],
                HavingConditions =
                [
                    new FunctionHavingCondition
                    {
                        LeftFunction = new SqlFunctionCondition
                        {
                            FunctionName = "COUNT",
                            Arguments = [new FieldSelectCondition { FieldName = "o.id" }]
                        },
                        Operator = ">=",
                        Value = 0
                    }
                ],
                OrderByColumns =
                [
                    new FieldOrderByCondition { FieldName = "order_count", Direction = SortDirection.Desc }
                ],
                Limit = 5
            },
            Fixture.ConnectionString,
            cancellationToken: TestContext.Current.CancellationToken);

        var rows = JsonSerializer.Deserialize<List<JsonElement>>(json);
        Assert.NotNull(rows);
        Assert.NotEmpty(rows);
        Assert.True(rows.Count <= 5, $"Expected ?? rows, got {rows.Count}");

        foreach (var row in rows)
        {
            Assert.True(row.TryGetProperty("user_id", out _));
            Assert.True(row.TryGetProperty("username", out _));
            Assert.True(row.TryGetProperty("order_count", out _));
            Assert.True(row.TryGetProperty("age_check", out _));
            Assert.True(row.TryGetProperty("user_type", out _));
            Assert.True(row.TryGetProperty("age_group", out _));
        }
    }

    [Fact]
    public virtual async Task ExecuteQueryAsync_ShouldSupportSubQueryWhereCondition()
    {
        var json = await Strategy.ExecuteQueryAsync(
            new QueryDefinition
            {
                TableName = TestTableName,
                SelectColumns =
                [
                    new FieldSelectCondition { FieldName = "name", Alias = "uname" }
                ],
                WhereColumnsAndValues =
                [
                    new SubQueryWhereCondition
                    {
                        FieldName = "id",
                        Operator = "IN",
                        SubQuery = new QueryDefinition
                        {
                            TableName = TestOrdersTableName,
                            SelectColumns =
                            [
                                new FieldSelectCondition { FieldName = TestOrdersUserIdColumn }
                            ],
                            WhereColumnsAndValues =
                            [
                                new BasicWhereCondition { FieldName = "amount", Operator = ">", Value = 100 }
                            ]
                        }
                    }
                ],
                OrderByColumns =
                [
                    new FieldOrderByCondition { FieldName = "name", Direction = SortDirection.Asc }
                ]
            },
            Fixture.ConnectionString,
            cancellationToken: TestContext.Current.CancellationToken);

        var rows = JsonSerializer.Deserialize<List<JsonElement>>(json);
        Assert.NotNull(rows);
        Assert.NotEmpty(rows);
        // Users with orders > 100: Alice (userId=1 has orders 150, 200)
        // Bob's order is 50, Charlie has no orders
        Assert.Single(rows);
        Assert.Equal("Alice", rows[0].GetProperty("uname").GetString());
    }

    [Fact]
    public virtual async Task ExecuteQueryAsync_ShouldSupportWhereNotIsOrIsNot()
    {
        var json = await Strategy.ExecuteQueryAsync(
            new QueryDefinition
            {
                TableName = TestTableName,
                SelectColumns =
                [
                    new FieldSelectCondition { FieldName = "name", Alias = "uname" }
                ],
                WhereColumnsAndValues =
                [
                    new BasicWhereCondition { FieldName = "name", Operator = "=", Value = "Alice" },
                    new BasicWhereCondition { FieldName = "name", Operator = "=", Value = "Bob", IsOr = true },
                    new BasicWhereCondition { FieldName = "name", Operator = "LIKE", Value = "C%", IsNot = true }
                ],
                OrderByColumns =
                [
                    new FieldOrderByCondition { FieldName = "name", Direction = SortDirection.Asc }
                ]
            },
            Fixture.ConnectionString,
            cancellationToken: TestContext.Current.CancellationToken);

        var rows = JsonSerializer.Deserialize<List<JsonElement>>(json);
        Assert.NotNull(rows);
        Assert.NotEmpty(rows);
        // WHERE (name = 'Alice' OR name = 'Bob') AND NOT name LIKE 'C%'
        // Alice: matches 'Alice' ????included
        // Bob: matches 'Bob' ????included
        // Charlie: doesn't match 'Alice' or 'Bob' ??excluded (even though NOT LIKE 'C%' would be true)
        Assert.Equal(2, rows.Count);
        Assert.Equal("Alice", rows[0].GetProperty("uname").GetString());
        Assert.Equal("Bob", rows[1].GetProperty("uname").GetString());
    }

    [Fact]
    public virtual async Task ExecuteQueryAsync_ShouldSupportSubQuerySelectCondition()
    {
        var json = await Strategy.ExecuteQueryAsync(
            new QueryDefinition
            {
                TableName = TestTableName,
                Alias = "u",
                SelectColumns =
                [
                    new FieldSelectCondition { FieldName = "u.name", Alias = "uname" },
                    new SubQuerySelectCondition
                    {
                        TableName = TestOrdersTableName,
                        SelectColumns =
                        [
                            new FunctionSelectCondition
                            {
                                FunctionName = "COUNT",
                                Arguments = [new FieldSelectCondition { FieldName = "id" }]
                            }
                        ],
                        WhereColumnsAndValues =
                        [
                            new ColumnCompareWhereCondition
                            {
                                LeftFieldName = TestOrdersUserIdColumn,
                                Operator = "=",
                                RightFieldName = "u.id"
                            }
                        ],
                        Alias = "order_count"
                    }
                ],
                OrderByColumns =
                [
                    new FieldOrderByCondition { FieldName = "u.name", Direction = SortDirection.Asc }
                ]
            },
            Fixture.ConnectionString,
            cancellationToken: TestContext.Current.CancellationToken);

        var rows = JsonSerializer.Deserialize<List<JsonElement>>(json);
        Assert.NotNull(rows);
        Assert.NotEmpty(rows);
        // Alice has 2 orders, Bob has 1, Charlie has 0
        Assert.True(rows[0].TryGetProperty("order_count", out _));
    }

    [Fact]
    public virtual async Task ExecuteQueryAsync_ShouldSupportOffsetWithoutLimit()
    {
        var json = await Strategy.ExecuteQueryAsync(
            new QueryDefinition
            {
                TableName = TestTableName,
                SelectColumns =
                [
                    new FieldSelectCondition { FieldName = "name", Alias = "uname" }
                ],
                OrderByColumns =
                [
                    new FieldOrderByCondition { FieldName = "name", Direction = SortDirection.Asc }
                ],
                Offset = 1
            },
            Fixture.ConnectionString,
            cancellationToken: TestContext.Current.CancellationToken);

        var rows = JsonSerializer.Deserialize<List<JsonElement>>(json);
        Assert.NotNull(rows);
        // Skip 1: Bob (sorted: Alice, Bob, Charlie ??skip Alice)
        Assert.Equal(2, rows.Count);
        Assert.Equal("Bob", rows[0].GetProperty("uname").GetString());
    }

    [Fact]
    public virtual async Task ExecuteQueryAsync_ShouldSupportExistsSubQueryWhereCondition()
    {
        var json = await Strategy.ExecuteQueryAsync(
            new QueryDefinition
            {
                TableName = TestTableName,
                SelectColumns =
                [
                    new FieldSelectCondition { FieldName = "name", Alias = "uname" }
                ],
                WhereColumnsAndValues =
                [
                    new SubQueryWhereCondition
                    {
                        Operator = "EXISTS",
                        SubQuery = new QueryDefinition
                        {
                            TableName = TestOrdersTableName,
                            WhereColumnsAndValues =
                            [
                                new ColumnCompareWhereCondition
                                {
                                    LeftFieldName = TestOrdersUserIdColumn,
                                    Operator = "=",
                                    RightFieldName = $"{TestTableName}.id"
                                }
                            ]
                        }
                    }
                ],
                OrderByColumns =
                [
                    new FieldOrderByCondition { FieldName = "name", Direction = SortDirection.Asc }
                ]
            },
            Fixture.ConnectionString,
            cancellationToken: TestContext.Current.CancellationToken);

        var rows = JsonSerializer.Deserialize<List<JsonElement>>(json);
        Assert.NotNull(rows);
        // EXISTS: users who have at least one order ??Alice (id=1) and Bob (id=2)
        // Charlie (id=3) has no orders ??excluded
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public virtual async Task ExecuteQueryAsync_ShouldSupportBetweenInHaving()
    {
        var json = await Strategy.ExecuteQueryAsync(
            new QueryDefinition
            {
                TableName = TestTableName,
                SelectColumns =
                [
                    new FieldSelectCondition { FieldName = "name", Alias = "uname" },
                    new FieldSelectCondition { FieldName = "age", Alias = "age" }
                ],
                WhereColumnsAndValues =
                [
                    new BasicWhereCondition { FieldName = "age", Operator = "between", Value = new object[] { 25, 35 } }
                ],
                OrderByColumns =
                [
                    new FieldOrderByCondition { FieldName = "name", Direction = SortDirection.Asc }
                ]
            },
            Fixture.ConnectionString,
            cancellationToken: TestContext.Current.CancellationToken);

        var rows = JsonSerializer.Deserialize<List<JsonElement>>(json);
        Assert.NotNull(rows);
        // age BETWEEN 25 AND 35 ??Alice (30), Bob (25), Charlie (35)
        Assert.Equal(3, rows.Count);
    }

    [Fact]
    public virtual async Task ExecuteQueryAsync_ShouldSupportGroupHavingCondition()
    {
        var json = await Strategy.ExecuteQueryAsync(
            new QueryDefinition
            {
                TableName = TestOrdersTableName,
                SelectColumns =
                [
                    new FieldSelectCondition { FieldName = TestOrdersUserIdColumn, Alias = "uid" },
                    new FunctionSelectCondition
                    {
                        FunctionName = "COUNT",
                        Arguments = [new FieldSelectCondition { FieldName = "id" }],
                        Alias = "cnt"
                    }
                ],
                GroupByConditions =
                [
                    new FieldGroupByCondition { FieldName = TestOrdersUserIdColumn }
                ],
                HavingConditions =
                [
                    new GroupHavingCondition
                    {
                        Groups =
                        [
                            new FunctionHavingCondition
                            {
                                LeftFunction = new SqlFunctionCondition
                                {
                                    FunctionName = "COUNT",
                                    Arguments = [new FieldSelectCondition { FieldName = "id" }]
                                },
                                Operator = ">=",
                                Value = 1
                            }
                        ]
                    }
                ],
                OrderByColumns =
                [
                    new FieldOrderByCondition { FieldName = "uid", Direction = SortDirection.Asc }
                ]
            },
            Fixture.ConnectionString,
            cancellationToken: TestContext.Current.CancellationToken);

        var rows = JsonSerializer.Deserialize<List<JsonElement>>(json);
        Assert.NotNull(rows);
        Assert.NotEmpty(rows);
        // HAVING (COUNT(id) >= 1) ??all order groups pass (each user has at least 1 order)
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public virtual async Task ExecuteQueryAsync_ShouldSupportRoundedAggregatedNestedArithmeticExpression()
    {
        const string alias = "total_sales";
        var tableAlias = "od";

        var json = await Strategy.ExecuteQueryAsync(
            new QueryDefinition
            {
                TableName = TestOrderDetailsTableName,
                Alias = tableAlias,
                SelectColumns =
                [
                    new FunctionSelectCondition
                    {
                        Alias = alias,
                        FunctionName = "ROUND",
                        Arguments =
                        [
                            new FunctionSelectCondition
                            {
                                FunctionName = "SUM",
                                Arguments =
                                [
                                    new OperationSelectCondition
                                    {
                                        Left = new OperationSelectCondition
                                        {
                                            Left = new FieldSelectCondition { FieldName = $"{tableAlias}.{TestOrderDetailsUnitPriceColumn}" },
                                            Operator = ArithmeticOperator.Multiply,
                                            Right = new FieldSelectCondition { FieldName = $"{tableAlias}.{TestOrderDetailsQuantityColumn}" }
                                        },
                                        Operator = ArithmeticOperator.Multiply,
                                        Right = new OperationSelectCondition
                                        {
                                            Left = new ConstantSelectCondition { Constant = 1 },
                                            Operator = ArithmeticOperator.Subtract,
                                            Right = new FieldSelectCondition { FieldName = $"{tableAlias}.{TestOrderDetailsDiscountColumn}" }
                                        }
                                    }
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
        Assert.True(TryGetPropertyIgnoreCase(rows[0], alias, out var totalSales), $"Expected property '{alias}' in result: {rows[0]}");
        Assert.InRange(totalSales.GetDecimal(), 37.65m, 37.66m);
    }

    protected static void ValidateQuery(QueryDefinition qd)
    {
        var errors = DefinitionValidator.Validate(qd);
        Assert.True(errors.Count == 0, $"QueryDefinition validation failed:\n" + string.Join("\n", errors));
    }

    protected static void ValidateDml(DmlDefinition dml)
    {
        var errors = DefinitionValidator.Validate(dml);
        Assert.True(errors.Count == 0, $"DmlDefinition validation failed:\n" + string.Join("\n", errors));
    }

    protected abstract string TableNotFoundErrorCode { get; }
    protected abstract string ColumnNotFoundErrorCode { get; }
    protected abstract DmlDefinition CreateInsertDml();

    private static bool TryGetPropertyIgnoreCase(JsonElement row, string propertyName, out JsonElement value)
    {
        foreach (var property in row.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
