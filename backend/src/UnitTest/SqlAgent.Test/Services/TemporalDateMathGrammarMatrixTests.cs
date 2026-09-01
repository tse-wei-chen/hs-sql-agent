using Xunit;

namespace SqlAgent.Test.Services;

public sealed class TemporalDateMathGrammarMatrixTests
{
    public const int ExpectedDateAddCaseCount = 144;
    public const int ExpectedSourceGrammarCaseCount = 8;
    public const int ExpectedPositiveCaseCount =
        ExpectedDateAddCaseCount + ExpectedSourceGrammarCaseCount;

    private static readonly SqlAgentToolType[] Targets =
    [
        SqlAgentToolType.Postgres,
        SqlAgentToolType.MySQL,
        SqlAgentToolType.Sqlite,
        SqlAgentToolType.MsSqlServer,
        SqlAgentToolType.Oracle,
        SqlAgentToolType.Firebird
    ];

    private static readonly string[] Units =
    [
        "DAY", "WEEK", "MONTH", "QUARTER",
        "YEAR", "HOUR", "MINUTE", "SECOND"
    ];

    private static readonly string[] Contexts =
    [
        "select", "predicate", "order"
    ];

    public static IEnumerable<object[]> DateAddCases()
    {
        foreach (var target in Targets)
        foreach (var unit in Units)
        foreach (var context in Contexts)
            yield return [target, unit, context];
    }

    [Theory]
    [MemberData(nameof(DateAddCases))]
    public void DateAdd_SixProviderGrammarMatrix_ParsesBindsValidatesCompilesAndRenders(
        SqlAgentToolType targetProvider,
        string unit,
        string context)
    {
        var expression = $"DATEADD({unit}, 2, created_at)";
        var sql = context switch
        {
            "select" => $"SELECT {expression} AS shifted FROM events",
            "predicate" => $"SELECT id FROM events WHERE {expression} > created_at",
            "order" => $"SELECT id FROM events ORDER BY {expression}",
            _ => throw new ArgumentOutOfRangeException(nameof(context))
        };

        var command = Compile(sql, SqlAgentToolType.MsSqlServer, targetProvider);

        Assert.False(string.IsNullOrWhiteSpace(command.Sql));
        Assert.DoesNotContain("CORE_DATE_ADD", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("TIMESTAMPADD(HOUR, 2, created_at)", "TIMESTAMPADD(HOUR")]
    [InlineData("TIMESTAMPADD(QUARTER, 2, created_at)", "TIMESTAMPADD(QUARTER")]
    [InlineData("TIMESTAMPDIFF(HOUR, created_at, updated_at)", "TIMESTAMPDIFF(HOUR")]
    [InlineData("TIMESTAMPDIFF(QUARTER, created_at, updated_at)", "TIMESTAMPDIFF(QUARTER")]
    public void MySqlNativeTimestampMath_SourceGrammar_IsModeled(
        string expression,
        string marker)
    {
        var command = Compile(
            $"SELECT {expression} FROM events",
            SqlAgentToolType.MySQL,
            SqlAgentToolType.MySQL);

        Assert.Contains(marker, command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("DATEPART(HOUR, created_at)", "DATEPART(HOUR")]
    [InlineData("DATEPART(QUARTER, created_at)", "DATEPART(QUARTER")]
    public void SqlServerDatePart_SourceGrammar_IsModeled(
        string expression,
        string marker)
    {
        var command = Compile(
            $"SELECT {expression} FROM events",
            SqlAgentToolType.MsSqlServer,
            SqlAgentToolType.MsSqlServer);

        Assert.Contains(marker, command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("'HOUR'", "DATEPART(HOUR")]
    [InlineData("'QUARTER'", "DATEPART(QUARTER")]
    public void PostgresDatePartFunction_CrossLowersToSqlServer(
        string part,
        string marker)
    {
        var command = Compile(
            $"SELECT DATE_PART({part}, created_at) FROM events",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.MsSqlServer);

        Assert.Contains(marker, command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    private static CompiledSqlCommand Compile(
        string sql,
        SqlAgentToolType sourceDialect,
        SqlAgentToolType targetProvider) =>
        CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(sql, sourceDialect),
            targetProvider,
            new SqlPlanValidationContext("temporal-date-math-matrix-v1"),
            new SqlExecutionPlanPolicy());
}
