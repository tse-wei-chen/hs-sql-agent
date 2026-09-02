using System.Globalization;
using System.Text.Json;
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
    protected virtual string TestUserAgeColumn => "age";

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
    public async Task ExecuteRawQueryAsync_DifferentialSemanticOracleCorpus_ShouldMatchNativeEngine()
    {
        foreach (var sql in DifferentialSemanticOracleSql())
        {
            var translatedJson = await Strategy.ExecuteRawQueryAsync(
                sql,
                SqlAgentToolType.Postgres,
                Fixture.ConnectionString,
                TestContext.Current.CancellationToken);

            var translatedRows = NormalizeJsonRows(translatedJson);
            var nativeRows = await ExecuteNativeRowsAsync(sql);

            Assert.Equal(
                nativeRows,
                translatedRows);
        }
    }

    private IEnumerable<string> DifferentialSemanticOracleSql()
    {
        yield return
            $"SELECT {TestUserIdColumn} AS item_id, " +
            $"{TestUserNameColumn} AS item_name, " +
            $"{TestUserAgeColumn} + 2 AS next_age " +
            $"FROM {TestTableName} " +
            $"WHERE {TestUserAgeColumn} >= 25 " +
            $"ORDER BY {TestUserIdColumn}";

        yield return
            $"SELECT {TestOrdersUserIdColumn} AS owner_id, " +
            $"COUNT(*) AS order_count " +
            $"FROM {TestOrdersTableName} " +
            $"GROUP BY {TestOrdersUserIdColumn} " +
            $"HAVING COUNT(*) > 0 " +
            $"ORDER BY {TestOrdersUserIdColumn}";

        yield return
            $"SELECT {TestUserIdColumn} AS item_id, " +
            $"CASE WHEN {TestUserAgeColumn} >= 30 " +
            $"THEN 'senior' ELSE 'junior' END AS age_band " +
            $"FROM {TestTableName} " +
            $"ORDER BY {TestUserIdColumn}";
    }

    private async Task<string[]> ExecuteNativeRowsAsync(string sql)
    {
        await using var connection = ProviderStrategy.CreateConnection(Fixture.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var rows = new List<string>();

        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            var values = new string[reader.FieldCount];
            for (var index = 0; index < reader.FieldCount; index++)
            {
                values[index] = NormalizeNativeValue(reader.GetValue(index));
            }

            rows.Add(string.Join("\u001f", values));
        }

        return rows.ToArray();
    }

    private static string[] NormalizeJsonRows(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement
            .EnumerateArray()
            .Select(row => string.Join(
                "\u001f",
                row.EnumerateObject().Select(property => NormalizeJsonValue(property.Value))))
            .ToArray();
    }

    private static string NormalizeNativeValue(object value) =>
        value switch
        {
            DBNull => "<null>",
            null => "<null>",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "<null>"
        };

    private static string NormalizeJsonValue(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.Null => "<null>",
            JsonValueKind.String => value.GetString() ?? "<null>",
            JsonValueKind.Number when value.TryGetDecimal(out var number) =>
                number.ToString(CultureInfo.InvariantCulture),
            JsonValueKind.True => "True",
            JsonValueKind.False => "False",
            _ => value.ToString()
        };

    [Fact]
    public async Task ExecuteRawQueryAsync_CteWhereOrder_ShouldCompileRenderAndExecute()
    {
        var sql =
            $"WITH recent AS (" +
            $"SELECT {TestUserIdColumn} AS item_id " +
            $"FROM {TestTableName} " +
            $"WHERE {TestUserIdColumn} > 0" +
            $") SELECT item_id FROM recent ORDER BY item_id";

        var json = await Strategy.ExecuteRawQueryAsync(
            sql,
            Strategy.DbType,
            Fixture.ConnectionString,
            TestContext.Current.CancellationToken);

        var rows = JsonSerializer.Deserialize<List<JsonElement>>(json);
        Assert.NotNull(rows);
        Assert.NotEmpty(rows);
    }

    [Fact]
    public async Task ExecuteRawQueryAsync_CteUnionAllBody_ShouldCompileRenderAndExecute()
    {
        var sql =
            $"WITH recent AS (" +
            $"SELECT {TestUserIdColumn} AS item_id FROM {TestTableName} " +
            $"UNION ALL " +
            $"SELECT {TestUserIdColumn} AS item_id FROM {TestTableName}" +
            $") SELECT item_id FROM recent";

        var json = await Strategy.ExecuteRawQueryAsync(
            sql,
            Strategy.DbType,
            Fixture.ConnectionString,
            TestContext.Current.CancellationToken);

        var rows = JsonSerializer.Deserialize<List<JsonElement>>(json);
        Assert.NotNull(rows);
        Assert.True(
            rows.Count >= 2,
            $"Expected CTE UNION ALL to return multiple rows for {Strategy.DbType}.");
    }

    [Fact]
    public async Task ExecuteRawQueryAsync_CteJoin_ShouldCompileRenderAndExecute()
    {
        var sql =
            $"WITH recent AS (" +
            $"SELECT o.{TestOrdersIdColumn} AS item_id " +
            $"FROM {TestOrdersTableName} o " +
            $"JOIN {TestTableName} u " +
            $"ON o.{TestOrdersUserIdColumn} = u.{TestUserIdColumn}" +
            $") SELECT item_id FROM recent";

        var json = await Strategy.ExecuteRawQueryAsync(
            sql,
            Strategy.DbType,
            Fixture.ConnectionString,
            TestContext.Current.CancellationToken);

        var rows = JsonSerializer.Deserialize<List<JsonElement>>(json);
        Assert.NotNull(rows);
        Assert.NotEmpty(rows);
    }

    [Fact]
    public async Task ExecuteRawQueryAsync_CteGroupHaving_ShouldCompileRenderAndExecute()
    {
        var sql =
            $"WITH totals AS (" +
            $"SELECT {TestOrdersUserIdColumn} AS owner_id, COUNT(*) AS total " +
            $"FROM {TestOrdersTableName} " +
            $"GROUP BY {TestOrdersUserIdColumn} " +
            $"HAVING COUNT(*) > 0" +
            $") SELECT owner_id FROM totals";

        var json = await Strategy.ExecuteRawQueryAsync(
            sql,
            Strategy.DbType,
            Fixture.ConnectionString,
            TestContext.Current.CancellationToken);

        var rows = JsonSerializer.Deserialize<List<JsonElement>>(json);
        Assert.NotNull(rows);
        Assert.NotEmpty(rows);
    }

    [Fact]
    public async Task ExecuteRawQueryAsync_CteCorrelatedExists_ShouldCompileRenderAndExecute()
    {
        var sql =
            $"WITH recent AS (" +
            $"SELECT {TestOrdersUserIdColumn} AS owner_id " +
            $"FROM {TestOrdersTableName}" +
            $") SELECT r.owner_id FROM recent r " +
            $"WHERE EXISTS (" +
            $"SELECT {TestUserIdColumn} FROM {TestTableName} u " +
            $"WHERE u.{TestUserIdColumn} = r.owner_id" +
            $")";

        var json = await Strategy.ExecuteRawQueryAsync(
            sql,
            Strategy.DbType,
            Fixture.ConnectionString,
            TestContext.Current.CancellationToken);

        var rows = JsonSerializer.Deserialize<List<JsonElement>>(json);
        Assert.NotNull(rows);
        Assert.NotEmpty(rows);
    }

    [Fact]
    public async Task ExecuteRawQueryAsync_CteWindow_ShouldCompileRenderAndExecute()
    {
        var sql =
            $"WITH ranked AS (" +
            $"SELECT {TestOrdersUserIdColumn} AS owner_id, " +
            $"ROW_NUMBER() OVER (" +
            $"PARTITION BY {TestOrdersUserIdColumn} " +
            $"ORDER BY {TestOrdersIdColumn}" +
            $") AS row_no " +
            $"FROM {TestOrdersTableName}" +
            $") SELECT owner_id, row_no FROM ranked";

        var json = await Strategy.ExecuteRawQueryAsync(
            sql,
            Strategy.DbType,
            Fixture.ConnectionString,
            TestContext.Current.CancellationToken);

        var rows = JsonSerializer.Deserialize<List<JsonElement>>(json);
        Assert.NotNull(rows);
        Assert.NotEmpty(rows);
        Assert.All(
            rows,
            row => Assert.True(
                TryGetPropertyIgnoreCase(row, "row_no", out _)));
    }

    [Fact]
    public async Task ExecuteRawQueryAsync_ChainedCtes_ShouldCompileRenderAndExecute()
    {
        var sql =
            $"WITH base AS (" +
            $"SELECT {TestUserIdColumn} AS item_id FROM {TestTableName}" +
            $"), recent AS (" +
            $"SELECT item_id FROM base" +
            $") SELECT item_id FROM recent";

        var json = await Strategy.ExecuteRawQueryAsync(
            sql,
            Strategy.DbType,
            Fixture.ConnectionString,
            TestContext.Current.CancellationToken);

        var rows = JsonSerializer.Deserialize<List<JsonElement>>(json);
        Assert.NotNull(rows);
        Assert.NotEmpty(rows);
    }

    [Fact]
    public async Task ExecuteRawQueryAsync_CtePaging_ReturnsSecondRow()
    {
        var paging = Strategy.DbType switch
        {
            SqlAgentToolType.Postgres
                or SqlAgentToolType.MySQL
                or SqlAgentToolType.Sqlite =>
                " LIMIT 1 OFFSET 1",
            SqlAgentToolType.MsSqlServer
                or SqlAgentToolType.Oracle
                or SqlAgentToolType.Firebird =>
                " OFFSET 1 ROWS FETCH NEXT 1 ROWS ONLY",
            _ => throw new ArgumentOutOfRangeException(
                nameof(Strategy.DbType),
                Strategy.DbType,
                "Unsupported SQL dialect.")
        };
        var sql =
            $"WITH recent AS (" +
            $"SELECT {TestUserIdColumn} AS item_id FROM {TestTableName}" +
            $") SELECT item_id FROM recent ORDER BY item_id" +
            paging;

        var json = await Strategy.ExecuteRawQueryAsync(
            sql,
            Strategy.DbType,
            Fixture.ConnectionString,
            TestContext.Current.CancellationToken);

        using var document = JsonDocument.Parse(json);
        var row = Assert.Single(document.RootElement.EnumerateArray());
        var value = row.EnumerateObject().Single().Value.GetDecimal();
        Assert.Equal(2m, value);
    }

    [Fact]
    public async Task ExecuteRawQueryAsync_CteReferencedInsideSubquery_ShouldCompileRenderAndExecute()
    {
        var sql =
            $"WITH recent AS (" +
            $"SELECT {TestOrdersUserIdColumn} AS owner_id FROM {TestOrdersTableName}" +
            $") SELECT u.{TestUserIdColumn} FROM {TestTableName} u " +
            $"WHERE EXISTS (" +
            $"SELECT r.owner_id FROM recent r " +
            $"WHERE r.owner_id = u.{TestUserIdColumn}" +
            $")";

        var json = await Strategy.ExecuteRawQueryAsync(
            sql,
            Strategy.DbType,
            Fixture.ConnectionString,
            TestContext.Current.CancellationToken);

        var rows = JsonSerializer.Deserialize<List<JsonElement>>(json);
        Assert.NotNull(rows);
        Assert.NotEmpty(rows);
    }

    [Fact]
    public async Task ExecuteRawQueryAsync_CteJoinedWithPhysicalTable_ShouldCompileRenderAndExecute()
    {
        var sql =
            $"WITH recent AS (" +
            $"SELECT {TestOrdersUserIdColumn} AS owner_id FROM {TestOrdersTableName}" +
            $") SELECT r.owner_id FROM recent r " +
            $"JOIN {TestTableName} u " +
            $"ON r.owner_id = u.{TestUserIdColumn}";

        var json = await Strategy.ExecuteRawQueryAsync(
            sql,
            Strategy.DbType,
            Fixture.ConnectionString,
            TestContext.Current.CancellationToken);

        var rows = JsonSerializer.Deserialize<List<JsonElement>>(json);
        Assert.NotNull(rows);
        Assert.NotEmpty(rows);
    }

    [Fact]
    public async Task ExecuteRawQueryAsync_CteRootUnionAll_ShouldCompileRenderAndExecute()
    {
        var sql =
            $"WITH recent AS (" +
            $"SELECT {TestUserIdColumn} AS item_id FROM {TestTableName}" +
            $") SELECT item_id FROM recent " +
            $"UNION ALL " +
            $"SELECT {TestUserIdColumn} AS item_id FROM {TestTableName}";

        var json = await Strategy.ExecuteRawQueryAsync(
            sql,
            Strategy.DbType,
            Fixture.ConnectionString,
            TestContext.Current.CancellationToken);

        var rows = JsonSerializer.Deserialize<List<JsonElement>>(json);
        Assert.NotNull(rows);
        Assert.Equal(6, rows.Count);
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
        var json = await Strategy.ExecuteRawQueryAsync(
            $"SELECT DATEDIFF(DAY, CAST('2026-08-20' AS DATE), CAST('2026-08-22' AS DATE)) AS day_count FROM {TestTableName}",
            SqlAgentToolType.MsSqlServer,
            Fixture.ConnectionString,
            TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(2m, document.RootElement[0].EnumerateObject().Single().Value.GetDecimal());
    }

    [Fact]
    public async Task ExecuteQueryAsync_DateAddDay_ShouldReturnExpectedDate()
    {
        var json = await Strategy.ExecuteRawQueryAsync(
            $"SELECT DATEADD(DAY, 2, CAST('2026-08-20' AS DATE)) AS due_date FROM {TestTableName}",
            SqlAgentToolType.MsSqlServer,
            Fixture.ConnectionString,
            TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(json);
        var value = document.RootElement[0].EnumerateObject().Single().Value.GetString();
        Assert.NotNull(value);
        Assert.StartsWith("2026-08-22", value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteQueryAsync_PortableDateFormat_ShouldPreserveMinutes()
    {
        const string formatSqlPrefix = "SELECT FORMAT(CAST('2026-08-22 13:45:09' AS DATETIME2), 'yyyy-MM-dd HH:mm:ss') AS formatted FROM ";
        var sql = formatSqlPrefix + TestTableName;
        if (!SupportsPortableDateFormatting)
        {
            var error = await Assert.ThrowsAsync<SqlCompilationException>(() => Strategy.ExecuteRawQueryAsync(
                sql,
                SqlAgentToolType.MsSqlServer,
                Fixture.ConnectionString,
                TestContext.Current.CancellationToken));
            Assert.Contains("portable date formatting", error.Message);
            return;
        }
        var json = await Strategy.ExecuteRawQueryAsync(
            sql,
            SqlAgentToolType.MsSqlServer,
            Fixture.ConnectionString,
            TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(json);
        Assert.Equal("2026-08-22 13:45:09", document.RootElement[0].EnumerateObject().Single().Value.GetString());
    }

    [Fact]
    public async Task ExecuteQueryAsync_ToDate_ShouldTranslatePortableFormat()
    {
        var sql = $"SELECT TO_DATE('2026/08/22', 'YYYY/MM/DD') AS parsed_date FROM {TestTableName}";
        if (!SupportsFormattedDateParsing)
        {
            var error = await Assert.ThrowsAsync<SqlCompilationException>(() => Strategy.ExecuteRawQueryAsync(
                sql,
                SqlAgentToolType.Oracle,
                Fixture.ConnectionString,
                TestContext.Current.CancellationToken));
            Assert.Contains("formatted date parsing", error.Message);
            return;
        }
        var json = await Strategy.ExecuteRawQueryAsync(
            sql,
            SqlAgentToolType.Oracle,
            Fixture.ConnectionString,
            TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(json);
        var value = document.RootElement[0].EnumerateObject().Single().Value.GetString();
        Assert.NotNull(value);
        Assert.StartsWith("2026-08-22", value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteQueryAsync_DateParts_ShouldReturnNumbers()
    {
        var json = await Strategy.ExecuteRawQueryAsync(
            $"SELECT EXTRACT(YEAR FROM DATE '2026-08-22') AS y, EXTRACT(MONTH FROM DATE '2026-08-22') AS m, EXTRACT(DAY FROM DATE '2026-08-22') AS d FROM {TestTableName}",
            SqlAgentToolType.Postgres,
            Fixture.ConnectionString,
            TestContext.Current.CancellationToken);
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
        ArgumentNullException.ThrowIfNull(definition);
        _ = QueryDefinitionCoreMapper.Map(definition);
    }

    protected static void ValidateDml(DmlDefinition definition) =>
        ArgumentNullException.ThrowIfNull(definition);

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
