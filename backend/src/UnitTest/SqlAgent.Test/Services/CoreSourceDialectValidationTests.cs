using Xunit;

namespace SqlAgent.Test.Services;

public class CoreSourceDialectValidationTests
{
    [Fact]
    public void Compile_DateAdd_WithPostgresSource_FailsBeforeCanonicalization()
    {
        var ex = Assert.Throws<SqlCompilationException>(() => CompileQuery(
            "SELECT DATEADD(DAY, 1, created_at) FROM orders",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.MySQL));

        Assert.Contains("source dialect Postgres", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DATEADD", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_DateAdd_WithSqlServerSource_RemainsPortable()
    {
        var command = CompileQuery(
            "SELECT DATEADD(DAY, 1, created_at) FROM orders",
            SqlAgentToolType.MsSqlServer,
            SqlAgentToolType.MySQL);

        Assert.Contains("TIMESTAMPADD", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_MySqlTwoArgumentDateDiff_RemainsPortable()
    {
        var command = CompileQuery(
            "SELECT DATEDIFF(completed_at, created_at) FROM orders",
            SqlAgentToolType.MySQL,
            SqlAgentToolType.Postgres);

        Assert.NotEmpty(command.Sql);
        Assert.DoesNotContain("CORE_DATE_DIFF", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_PostgresTwoArgumentDateDiff_IsRejectedAsRawSourceSyntax()
    {
        var ex = Assert.Throws<SqlCompilationException>(() => CompileQuery(
            "SELECT DATEDIFF(completed_at, created_at) FROM orders",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres));

        Assert.Contains("DATEDIFF", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("source dialect Postgres", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_MySqlFormat_IsNotMisreadAsSqlServerDateFormat()
    {
        var ex = Assert.Throws<SqlCompilationException>(() => CompileQuery(
            "SELECT FORMAT(amount, 2) FROM orders",
            SqlAgentToolType.MySQL,
            SqlAgentToolType.MsSqlServer));

        Assert.Contains("FORMAT", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("different semantics", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_DateFormat_WithSqlServerSource_IsRejectedAsInvalidSourceSyntax()
    {
        var ex = Assert.Throws<SqlCompilationException>(() => CompileQuery(
            "SELECT DATE_FORMAT(created_at, '%Y-%m-%d') FROM orders",
            SqlAgentToolType.MsSqlServer,
            SqlAgentToolType.MySQL));

        Assert.Contains("DATE_FORMAT", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("source dialect MsSqlServer", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_GroupConcat_WithPostgresSource_IsRejectedBeforeTargetTranslation()
    {
        var ex = Assert.Throws<SqlCompilationException>(() => CompileQuery(
            "SELECT GROUP_CONCAT(name) FROM users",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.MySQL));

        Assert.Contains("GROUP_CONCAT", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("source dialect Postgres", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_CurrentDate_WithSqlServerSource_IsRejectedButCurrentTimestampIsAllowed()
    {
        var ex = Assert.Throws<SqlCompilationException>(() => CompileQuery(
            "SELECT CURRENT_DATE FROM orders",
            SqlAgentToolType.MsSqlServer,
            SqlAgentToolType.Postgres));
        Assert.Contains("CURRENT_DATE", ex.Message, StringComparison.OrdinalIgnoreCase);

        var command = CompileQuery(
            "SELECT CURRENT_TIMESTAMP FROM orders",
            SqlAgentToolType.MsSqlServer,
            SqlAgentToolType.Postgres);
        Assert.Contains("CURRENT_TIMESTAMP", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_PostgresIntervalLiteral_RemainsValidRawSourceSyntax()
    {
        var command = CompileQuery(
            "SELECT INTERVAL '1 day'",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres);

        Assert.Contains("INTERVAL '1 day'", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void Compile_PostgresStyleIntervalLiteral_IsRejectedForOtherRawSourceDialects(
        SqlAgentToolType sourceDialect)
    {
        var ex = Assert.Throws<SqlCompilationException>(() => CompileQuery(
            "SELECT INTERVAL '1 day'",
            sourceDialect,
            SqlAgentToolType.Postgres));

        Assert.Contains("INTERVAL", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"source dialect {sourceDialect}", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PostgreSQL", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    public void Compile_Limit_RemainsValidForDeclaredRawSourceDialects(SqlAgentToolType sourceDialect)
    {
        var command = CompileQuery(
            "SELECT id FROM users LIMIT 5",
            sourceDialect,
            SqlAgentToolType.Postgres);

        Assert.Contains("LIMIT", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void Compile_Limit_IsRejectedForRawSourcesWithoutLimitSpelling(SqlAgentToolType sourceDialect)
    {
        var ex = Assert.Throws<SqlParseException>(() => CompileQuery(
            "SELECT id FROM users LIMIT 5",
            sourceDialect,
            SqlAgentToolType.Postgres));

        Assert.Contains("LIMIT", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(sourceDialect.ToString(), ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("row-limiting", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_PostgresBareOffset_RemainsValidRawSourceSyntax()
    {
        var command = CompileQuery(
            "SELECT id FROM users OFFSET 5",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres);

        Assert.Contains("OFFSET", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    public void Compile_BareOffset_RequiresLimitForMySqlAndSqlite(SqlAgentToolType sourceDialect)
    {
        var ex = Assert.Throws<SqlParseException>(() => CompileQuery(
            "SELECT id FROM users OFFSET 5",
            sourceDialect,
            SqlAgentToolType.Postgres));

        Assert.Contains("OFFSET", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("preceding LIMIT", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(sourceDialect.ToString(), ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    public void Compile_LimitOffset_RemainsValidForMySqlAndSqliteRawSource(SqlAgentToolType sourceDialect)
    {
        var command = CompileQuery(
            "SELECT id FROM users LIMIT 10 OFFSET 5",
            sourceDialect,
            SqlAgentToolType.Postgres);

        Assert.Contains("LIMIT", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OFFSET", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MsSqlServer, "SELECT id FROM users ORDER BY id OFFSET 5 ROWS")]
    [InlineData(SqlAgentToolType.Oracle, "SELECT id FROM users OFFSET 5 ROWS")]
    [InlineData(SqlAgentToolType.Firebird, "SELECT id FROM users OFFSET 5 ROWS")]
    public void Compile_NativeOffsetShape_NormalizesForModeledRawDialects(
        SqlAgentToolType sourceDialect,
        string sql)
    {
        var command = CompileQuery(
            sql,
            sourceDialect,
            SqlAgentToolType.Postgres);

        Assert.Contains("OFFSET", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_SqlServerTop_RemainsPortableAfterLimitSourceGuard()
    {
        var command = CompileQuery(
            "SELECT TOP 5 id FROM users",
            SqlAgentToolType.MsSqlServer,
            SqlAgentToolType.Postgres);

        Assert.Contains("LIMIT", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TOP", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_QuotedLimitIdentifier_DoesNotTriggerRawLimitGuard()
    {
        var command = CompileQuery(
            "SELECT [LIMIT] FROM users",
            SqlAgentToolType.MsSqlServer,
            SqlAgentToolType.MsSqlServer);

        Assert.NotEmpty(command.Sql);
    }

    [Fact]
    public void Compile_MySqlNullOrdering_IsRejectedAsRawSourceSyntax()
    {
        var ex = Assert.Throws<SqlCompilationException>(() => CompileQuery(
            "SELECT amount FROM orders ORDER BY amount NULLS FIRST",
            SqlAgentToolType.MySQL,
            SqlAgentToolType.Postgres));

        Assert.Contains("NULLS FIRST", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("source dialect MySQL", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_SqlServerNullOrdering_IsRejectedAsRawSourceSyntax()
    {
        var ex = Assert.Throws<SqlCompilationException>(() => CompileQuery(
            "SELECT amount FROM orders ORDER BY amount DESC NULLS LAST",
            SqlAgentToolType.MsSqlServer,
            SqlAgentToolType.Postgres));

        Assert.Contains("NULLS LAST", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("source dialect MsSqlServer", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_MySqlWindowNullOrdering_IsRejectedThroughSharedTraversal()
    {
        var ex = Assert.Throws<SqlCompilationException>(() => CompileQuery(
            "SELECT ROW_NUMBER() OVER (ORDER BY amount NULLS FIRST) FROM orders",
            SqlAgentToolType.MySQL,
            SqlAgentToolType.Postgres));

        Assert.Contains("NULLS FIRST", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("source dialect MySQL", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_DeepCaseFunction_UsesSharedSourceDialectBoundary()
    {
        var ex = Assert.Throws<SqlCompilationException>(() => CompileQuery(
            "SELECT CASE WHEN id > 0 THEN COALESCE(DATE_FORMAT(created_at, '%Y'), 'n/a') ELSE 'none' END FROM orders",
            SqlAgentToolType.MsSqlServer,
            SqlAgentToolType.MySQL));

        Assert.Contains("DATE_FORMAT", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("source dialect MsSqlServer", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_NestedScalarSubqueryStatementNullOrdering_IsStillRejected()
    {
        var ex = Assert.Throws<SqlCompilationException>(() => CompileQuery(
            "SELECT (SELECT amount FROM orders ORDER BY amount NULLS FIRST LIMIT 1) FROM users",
            SqlAgentToolType.MySQL,
            SqlAgentToolType.Postgres));

        Assert.Contains("NULLS FIRST", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("source dialect MySQL", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_Dml_UsesTheSameSourceDialectBoundary()
    {
        var ex = Assert.Throws<SqlCompilationException>(() =>
            CoreDmlCompiler.CreateDefault().Compile(
                CoreSqlTextParser.ParseDml(
                    "DELETE FROM orders WHERE DATE_FORMAT(created_at, '%Y') = '2026'",
                    SqlAgentToolType.MsSqlServer),
                SqlAgentToolType.MySQL,
                new SqlPlanValidationContext("policy-v1"),
                new DmlCompilationPolicy()));

        Assert.Contains("DATE_FORMAT", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("source dialect MsSqlServer", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_DmlRejectsPostgresStyleIntervalForMySqlSource()
    {
        var ex = Assert.Throws<SqlCompilationException>(() =>
            CoreDmlCompiler.CreateDefault().Compile(
                CoreSqlTextParser.ParseDml(
                    "UPDATE orders SET expires_at = created_at + INTERVAL '1 day' WHERE id = 9",
                    SqlAgentToolType.MySQL),
                SqlAgentToolType.Postgres,
                new SqlPlanValidationContext("policy-v1"),
                new DmlCompilationPolicy()));

        Assert.Contains("INTERVAL", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("source dialect MySQL", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseDml_InsertSelectLimit_UsesTheSameRawSourceBoundary()
    {
        var ex = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseDml(
                "INSERT INTO archived_users (id) SELECT id FROM users LIMIT 5",
                SqlAgentToolType.Oracle));

        Assert.Contains("LIMIT", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Oracle", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseDml_InsertSelectBareOffset_PropagatesSourceDialect()
    {
        var ex = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseDml(
                "INSERT INTO archived_users (id) SELECT id FROM users OFFSET 5",
                SqlAgentToolType.MySQL));

        Assert.Contains("OFFSET", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("preceding LIMIT", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MySQL", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static CompiledSqlCommand CompileQuery(
        string sql,
        SqlAgentToolType sourceDialect,
        SqlAgentToolType targetProvider) =>
        CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(sql, sourceDialect),
            targetProvider,
            new SqlPlanValidationContext("policy-v1"),
            new SqlExecutionPlanPolicy());
}
