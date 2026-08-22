using System.Globalization;
using System.Text.Json;
using SqlAgent.Service.Core.Compilation;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Models;
using SqlAgent.Service.SqlParsing;
using SqlAgent.Service.Strategies;
using SqlAgent.Service.Strategies.Adapters;
using SqlAgent.Service.Validation;
using Xunit;

namespace SqlAgent.Test.Strategies;

public interface IDbFixture : IAsyncLifetime
{
    string ConnectionString { get; }
}

/// <summary>
/// Shared provider integration coverage for the remaining strategy responsibilities: provider
/// connections/metadata plus query execution through the canonical Core compiler/executor path.
/// Legacy DML preview/token/commit behavior was removed from BaseSqlStrategy and is covered at the
/// typed DML/Core boundary instead of being duplicated here.
/// </summary>
public abstract class BaseStrategyTests<TStrategy, TFixture> : IClassFixture<TFixture>
    where TStrategy : ISqlStrategy
    where TFixture : class, IDbFixture
{
    protected readonly TFixture Fixture;
    protected readonly TStrategy ProviderStrategy;
    protected readonly CoreStrategyTestHarness<TStrategy> Strategy;

    protected BaseStrategyTests(TFixture fixture)
    {
        Fixture = fixture;
        ProviderStrategy = CreateStrategy();
        Strategy = new CoreStrategyTestHarness<TStrategy>(ProviderStrategy);
    }

    protected abstract TStrategy CreateStrategy();

    protected abstract string TestTableName { get; }
    protected abstract string TestOrdersTableName { get; }
    protected virtual string TestOrderDetailsTableName => "order_details";
    protected virtual string TestOrderDetailsUnitPriceColumn => "unit_price";
    protected virtual string TestOrderDetailsQuantityColumn => "quantity";
    protected virtual string TestOrderDetailsDiscountColumn => "discount";
    protected abstract string TestSchemaName { get; }
    protected virtual string TestOrdersUserIdColumn => "user_id";
    protected virtual string TestOrdersIdColumn => "id";
    protected virtual string TestOrderDateColumn => "order_date";
    protected virtual int TestFirstOrderId => 1;
    protected virtual bool SupportsStandaloneTime => true;
    protected virtual bool SupportsOffsetTimestamp => Strategy.DbType != SqlAgentToolType.Firebird;
    protected virtual bool SupportsPortableDateFormatting => true;
    protected virtual bool SupportsFormattedDateParsing => true;
    protected virtual string TestUserIdColumn => "id";
    protected virtual string TestUserNameColumn => "Name";

    protected abstract string TableNotFoundErrorCode { get; }
    protected abstract string ColumnNotFoundErrorCode { get; }

    // Retained only so existing provider subclasses do not need unrelated test-hook churn in this
    // strangler step. No shared test invokes the legacy DML compatibility surface.
    protected abstract DmlDefinition CreateInsertDml();

    [Fact]
    public virtual async Task GetTablesAsync_ShouldReturnTables()
    {
        var tables = await Strategy.GetTablesAsync(
            Fixture.ConnectionString,
            TestSchemaName,
            TestContext.Current.CancellationToken);
        Assert.Contains(TestTableName, tables, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public virtual async Task GetSchemasAsync_ShouldReturnAvailableSchemas()
    {
        var schemas = await Strategy.GetSchemasAsync(
            Fixture.ConnectionString,
            TestContext.Current.CancellationToken);
        Assert.Contains(TestSchemaName, schemas, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public virtual async Task GetColumnsAsync_ShouldReturnColumnTypes()
    {
        var columns = await Strategy.GetColumnsAsync(
            Fixture.ConnectionString,
            TestSchemaName,
            TestTableName,
            TestContext.Current.CancellationToken);
        Assert.NotEmpty(columns);
    }

    [Fact]
    public virtual async Task ExecuteQueryAsync_ShouldReturnValidJson()
    {
        var definition = new QueryDefinition { TableName = TestTableName, Limit = 1 };
        ValidateQuery(definition);
        var rows = JsonSerializer.Deserialize<List<JsonElement>>(await Strategy.ExecuteQueryAsync(
            definition,
            Fixture.ConnectionString,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.NotNull(rows);
        Assert.NotEmpty(rows);
    }

    [Fact]
    public virtual async Task ExecuteQueryAsync_ShouldReturnDbError_WhenTableNotFound()
    {
        var definition = new QueryDefinition { TableName = "NON_EXISTENT_TABLE_HS" };
        ValidateQuery(definition);
        var error = await Assert.ThrowsAsync<ProviderExecutionException>(() => Strategy.ExecuteQueryAsync(
            definition,
            Fixture.ConnectionString,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(Strategy.DbType, error.ProviderType);
        Assert.Equal("query", error.Operation);
        Assert.Equal(TableNotFoundErrorCode, error.Code);
    }

    [Fact]
    public virtual async Task ExecuteQueryAsync_ShouldReturnDbError_WhenColumnNotFound()
    {
        var definition = new QueryDefinition
        {
            TableName = TestTableName,
            SelectColumns = [new FieldSelectCondition { FieldName = $"{TestTableName}.NON_EXISTENT_COL_HS" }]
        };
        ValidateQuery(definition);
        var error = await Assert.ThrowsAsync<ProviderExecutionException>(() => Strategy.ExecuteQueryAsync(
            definition,
            Fixture.ConnectionString,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(Strategy.DbType, error.ProviderType);
        Assert.Equal("query", error.Operation);
        Assert.Equal(ColumnNotFoundErrorCode, error.Code);
    }

    [Fact]
    public async Task ExecuteQueryAsync_TimeValue_ShouldBindAsTypedParameter()
    {
        var execution = () => Strategy.ExecuteQueryAsync(
            new QueryDefinition
            {
                TableName = TestTableName,
                SelectColumns =
                [
                    new ConstantSelectCondition
                    {
                        Constant = new SqlTimeValue(new TimeOnly(9, 30, 15)),
                        Alias = "typed_time"
                    }
                ],
                Limit = 1
            },
            Fixture.ConnectionString,
            cancellationToken: TestContext.Current.CancellationToken);

        if (!SupportsStandaloneTime)
        {
            var error = await Assert.ThrowsAnyAsync<Exception>(execution);
            Assert.Contains("no standalone TIME data type", error.Message);
            return;
        }
        Assert.NotEqual("[]", await execution());
    }

    [Fact]
    public async Task ExecuteQueryAsync_LocalTimestampValue_ShouldBindAsTypedParameter()
    {
        var json = await Strategy.ExecuteQueryAsync(
            new QueryDefinition
            {
                TableName = TestTableName,
                SelectColumns =
                [
                    new ConstantSelectCondition
                    {
                        Constant = new SqlLocalDateTimeValue(new DateTime(2026, 8, 21, 9, 30, 15)),
                        Alias = "typed_timestamp"
                    }
                ],
                Limit = 1
            },
            Fixture.ConnectionString,
            cancellationToken: TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(json);
        var value = document.RootElement[0].EnumerateObject().Single().Value.GetString();
        Assert.NotNull(value);
        Assert.True(DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed));
        Assert.Equal(new DateTime(2026, 8, 21, 9, 30, 15), parsed);
    }

    [Fact]
    public async Task ExecuteQueryAsync_LegacyDateTimeConstant_ShouldUseTypedParameter()
    {
        var json = await Strategy.ExecuteQueryAsync(
            new QueryDefinition
            {
                TableName = TestTableName,
                SelectColumns =
                [
                    new ConstantSelectCondition
                    {
                        Constant = new DateTime(2026, 8, 21, 9, 30, 15, DateTimeKind.Unspecified),
                        Alias = "legacy_timestamp"
                    }
                ],
                Limit = 1
            },
            Fixture.ConnectionString,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotEqual("[]", json);
    }

    [Fact]
    public async Task ExecuteQueryAsync_OffsetTimestampValue_ShouldBindAsTypedParameter()
    {
        var execution = () => Strategy.ExecuteQueryAsync(
            new QueryDefinition
            {
                TableName = TestTableName,
                SelectColumns =
                [
                    new ConstantSelectCondition
                    {
                        Constant = new SqlOffsetDateTimeValue(
                            new DateTimeOffset(2026, 8, 21, 9, 30, 15, TimeSpan.FromHours(8))),
                        Alias = "typed_offset_timestamp"
                    }
                ],
                Limit = 1
            },
            Fixture.ConnectionString,
            cancellationToken: TestContext.Current.CancellationToken);

        if (!SupportsOffsetTimestamp)
        {
            await Assert.ThrowsAnyAsync<Exception>(execution);
            return;
        }
        Assert.NotEqual("[]", await execution());
    }

    [Fact]
    public async Task ExecuteQueryAsync_DateDiffDay_ShouldReturnStartToEndDifference()
    {
        var definition = SqlDefinitionParser.ParseQuery(
            $"SELECT DATEDIFF(DAY, DATE '2026-08-20', DATE '2026-08-22') AS day_count FROM {TestTableName}");
        definition.Limit = 1;
        var json = await Strategy.ExecuteQueryAsync(
            definition,
            Fixture.ConnectionString,
            cancellationToken: TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(2m, document.RootElement[0].EnumerateObject().Single().Value.GetDecimal());
    }

    [Fact]
    public async Task ExecuteQueryAsync_DateAddDay_ShouldReturnExpectedDate()
    {
        var definition = SqlDefinitionParser.ParseQuery(
            $"SELECT DATEADD(DAY, 2, DATE '2026-08-20') AS due_date FROM {TestTableName}");
        definition.Limit = 1;
        var json = await Strategy.ExecuteQueryAsync(
            definition,
            Fixture.ConnectionString,
            cancellationToken: TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(json);
        var value = document.RootElement[0].EnumerateObject().Single().Value.GetString();
        Assert.NotNull(value);
        Assert.StartsWith("2026-08-22", value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteQueryAsync_PortableDateFormat_ShouldPreserveMinutes()
    {
        var definition = SqlDefinitionParser.ParseQuery(
            $"SELECT DATE_FORMAT(TIMESTAMP '2026-08-22 13:45:09', 'yyyy-MM-dd HH:mm:ss') AS formatted FROM {TestTableName}");
        definition.SourceDialect = SqlAgentToolType.MsSqlServer;
        definition.Limit = 1;
        if (!SupportsPortableDateFormatting)
        {
            var error = await Assert.ThrowsAsync<SqlCompilationException>(() => Strategy.ExecuteQueryAsync(
                definition,
                Fixture.ConnectionString,
                cancellationToken: TestContext.Current.CancellationToken));
            Assert.Contains("portable date formatting", error.Message);
            return;
        }
        var json = await Strategy.ExecuteQueryAsync(
            definition,
            Fixture.ConnectionString,
            cancellationToken: TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(json);
        Assert.Equal("2026-08-22 13:45:09", document.RootElement[0].EnumerateObject().Single().Value.GetString());
    }

    [Fact]
    public async Task ExecuteQueryAsync_ToDate_ShouldTranslatePortableFormat()
    {
        var definition = SqlDefinitionParser.ParseQuery(
            $"SELECT TO_DATE('2026/08/22', 'yyyy/MM/dd') AS parsed_date FROM {TestTableName}");
        definition.SourceDialect = SqlAgentToolType.MsSqlServer;
        definition.Limit = 1;
        if (!SupportsFormattedDateParsing)
        {
            var error = await Assert.ThrowsAsync<SqlCompilationException>(() => Strategy.ExecuteQueryAsync(
                definition,
                Fixture.ConnectionString,
                cancellationToken: TestContext.Current.CancellationToken));
            Assert.Contains("formatted date parsing", error.Message);
            return;
        }
        var json = await Strategy.ExecuteQueryAsync(
            definition,
            Fixture.ConnectionString,
            cancellationToken: TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(json);
        var value = document.RootElement[0].EnumerateObject().Single().Value.GetString();
        Assert.NotNull(value);
        Assert.StartsWith("2026-08-22", value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteQueryAsync_DateParts_ShouldReturnNumbers()
    {
        var definition = SqlDefinitionParser.ParseQuery(
            $"SELECT YEAR(DATE '2026-08-22') AS y, MONTH(DATE '2026-08-22') AS m, DAY(DATE '2026-08-22') AS d FROM {TestTableName}");
        definition.Limit = 1;
        var json = await Strategy.ExecuteQueryAsync(
            definition,
            Fixture.ConnectionString,
            cancellationToken: TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(json);
        var values = document.RootElement[0].EnumerateObject().Select(x => x.Value.GetDecimal()).ToArray();
        Assert.Equal([2026m, 8m, 22m], values);
    }

    [Fact]
    public virtual async Task ExecuteQueryAsync_ShouldSupportFullQueryDefinitionStructure()
    {
        var json = await Strategy.ExecuteQueryAsync(
            new QueryDefinition
            {
                TableName = TestTableName,
                Alias = "u",
                SelectColumns =
                [
                    new FieldSelectCondition { FieldName = $"u.{TestUserIdColumn}", Alias = "user_id" },
                    new FieldSelectCondition { FieldName = $"u.{TestUserNameColumn}", Alias = "user_name" }
                ],
                WhereColumnsAndValues =
                [
                    new BasicWhereCondition { FieldName = $"u.{TestUserIdColumn}", Operator = ">", Value = 0 }
                ],
                OrderByColumns =
                [
                    new FieldOrderByCondition { FieldName = $"u.{TestUserIdColumn}", Direction = SortDirection.Asc }
                ],
                Limit = 2
            },
            Fixture.ConnectionString,
            cancellationToken: TestContext.Current.CancellationToken);
        var rows = JsonSerializer.Deserialize<List<JsonElement>>(json);
        Assert.NotNull(rows);
        Assert.NotEmpty(rows);
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
                    new FieldSelectCondition { FieldName = $"u.{TestUserNameColumn}", Alias = "uname" },
                    new SubQuerySelectCondition
                    {
                        TableName = TestOrdersTableName,
                        SelectColumns =
                        [
                            new FunctionSelectCondition
                            {
                                FunctionName = "COUNT",
                                Arguments = [new FieldSelectCondition { FieldName = TestOrdersIdColumn }]
                            }
                        ],
                        WhereColumnsAndValues =
                        [
                            new ColumnCompareWhereCondition
                            {
                                LeftFieldName = TestOrdersUserIdColumn,
                                Operator = "=",
                                RightFieldName = $"u.{TestUserIdColumn}"
                            }
                        ],
                        Alias = "order_count"
                    }
                ],
                Limit = 1
            },
            Fixture.ConnectionString,
            cancellationToken: TestContext.Current.CancellationToken);
        var rows = JsonSerializer.Deserialize<List<JsonElement>>(json);
        Assert.NotNull(rows);
        Assert.NotEmpty(rows);
        Assert.True(TryGetPropertyIgnoreCase(rows[0], "order_count", out _));
    }

    [Fact]
    public virtual async Task ExecuteQueryAsync_ShouldSupportRoundedAggregatedNestedArithmeticExpression()
    {
        const string alias = "total_sales";
        const string tableAlias = "od";
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
        Assert.True(TryGetPropertyIgnoreCase(rows[0], alias, out var totalSales));
        Assert.InRange(totalSales.GetDecimal(), 37.65m, 37.66m);
    }

    // Provider-specific files still own these integration shapes when identifier casing or provider
    // syntax needs a specialized fixture. Keep virtual hooks without base Facts to avoid duplicate
    // executions while preserving their override contracts.
    public virtual Task ExecuteQueryAsync_ShouldSupportOffsetWithoutLimit() => Task.CompletedTask;
    public virtual Task ExecuteQueryAsync_ShouldSupportSubQueryWhereCondition() => Task.CompletedTask;
    public virtual Task ExecuteQueryAsync_ShouldSupportExistsSubQueryWhereCondition() => Task.CompletedTask;
    public virtual Task ExecuteQueryAsync_ShouldSupportBetweenInHaving() => Task.CompletedTask;
    public virtual Task ExecuteQueryAsync_ShouldSupportWhereNotIsOrIsNot() => Task.CompletedTask;
    public virtual Task ExecuteQueryAsync_ShouldSupportGroupHavingCondition() => Task.CompletedTask;

    protected static void ValidateQuery(QueryDefinition definition)
    {
        var errors = DefinitionValidator.Validate(definition);
        Assert.True(errors.Count == 0, "QueryDefinition validation failed:\n" + string.Join("\n", errors));
    }

    protected static void ValidateDml(DmlDefinition definition)
    {
        var errors = DefinitionValidator.Validate(definition);
        Assert.True(errors.Count == 0, "DmlDefinition validation failed:\n" + string.Join("\n", errors));
    }

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
