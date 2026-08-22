using System.Text.Json;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Models;
using SqlAgent.Service.SqlParsing;
using SqlAgent.Service.Strategies;
using SqlAgent.Service.Strategies.Adapters;
using Testcontainers.PostgreSql;
using Xunit;

namespace SqlAgent.Test.Strategies;

public class PostgresFixture : IDbFixture
{
    public PostgreSqlContainer Container { get; }
    public string ConnectionString => Container.GetConnectionString();

    public PostgresFixture()
    {
        Container = new PostgreSqlBuilder("postgres:15-alpine")
            .WithDatabase("test_db")
            .WithUsername("test_user")
            .WithPassword("TestPass123!")
            .WithCommand("-c", "fsync=off", "-c", "full_page_writes=off")
            .Build();
    }

    public async ValueTask InitializeAsync()
    {
        await Container.StartAsync();

        var strategy = new PostgresStrategy();

        using var conn = strategy.CreateConnection(ConnectionString);
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE SCHEMA IF NOT EXISTS custom_schema;

            CREATE TABLE IF NOT EXISTS public.users (
                id SERIAL PRIMARY KEY,
                name VARCHAR(100),
                age INTEGER,
                active BOOLEAN,
                created_date TIMESTAMP
            );
            INSERT INTO public.users (name, age, active, created_date) VALUES
            ('Alice', 30, true, '2023-01-01 10:00:00'),
            ('Bob', 25, true, '2023-02-01 10:00:00'),
            ('Charlie', 35, false, '2023-03-01 10:00:00');

            CREATE TABLE IF NOT EXISTS public.orders (
                id SERIAL PRIMARY KEY,
                user_id INTEGER,
                amount DECIMAL(10, 2),
                order_date DATE
            );
            INSERT INTO public.orders (user_id, amount, order_date) VALUES
            (1, 150.0, '2023-01-10'),
            (1, 200.0, '2023-02-15'),
            (2, 50.0, '2023-03-20');

            CREATE TABLE IF NOT EXISTS public.order_details (
                id SERIAL PRIMARY KEY,
                unit_price DOUBLE PRECISION,
                quantity INTEGER,
                discount DOUBLE PRECISION
            );
            INSERT INTO public.order_details (unit_price, quantity, discount) VALUES
            (10.123, 2, 0.1),
            (20.456, 1, 0.05);
        ";
        await cmd.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync() => await Container.DisposeAsync();
}


public class PostgresStrategyTests(PostgresFixture fixture) : BaseStrategyTests<PostgresStrategy, PostgresFixture>(fixture)
{
    protected override PostgresStrategy CreateStrategy() => new();

    protected override string TestTableName => "users";
    protected override string TestOrdersTableName => "orders";
    protected override string TestSchemaName => "public";

    protected override string TableNotFoundErrorCode => "42P01";
    protected override string ColumnNotFoundErrorCode => "42703";

    [Fact]
    public override async Task GetSchemasAsync_ShouldReturnAvailableSchemas()
    {
        var schemas = await Strategy.GetSchemasAsync(Fixture.ConnectionString, TestContext.Current.CancellationToken);
        Assert.Contains("public", schemas);
        Assert.Contains("custom_schema", schemas);
    }

    [Fact]
    public override async Task GetColumnsAsync_ShouldReturnColumnTypes()
    {
        await base.GetColumnsAsync_ShouldReturnColumnTypes();
        var columns = await Strategy.GetColumnsAsync(Fixture.ConnectionString, TestSchemaName, TestTableName, TestContext.Current.CancellationToken);
        Assert.Equal("integer", columns.First(c => c.Column == "id").Type, ignoreCase: true);
        Assert.Equal("boolean", columns.First(c => c.Column == "active").Type, ignoreCase: true);
    }

    protected override DmlDefinition CreateInsertDml() => new()
    {
        Operation = DmlOperation.Insert,
        TableName = TestTableName,
        Values = [
            new NameValuePair { FieldName = "name", Value = "David" },
            new NameValuePair { FieldName = "age", Value = 40 },
            new NameValuePair { FieldName = "active", Value = true }
        ]
    };

    [Fact]
    public void BuildConnectionString_ShouldGenerateValidPostgresFormat()
    {
        var model = new BuildDbConnectionModel
        {
            Provider = "Postgres",
            Host = "localhost",
            Port = "5432",
            Database = "mydb",
            Username = "user",
            Password = "pw"
        };
        var connStr = Strategy.BuildConnectionString(model);

        Assert.Contains("Host=localhost", connStr);
        Assert.Contains("Port=5432", connStr);
    }

    [Fact]
    public async Task ExecuteQueryAsync_ShouldReturnDbError_WhenValueFormatIsInvalid()
    {
        var ex = await Assert.ThrowsAsync<ProviderExecutionException>(() => Strategy.ExecuteQueryAsync(
            new QueryDefinition
            {
                TableName = TestTableName,
                WhereColumnsAndValues = [new BasicWhereCondition { FieldName = "created_date", Operator = "=", Value = "this-is-not-a-date" }]
            },
            Fixture.ConnectionString,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(SqlAgentToolType.Postgres, ex.ProviderType);
        Assert.Equal("query", ex.Operation);
        Assert.True(ex.Code is "22P02" or "42883", $"Result was: {ex.Message}");
    }

    [Fact]
    public async Task ExecuteQueryAsync_ShouldReturnDbError_WhenOperatorOrTypeMismatch()
    {
        var ex = await Assert.ThrowsAsync<ProviderExecutionException>(() => Strategy.ExecuteQueryAsync(
            new QueryDefinition
            {
                TableName = TestTableName,
                SelectColumns = [
                    new OperationSelectCondition
                    {
                        Left = new FieldSelectCondition { FieldName = "name" },
                        Operator = ArithmeticOperator.Subtract,
                        Right = new ConstantSelectCondition { Constant = 1 }
                    }
                ]
            },
            Fixture.ConnectionString,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(SqlAgentToolType.Postgres, ex.ProviderType);
        Assert.Equal("query", ex.Operation);
        Assert.Equal("42883", ex.Code);
        Assert.Contains("operator", ex.ProviderMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteQueryAsync_ShouldReturnDbError_WhenColumnIsAmbiguous()
    {
        var ex = await Assert.ThrowsAsync<ProviderExecutionException>(() => Strategy.ExecuteQueryAsync(
            new QueryDefinition
            {
                TableName = TestTableName,
                Joins = [
                    new JoinCondition
                    {
                        Table = "orders",
                        OnConditions =
                        [
                            new ColumnCompareWhereCondition
                            {
                                LeftFieldName = "users.id",
                                Operator = "=",
                                RightFieldName = "orders.user_id"
                            }
                        ]
                    }
                ],
                SelectColumns = [new FieldSelectCondition { FieldName = "id" }]
            },
            Fixture.ConnectionString,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(SqlAgentToolType.Postgres, ex.ProviderType);
        Assert.Equal("query", ex.Operation);
        Assert.Equal("42702", ex.Code);
        Assert.Contains("ambiguous", ex.ProviderMessage, StringComparison.OrdinalIgnoreCase);
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
                        Right = new FieldSelectCondition { FieldName = "age" },
                        Alias = "delta"
                    }
                ],
                WhereColumnsAndValues = [new BasicWhereCondition { FieldName = "name", Operator = "=", Value = "Alice" }],
            },
            Fixture.ConnectionString,
            cancellationToken: TestContext.Current.CancellationToken);

        var rows = JsonSerializer.Deserialize<List<JsonElement>>(json);

        Assert.NotNull(rows);
        Assert.Single(rows);
        Assert.Equal(-29, rows[0].GetProperty("delta").GetInt32());
    }

    [Fact]
    public async Task ExecuteQueryAsync_ShouldSupportNestedRoundFunctionWithConstantPrecision()
    {
        var json = await Strategy.ExecuteQueryAsync(
            new QueryDefinition
            {
                SourceDialect = SqlAgentToolType.MySQL,
                TableName = "orders",
                SelectColumns =
                [
                    new FunctionSelectCondition
                    {
                        Alias = "avg_amount",
                        FunctionName = "ROUND",
                        Arguments =
                        [
                            new FunctionSelectCondition
                            {
                                FunctionName = "AVG",
                                Arguments =
                                [
                                    new FieldSelectCondition { FieldName = "amount" }
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
        Assert.Equal(133.33m, rows[0].GetProperty("avg_amount").GetDecimal());
    }

    [Fact]
    public async Task ExecuteQueryAsync_ShouldSupportSelectWindowFunction()
    {
        var json = await Strategy.ExecuteQueryAsync(
            new QueryDefinition
            {
                TableName = "orders",
                SelectColumns =
                [
                    new FieldSelectCondition { FieldName = "user_id" },
                    new FieldSelectCondition { FieldName = "order_date" },
                    new FunctionSelectCondition
                    {
                        FunctionName = "LAG",
                        Arguments = [new FieldSelectCondition { FieldName = "order_date" }],
                        Alias = "previous_order_date",
                        Window = new WindowDefinition
                        {
                            PartitionBy = [new FieldGroupByCondition { FieldName = "user_id" }],
                            OrderBy = [new FieldOrderByCondition { FieldName = "order_date" }]
                        }
                    }
                ],
                WhereColumnsAndValues =
                [
                    new BasicWhereCondition { FieldName = "user_id", Operator = "=", Value = 1 }
                ],
                OrderByColumns =
                [
                    new FieldOrderByCondition { FieldName = "order_date", Direction = SortDirection.Asc }
                ]
            },
            Fixture.ConnectionString,
            cancellationToken: TestContext.Current.CancellationToken);

        var rows = JsonSerializer.Deserialize<List<JsonElement>>(json);

        Assert.NotNull(rows);
        Assert.Equal(2, rows.Count);
        Assert.Equal(JsonValueKind.Null, rows[0].GetProperty("previous_order_date").ValueKind);
        Assert.Contains("2023-01-10", rows[1].GetProperty("previous_order_date").ToString());
    }

    [Fact]
    public async Task ExecuteQueryAsync_ShouldTranslatePortableDateFormat()
    {
        var json = await Strategy.ExecuteQueryAsync(
            new QueryDefinition
            {
                SourceDialect = SqlAgentToolType.MySQL,
                TableName = "orders",
                SelectColumns =
                [
                    new FunctionSelectCondition
                    {
                        FunctionName = "DATE_FORMAT",
                        Arguments =
                        [
                            new FieldSelectCondition { FieldName = "order_date" },
                            new ConstantSelectCondition { Constant = "%Y-%m" }
                        ],
                        Alias = "order_month"
                    },
                    new FunctionSelectCondition
                    {
                        FunctionName = "COUNT",
                        Arguments = [new FieldSelectCondition { FieldName = "id" }],
                        Alias = "total_orders"
                    }
                ],
                GroupByConditions =
                [
                    new FunctionGroupByCondition
                    {
                        FunctionName = "DATE_FORMAT",
                        Arguments =
                        [
                            new FieldSelectCondition { FieldName = "order_date" },
                            new ConstantSelectCondition { Constant = "%Y-%m" }
                        ]
                    }
                ],
                OrderByColumns =
                [
                    new FieldOrderByCondition { FieldName = "order_month", Direction = SortDirection.Asc }
                ]
            },
            Fixture.ConnectionString,
            cancellationToken: TestContext.Current.CancellationToken);

        var rows = JsonSerializer.Deserialize<List<JsonElement>>(json);

        Assert.NotNull(rows);
        Assert.Equal(3, rows.Count);
        Assert.Equal("2023-01", rows[0].GetProperty("order_month").GetString());
        Assert.Equal(1, rows[0].GetProperty("total_orders").GetInt64());
    }

    [Fact]
    public async Task ExecuteQueryAsync_ShouldTranslatePortableDateDiff()
    {
        var json = await Strategy.ExecuteQueryAsync(
            new QueryDefinition
            {
                TableName = "orders",
                SelectColumns =
                [
                    new FieldSelectCondition { FieldName = "id" },
                    new FunctionSelectCondition
                    {
                        FunctionName = "DATEDIFF",
                        Arguments =
                        [
                            new ConstantSelectCondition { Constant = "2023-02-15" },
                            new FieldSelectCondition { FieldName = "order_date" }
                        ],
                        Alias = "days_until"
                    }
                ],
                WhereColumnsAndValues =
                [
                    new BasicWhereCondition { FieldName = "id", Operator = "=", Value = 1 }
                ]
            },
            Fixture.ConnectionString,
            cancellationToken: TestContext.Current.CancellationToken);

        var rows = JsonSerializer.Deserialize<List<JsonElement>>(json);

        Assert.NotNull(rows);
        Assert.Single(rows);
        Assert.Equal(36, rows[0].GetProperty("days_until").GetInt32());
    }

    [Fact]
    public async Task ExecuteQueryAsync_ShouldSupportExpressionHavingWithAggregateArithmetic()
    {
        var json = await Strategy.ExecuteQueryAsync(
            new QueryDefinition
            {
                TableName = "orders",
                SelectColumns =
                [
                    new FieldSelectCondition { FieldName = "user_id" },
                    new FunctionSelectCondition
                    {
                        FunctionName = "SUM",
                        Arguments = [new FieldSelectCondition { FieldName = "amount" }],
                        Alias = "total_amount"
                    }
                ],
                GroupByConditions =
                [
                    new FieldGroupByCondition { FieldName = "user_id" }
                ],
                HavingConditions =
                [
                    new ExpressionHavingCondition
                    {
                        LeftExpression = new OperationSelectCondition
                        {
                            Left = new FunctionSelectCondition
                            {
                                FunctionName = "SUM",
                                Arguments = [new FieldSelectCondition { FieldName = "amount" }]
                            },
                            Operator = ArithmeticOperator.Subtract,
                            Right = new ConstantSelectCondition { Constant = 100 }
                        },
                        Operator = ">",
                        RightExpression = new ConstantSelectCondition { Constant = 0 }
                    }
                ],
                OrderByColumns =
                [
                    new FieldOrderByCondition { FieldName = "user_id", Direction = SortDirection.Asc }
                ]
            },
            Fixture.ConnectionString,
            cancellationToken: TestContext.Current.CancellationToken);

        var rows = JsonSerializer.Deserialize<List<JsonElement>>(json);

        Assert.NotNull(rows);
        Assert.Single(rows);
        Assert.Equal(1, rows[0].GetProperty("user_id").GetInt32());
        Assert.Equal(350m, rows[0].GetProperty("total_amount").GetDecimal());
    }

    [Fact]
    public async Task ExecuteQueryAsync_ShouldTranslateDialectSpecificDateFormat()
    {
        var json = await Strategy.ExecuteQueryAsync(
            new QueryDefinition
            {
                SourceDialect = SqlAgentToolType.MsSqlServer,
                TableName = "orders",
                SelectColumns =
                [
                    new FunctionSelectCondition
                    {
                        FunctionName = "FORMAT",
                        Arguments =
                        [
                            new FieldSelectCondition { FieldName = "order_date" },
                            new ConstantSelectCondition { Constant = "yyyy-MM" }
                        ],
                        Alias = "order_month"
                    },
                    new FunctionSelectCondition
                    {
                        FunctionName = "COUNT",
                        Arguments = [new FieldSelectCondition { FieldName = "id" }],
                        Alias = "total_orders"
                    }
                ],
                GroupByConditions =
                [
                    new FunctionGroupByCondition
                    {
                        FunctionName = "FORMAT",
                        Arguments =
                        [
                            new FieldSelectCondition { FieldName = "order_date" },
                            new ConstantSelectCondition { Constant = "yyyy-MM" }
                        ]
                    }
                ],
                OrderByColumns =
                [
                    new FieldOrderByCondition { FieldName = "order_month", Direction = SortDirection.Asc }
                ]
            },
            Fixture.ConnectionString,
            cancellationToken: TestContext.Current.CancellationToken);

        var rows = JsonSerializer.Deserialize<List<JsonElement>>(json);

        Assert.NotNull(rows);
        Assert.Equal(3, rows.Count);
        Assert.Equal("2023-01", rows[0].GetProperty("order_month").GetString());
    }
}