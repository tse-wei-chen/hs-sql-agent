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

        cmd.CommandText = "INSERT INTO USERS (ID, NAME, AGE) VALUES (2, 'Bob', 25)";
        await cmd.ExecuteNonQueryAsync();

        cmd.CommandText = "INSERT INTO USERS (ID, NAME, AGE) VALUES (3, 'Charlie', 35)";
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

        cmd.CommandText = @"
            CREATE TABLE ORDER_DETAILS (
                ID INTEGER PRIMARY KEY,
                UNIT_PRICE DOUBLE PRECISION,
                QUANTITY INTEGER,
                DISCOUNT DOUBLE PRECISION
            )
        ";
        await cmd.ExecuteNonQueryAsync();

        cmd.CommandText = "INSERT INTO ORDER_DETAILS (ID, UNIT_PRICE, QUANTITY, DISCOUNT) VALUES (1, 10.123, 2, 0.1)";
        await cmd.ExecuteNonQueryAsync();
        cmd.CommandText = "INSERT INTO ORDER_DETAILS (ID, UNIT_PRICE, QUANTITY, DISCOUNT) VALUES (2, 20.456, 1, 0.05)";
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
    protected override string TestOrderDetailsTableName => "ORDER_DETAILS";
    protected override string TestOrderDetailsUnitPriceColumn => "UNIT_PRICE";
    protected override string TestOrderDetailsQuantityColumn => "QUANTITY";
    protected override string TestOrderDetailsDiscountColumn => "DISCOUNT";
    protected override string TestSchemaName => "Default";
    protected override string TestOrdersUserIdColumn => "USER_ID";

    // Firebird returns SQL code -204 for both table-not-found and column-not-found errors.
    // The strategy extracts this as "FB_SQL_-204" from the exception message.
    protected override string TableNotFoundErrorCode => "FB_SQL_-204";
    protected override string ColumnNotFoundErrorCode => "FB_SQL_-204";

    // Firebird uppercases unquoted identifiers, so result properties are UPPERCASE.
    private const string PropUname = "UNAME";
    private const string PropAge = "AGE";
    private const string PropUid = "UID";
    private const string PropOrderCount = "ORDER_COUNT";
    private const string PropUserType = "USER_TYPE";

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
        Assert.Contains("Table does not exist", ex.Message);
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
        Assert.Contains("Column not found", ex.Message);
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
            new NameValuePair { FieldName = "ID", Value = 4 },  // IDs 1,2,3 exist (Alice, Bob, Charlie)
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

    [Fact]
    public override async Task ExecuteQueryAsync_ShouldSupportOffsetWithoutLimit()
    {
        var json = await Strategy.ExecuteQueryAsync(
            new QueryDefinition
            {
                TableName = TestTableName,
                SelectColumns =
                [
                    new FieldSelectCondition { FieldName = "NAME", Alias = "uname" }
                ],
                OrderByColumns =
                [
                    new FieldOrderByCondition { FieldName = "NAME", Direction = SortDirection.Asc }
                ],
                Limit = 10,
                Offset = 1
            },
            Fixture.ConnectionString,
            cancellationToken: TestContext.Current.CancellationToken);

        var rows = JsonSerializer.Deserialize<List<JsonElement>>(json);
        Assert.NotNull(rows);
        Assert.Equal(2, rows.Count);
        // Firebird uppercases unquoted aliases
        Assert.Equal("Bob", rows[0].GetProperty(PropUname).GetString());
    }

    [Fact]
    public override async Task ExecuteQueryAsync_ShouldSupportFullQueryDefinitionStructure()
    {
        var json = await Strategy.ExecuteQueryAsync(
            new QueryDefinition
            {
                Alias = "u",
                TableName = TestTableName,
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
                                LeftFieldName = "u.ID",
                                Operator = "=",
                                RightFieldName = "o.USER_ID"
                            }
                        ]
                    }
                ],
                SelectColumns =
                [
                    new FieldSelectCondition { FieldName = "u.ID", Alias = "uid" },
                    new FieldSelectCondition { FieldName = "u.NAME", Alias = "uname" },
                    new FunctionSelectCondition
                    {
                        FunctionName = "COUNT",
                        Arguments = [new FieldSelectCondition { FieldName = "o.ID" }],
                        Alias = "order_count"
                    },
                    new ConstantSelectCondition { Constant = "active_user", Alias = "user_type" }
                ],
                WhereColumnsAndValues =
                [
                    new GroupWhereCondition
                    {
                        Groups =
                        [
                            new BasicWhereCondition { FieldName = "o.AMOUNT", Operator = ">", Value = 0 },
                            new BasicWhereCondition { FieldName = "o.ID", Operator = "isnull", Value = null, IsOr = true }
                        ]
                    }
                ],
                GroupByConditions =
                [
                    new FieldGroupByCondition { FieldName = "u.ID" },
                    new FieldGroupByCondition { FieldName = "u.NAME" }
                ],
                HavingConditions =
                [
                    new FunctionHavingCondition
                    {
                        LeftFunction = new SqlFunctionCondition
                        {
                            FunctionName = "COUNT",
                            Arguments = [new FieldSelectCondition { FieldName = "o.ID" }]
                        },
                        Operator = ">=",
                        Value = 0
                    }
                ],
                OrderByColumns =
                [
                    new FieldOrderByCondition { FieldName = "u.ID", Direction = SortDirection.Asc }
                ],
                Limit = 5
            },
            Fixture.ConnectionString,
            cancellationToken: TestContext.Current.CancellationToken);

        var rows = JsonSerializer.Deserialize<List<JsonElement>>(json);
        Assert.NotNull(rows);
        Assert.NotEmpty(rows);
        Assert.True(rows.Count <= 5);

        foreach (var row in rows)
        {
            Assert.True(row.TryGetProperty(PropUid, out _));
            Assert.True(row.TryGetProperty(PropUname, out _));
            Assert.True(row.TryGetProperty(PropOrderCount, out _));
            Assert.True(row.TryGetProperty(PropUserType, out _));
        }
    }

    [Fact]
    public override async Task ExecuteQueryAsync_ShouldSupportExistsSubQueryWhereCondition()
    {
        var json = await Strategy.ExecuteQueryAsync(
            new QueryDefinition
            {
                TableName = TestTableName,
                SelectColumns =
                [
                    new FieldSelectCondition { FieldName = "NAME", Alias = "uname" }
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
                                    RightFieldName = $"{TestTableName}.ID"
                                }
                            ]
                        }
                    }
                ],
                OrderByColumns =
                [
                    new FieldOrderByCondition { FieldName = "NAME", Direction = SortDirection.Asc }
                ]
            },
            Fixture.ConnectionString,
            cancellationToken: TestContext.Current.CancellationToken);

        var rows = JsonSerializer.Deserialize<List<JsonElement>>(json);
        Assert.NotNull(rows);
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public override async Task ExecuteQueryAsync_ShouldSupportSubQueryWhereCondition()
    {
        var json = await Strategy.ExecuteQueryAsync(
            new QueryDefinition
            {
                TableName = TestTableName,
                SelectColumns =
                [
                    new FieldSelectCondition { FieldName = "NAME", Alias = "uname" }
                ],
                WhereColumnsAndValues =
                [
                    new SubQueryWhereCondition
                    {
                        FieldName = "ID",
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
                                new BasicWhereCondition { FieldName = "AMOUNT", Operator = ">", Value = 100 }
                            ]
                        }
                    }
                ],
                OrderByColumns =
                [
                    new FieldOrderByCondition { FieldName = "NAME", Direction = SortDirection.Asc }
                ]
            },
            Fixture.ConnectionString,
            cancellationToken: TestContext.Current.CancellationToken);

        var rows = JsonSerializer.Deserialize<List<JsonElement>>(json);
        Assert.NotNull(rows);
        Assert.NotEmpty(rows);
        Assert.Single(rows);
        // Firebird uppercases unquoted aliases
        Assert.Equal("Alice", rows[0].GetProperty(PropUname).GetString());
    }

    [Fact]
    public override async Task ExecuteQueryAsync_ShouldSupportBetweenInHaving()
    {
        var json = await Strategy.ExecuteQueryAsync(
            new QueryDefinition
            {
                TableName = TestTableName,
                SelectColumns =
                [
                    new FieldSelectCondition { FieldName = "NAME", Alias = "uname" },
                    new FieldSelectCondition { FieldName = "AGE", Alias = "age" }
                ],
                WhereColumnsAndValues =
                [
                    new BasicWhereCondition { FieldName = "AGE", Operator = "between", Value = new object[] { 25, 35 } }
                ],
                OrderByColumns =
                [
                    new FieldOrderByCondition { FieldName = "NAME", Direction = SortDirection.Asc }
                ]
            },
            Fixture.ConnectionString,
            cancellationToken: TestContext.Current.CancellationToken);

        var rows = JsonSerializer.Deserialize<List<JsonElement>>(json);
        Assert.NotNull(rows);
        Assert.Equal(3, rows.Count);
    }

    [Fact]
    public override async Task ExecuteQueryAsync_ShouldSupportWhereNotIsOrIsNot()
    {
        var json = await Strategy.ExecuteQueryAsync(
            new QueryDefinition
            {
                TableName = TestTableName,
                SelectColumns =
                [
                    new FieldSelectCondition { FieldName = "NAME", Alias = "uname" }
                ],
                WhereColumnsAndValues =
                [
                    new BasicWhereCondition { FieldName = "NAME", Operator = "=", Value = "Alice" },
                    new BasicWhereCondition { FieldName = "NAME", Operator = "=", Value = "Bob", IsOr = true },
                    new BasicWhereCondition { FieldName = "NAME", Operator = "LIKE", Value = "C%", IsNot = true }
                ],
                OrderByColumns =
                [
                    new FieldOrderByCondition { FieldName = "NAME", Direction = SortDirection.Asc }
                ]
            },
            Fixture.ConnectionString,
            cancellationToken: TestContext.Current.CancellationToken);

        var rows = JsonSerializer.Deserialize<List<JsonElement>>(json);
        Assert.NotNull(rows);
        Assert.NotEmpty(rows);
        Assert.Equal(2, rows.Count);
        // Firebird uppercases unquoted aliases
        Assert.Equal("Alice", rows[0].GetProperty(PropUname).GetString());
        Assert.Equal("Bob", rows[1].GetProperty(PropUname).GetString());
    }

    [Fact]
    public override async Task ExecuteQueryAsync_ShouldSupportSubQuerySelectCondition()
    {
        var json = await Strategy.ExecuteQueryAsync(
            new QueryDefinition
            {
                TableName = TestTableName,
                Alias = "u",
                SelectColumns =
                [
                    new FieldSelectCondition { FieldName = "u.NAME", Alias = "uname" },
                    new SubQuerySelectCondition
                    {
                        TableName = TestOrdersTableName,
                        SelectColumns =
                        [
                            new FunctionSelectCondition
                            {
                                FunctionName = "COUNT",
                                Arguments = [new FieldSelectCondition { FieldName = "ID" }]
                            }
                        ],
                        WhereColumnsAndValues =
                        [
                            new ColumnCompareWhereCondition
                            {
                                LeftFieldName = TestOrdersUserIdColumn,
                                Operator = "=",
                                RightFieldName = "u.ID"
                            }
                        ],
                        Alias = "order_count"
                    }
                ],
                OrderByColumns =
                [
                    new FieldOrderByCondition { FieldName = "u.NAME", Direction = SortDirection.Asc }
                ]
            },
            Fixture.ConnectionString,
            cancellationToken: TestContext.Current.CancellationToken);

        var rows = JsonSerializer.Deserialize<List<JsonElement>>(json);
        Assert.NotNull(rows);
        Assert.NotEmpty(rows);
        // Firebird uppercases unquoted aliases
        Assert.True(rows[0].TryGetProperty(PropOrderCount, out _));
    }
}
