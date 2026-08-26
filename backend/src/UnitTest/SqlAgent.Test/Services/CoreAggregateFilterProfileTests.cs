using Xunit;

namespace SqlAgent.Test.Services;

public class CoreAggregateFilterProfileTests
{
    private const string FilterSql =
        "SELECT SUM(amount) FILTER (WHERE status = 'open') FROM orders";

    [Fact]
    public void Compile_SqliteFilterWithoutSourceVersion_FailsClosed()
    {
        var ex = Assert.Throws<SqlCompilationException>(() => CompileRaw(
            FilterSql,
            SqlAgentToolType.Sqlite,
            SqlAgentToolType.Postgres));

        Assert.Contains("expression.filter", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Sqlite source", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("3.30", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_SqliteFilterWithSupportedSourceButMissingTargetVersion_FailsClosed()
    {
        var ex = Assert.Throws<SqlCompilationException>(() => CompileRaw(
            FilterSql,
            SqlAgentToolType.Sqlite,
            SqlAgentToolType.Sqlite,
            Profile(SqlAgentToolType.Sqlite, 3, 30)));

        Assert.Contains("Sqlite target", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("3.30", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_SqliteFilterWithDeclaredSupportedVersions_Compiles()
    {
        var profile = Profile(SqlAgentToolType.Sqlite, 3, 30);
        var command = CompileRaw(
            FilterSql,
            SqlAgentToolType.Sqlite,
            SqlAgentToolType.Sqlite,
            profile,
            profile);

        Assert.Contains("FILTER (WHERE", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_FirebirdFilterWithOldSourceVersion_FailsClosed()
    {
        var ex = Assert.Throws<SqlCompilationException>(() => CompileRaw(
            FilterSql,
            SqlAgentToolType.Firebird,
            SqlAgentToolType.Postgres,
            Profile(SqlAgentToolType.Firebird, 3, 0)));

        Assert.Contains("Firebird source", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("4.0", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_FirebirdFilterWithoutTargetVersion_FailsClosed()
    {
        var ex = Assert.Throws<SqlCompilationException>(() => CompileRaw(
            FilterSql,
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Firebird));

        Assert.Contains("Firebird target", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("4.0", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_FirebirdFilterWithDeclaredSupportedTargetVersion_Compiles()
    {
        var command = CompileRaw(
            FilterSql,
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Firebird,
            targetProfile: Profile(SqlAgentToolType.Firebird, 4, 0));

        Assert.Contains("FILTER (WHERE", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_PostgresFilterWithExplicitPreNineFourSource_FailsClosed()
    {
        var ex = Assert.Throws<SqlCompilationException>(() => CompileRaw(
            FilterSql,
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres,
            Profile(SqlAgentToolType.Postgres, 9, 3)));

        Assert.Contains("Postgres source", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("9.4", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_PostgresFilterWithExplicitPreNineFourTarget_FailsClosed()
    {
        var ex = Assert.Throws<SqlCompilationException>(() => CompileRaw(
            FilterSql,
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres,
            targetProfile: Profile(SqlAgentToolType.Postgres, 9, 3)));

        Assert.Contains("Postgres target", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("9.4", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_PostgresFilterWithoutVersionProfile_RemainsSupported()
    {
        var command = CompileRaw(
            FilterSql,
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres);

        Assert.Contains("FILTER (WHERE", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_Oracle26Filter_RemainsFailClosedUntilPredicateRestrictionsAreModeled()
    {
        var ex = Assert.Throws<SqlCompilationException>(() => CompileRaw(
            FilterSql,
            SqlAgentToolType.Oracle,
            SqlAgentToolType.Postgres,
            Profile(SqlAgentToolType.Oracle, 26, 0)));

        Assert.Contains("Oracle source", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("26ai", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("filter-condition", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_DmlInsertSelectFilter_UsesTheSameSourceVersionBoundary()
    {
        var ex = Assert.Throws<SqlCompilationException>(() =>
            CoreDmlCompiler.CreateDefault().Compile(
                CoreSqlTextParser.ParseDml(
                    "INSERT INTO order_totals (amount) SELECT SUM(amount) FILTER (WHERE status = 'open') FROM orders",
                    SqlAgentToolType.Sqlite),
                SqlAgentToolType.Postgres,
                new SqlPlanValidationContext("policy-v1"),
                new DmlCompilationPolicy()));

        Assert.Contains("Sqlite source", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("3.30", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static SqlProviderCapabilityProfile Profile(
        SqlAgentToolType provider,
        int major,
        int minor) =>
        new(provider, new Version(major, minor));

    private static CompiledSqlCommand CompileRaw(
        string sql,
        SqlAgentToolType sourceDialect,
        SqlAgentToolType targetProvider,
        SqlProviderCapabilityProfile? sourceProfile = null,
        SqlProviderCapabilityProfile? targetProfile = null) =>
        CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(sql, sourceDialect, sourceProfile),
            targetProvider,
            new SqlPlanValidationContext("policy-v1"),
            new SqlExecutionPlanPolicy(),
            targetProfile);
}
