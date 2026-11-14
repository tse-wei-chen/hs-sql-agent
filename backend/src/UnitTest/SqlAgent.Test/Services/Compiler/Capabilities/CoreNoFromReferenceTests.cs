using Xunit;

namespace SqlAgent.Test.Services;

public class CoreNoFromReferenceTests
{
    private static readonly SqlAgentToolType[] Providers =
    [
        SqlAgentToolType.Sqlite,
        SqlAgentToolType.Postgres,
        SqlAgentToolType.MySQL,
        SqlAgentToolType.MsSqlServer,
        SqlAgentToolType.Oracle,
        SqlAgentToolType.Firebird
    ];

    [Theory]
    [MemberData(nameof(AllProviders))]
    public void Compile_NoFromFreeColumn_FailsAtCoreBoundary(SqlAgentToolType provider)
    {
        var ex = Assert.Throws<SqlCompilationException>(() => CompileQuery(
            "SELECT missing_column",
            SqlAgentToolType.Postgres,
            provider));

        Assert.Contains("missing_column", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("requires a FROM source", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(AllProviders))]
    public void Compile_NoFromWildcard_FailsInsteadOfReadingProviderDummyTable(SqlAgentToolType provider)
    {
        var ex = Assert.Throws<SqlCompilationException>(() => CompileQuery(
            "SELECT *",
            SqlAgentToolType.Postgres,
            provider));

        Assert.Contains("requires a FROM source", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(AllProviders))]
    public void Compile_NoFromCountWildcard_PreservesSingletonAggregate(SqlAgentToolType provider)
    {
        var command = CompileQuery(
            "SELECT COUNT(*) AS row_count",
            SqlAgentToolType.Postgres,
            provider);

        Assert.Contains("COUNT(*)", command.Sql, StringComparison.OrdinalIgnoreCase);
        if (provider == SqlAgentToolType.Oracle)
            Assert.Contains("FROM DUAL", command.Sql, StringComparison.OrdinalIgnoreCase);
        if (provider == SqlAgentToolType.Firebird)
            Assert.Contains("FROM RDB$DATABASE", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(AllProviders))]
    public void Compile_NoFromProjectionAlias_RemainsValidInOrderBy(SqlAgentToolType provider)
    {
        var command = CompileQuery(
            "SELECT 1 AS value ORDER BY value",
            SqlAgentToolType.Postgres,
            provider);

        Assert.Contains("ORDER BY", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("value", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(AllProviders))]
    public void Compile_NoFromCorrelatedSubquery_PreservesOuterColumnReference(SqlAgentToolType provider)
    {
        var command = CompileQuery(
            "SELECT (SELECT u.id) AS copy_id FROM users u",
            SqlAgentToolType.Postgres,
            provider);

        Assert.Contains("copy_id", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("id", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_DmlCorrelatedNoFromScalarSubquery_RemainsValid()
    {
        var command = CoreDmlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseDml(
                "UPDATE users SET score = (SELECT users.score + 1) WHERE id = 7",
                SqlAgentToolType.Postgres),
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("policy-v1"),
            new DmlCompilationPolicy());

        Assert.Equal(SqlStatementKind.Update, command.Kind);
        Assert.Contains("SELECT", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("score", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_DmlNoFromWildcardSubquery_FailsAtCoreBoundary()
    {
        var ex = Assert.Throws<SqlCompilationException>(() => CoreDmlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseDml(
                "UPDATE users SET score = (SELECT *) WHERE id = 7",
                SqlAgentToolType.Postgres),
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("policy-v1"),
            new DmlCompilationPolicy()));

        Assert.Contains("requires a FROM source", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    public static IEnumerable<object[]> AllProviders() =>
        Providers.Select(provider => new object[] { provider });

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
