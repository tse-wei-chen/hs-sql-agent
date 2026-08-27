using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreCurrentTemporalCapabilityTests
{
    private static readonly SqlAgentToolType[] Providers =
    [
        SqlAgentToolType.Postgres,
        SqlAgentToolType.MySQL,
        SqlAgentToolType.Sqlite,
        SqlAgentToolType.MsSqlServer,
        SqlAgentToolType.Oracle,
        SqlAgentToolType.Firebird
    ];

    [Theory]
    [InlineData(SqlAgentToolType.Postgres, SqlCapabilityStatus.Supported)]
    [InlineData(SqlAgentToolType.MySQL, SqlCapabilityStatus.Supported)]
    [InlineData(SqlAgentToolType.Sqlite, SqlCapabilityStatus.Supported)]
    [InlineData(SqlAgentToolType.MsSqlServer, SqlCapabilityStatus.Supported)]
    [InlineData(SqlAgentToolType.Oracle, SqlCapabilityStatus.Translated)]
    [InlineData(SqlAgentToolType.Firebird, SqlCapabilityStatus.Supported)]
    public void Matrix_PreservesCurrentKeywordContract(
        SqlAgentToolType provider,
        SqlCapabilityStatus expected)
    {
        var capability = Assert.Single(
            SqlCapabilityMatrix.ForProvider(provider).Capabilities,
            item => item.Id == "temporal.current_keywords");
        Assert.Equal(expected, capability.Status);
    }

    [Fact]
    public void RawCurrentDate_IsRejectedOnlyForSqlServerSource()
    {
        foreach (var sourceDialect in Providers)
        {
            if (sourceDialect == SqlAgentToolType.MsSqlServer)
            {
                var ex = Assert.Throws<SqlCompilationException>(() => CompileQuery(
                    "SELECT CURRENT_DATE FROM orders", sourceDialect, SqlAgentToolType.Postgres));
                Assert.Contains("CURRENT_DATE", ex.Message, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("Transact-SQL", ex.Message, StringComparison.OrdinalIgnoreCase);
                continue;
            }
            var command = CompileQuery("SELECT CURRENT_DATE FROM orders", sourceDialect, SqlAgentToolType.Postgres);
            Assert.Contains("CURRENT_DATE", command.Sql, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void RawCurrentTime_IsRejectedOnlyForSqlServerAndOracleSources()
    {
        foreach (var sourceDialect in Providers)
        {
            var rejected = sourceDialect is SqlAgentToolType.MsSqlServer or SqlAgentToolType.Oracle;
            if (rejected)
            {
                var ex = Assert.Throws<SqlCompilationException>(() => CompileQuery(
                    "SELECT CURRENT_TIME FROM orders", sourceDialect, SqlAgentToolType.Postgres));
                Assert.Contains("CURRENT_TIME", ex.Message, StringComparison.OrdinalIgnoreCase);
                Assert.Contains(sourceDialect.ToString(), ex.Message, StringComparison.OrdinalIgnoreCase);
                continue;
            }
            var command = CompileQuery("SELECT CURRENT_TIME FROM orders", sourceDialect, SqlAgentToolType.Postgres);
            Assert.Contains("CURRENT_TIME", command.Sql, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void RawCurrentTimestamp_IsAcceptedForEveryDeclaredSourceDialect()
    {
        foreach (var sourceDialect in Providers)
        {
            var command = CompileQuery("SELECT CURRENT_TIMESTAMP FROM orders", sourceDialect, SqlAgentToolType.Postgres);
            Assert.Contains("CURRENT_TIMESTAMP", command.Sql, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void CurrentTime_TargetIsRejectedOnlyForOracle()
    {
        foreach (var targetProvider in Providers)
        {
            if (targetProvider == SqlAgentToolType.Oracle)
            {
                var ex = Assert.Throws<SqlCompilationException>(() => CompileQuery(
                    "SELECT CURRENT_TIME FROM orders", SqlAgentToolType.Postgres, targetProvider));
                Assert.Contains("function.current_time", ex.Message, StringComparison.OrdinalIgnoreCase);
                continue;
            }
            var command = CompileQuery("SELECT CURRENT_TIME FROM orders", SqlAgentToolType.Postgres, targetProvider);
            Assert.NotEmpty(command.Sql);
        }
    }

    [Fact]
    public void SqlServerTarget_TranslatesCanonicalCurrentDateAndTime()
    {
        var date = CompileQuery("SELECT CURRENT_DATE FROM orders", SqlAgentToolType.Postgres, SqlAgentToolType.MsSqlServer);
        Assert.Contains("CURRENT_TIMESTAMP", date.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("date", date.Sql, StringComparison.OrdinalIgnoreCase);

        var time = CompileQuery("SELECT CURRENT_TIME FROM orders", SqlAgentToolType.Postgres, SqlAgentToolType.MsSqlServer);
        Assert.Contains("CURRENT_TIMESTAMP", time.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("time", time.Sql, StringComparison.OrdinalIgnoreCase);
    }

    private static CompiledSqlCommand CompileQuery(
        string sql,
        SqlAgentToolType sourceDialect,
        SqlAgentToolType targetProvider) =>
        CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(sql, sourceDialect),
            targetProvider,
            new SqlPlanValidationContext("current-temporal-test"),
            new SqlExecutionPlanPolicy());
}
