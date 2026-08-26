using Xunit;

namespace SqlAgent.Test.Services;

public class CoreNullOrderingTranslationTests
{
    [Theory]
    [InlineData(SqlAgentToolType.MySQL, "ASC NULLS FIRST")]
    [InlineData(SqlAgentToolType.MySQL, "DESC NULLS LAST")]
    [InlineData(SqlAgentToolType.MsSqlServer, "ASC NULLS FIRST")]
    [InlineData(SqlAgentToolType.MsSqlServer, "DESC NULLS LAST")]
    public void Compile_PostgresDefaultEquivalentNullOrdering_DropsUnsupportedModifier(
        SqlAgentToolType targetProvider,
        string ordering)
    {
        var command = CompileQuery(
            $"SELECT name FROM users ORDER BY name {ordering}",
            SqlAgentToolType.Postgres,
            targetProvider);

        Assert.Contains("ORDER BY", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("NULLS FIRST", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("NULLS LAST", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL, "ASC NULLS LAST")]
    [InlineData(SqlAgentToolType.MySQL, "DESC NULLS FIRST")]
    [InlineData(SqlAgentToolType.MsSqlServer, "ASC NULLS LAST")]
    [InlineData(SqlAgentToolType.MsSqlServer, "DESC NULLS FIRST")]
    public void Compile_PostgresInverseNullOrderingOnDirectColumn_UsesNullRankRewrite(
        SqlAgentToolType targetProvider,
        string ordering)
    {
        var command = CompileQuery(
            $"SELECT name FROM users ORDER BY name {ordering}",
            SqlAgentToolType.Postgres,
            targetProvider);

        Assert.Contains("ORDER BY", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CASE", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("IS NULL", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("NULLS FIRST", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("NULLS LAST", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    public void Compile_QualifiedDirectColumnInverseNullOrdering_WithMultipleSources_UsesNullRankRewrite(
        SqlAgentToolType targetProvider)
    {
        var command = CompileQuery(
            "SELECT u.name FROM users AS u JOIN teams AS t ON t.id = u.team_id ORDER BY u.name ASC NULLS LAST",
            SqlAgentToolType.Postgres,
            targetProvider);

        Assert.Contains("CASE", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("IS NULL", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("NULLS LAST", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    public void Compile_ComputedInverseNullOrdering_RemainsFailClosed(SqlAgentToolType targetProvider)
    {
        var ex = Assert.Throws<SqlCompilationException>(() => CompileQuery(
            "SELECT name FROM users ORDER BY LOWER(name) ASC NULLS LAST",
            SqlAgentToolType.Postgres,
            targetProvider));

        Assert.Contains("ordering.nulls", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    public void Compile_DistinctInverseNullOrdering_RemainsFailClosed(SqlAgentToolType targetProvider)
    {
        var ex = Assert.Throws<SqlCompilationException>(() => CompileQuery(
            "SELECT DISTINCT name FROM users ORDER BY name ASC NULLS LAST",
            SqlAgentToolType.Postgres,
            targetProvider));

        Assert.Contains("ordering.nulls", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    public void Compile_ProjectionAliasInverseNullOrdering_RemainsFailClosed(SqlAgentToolType targetProvider)
    {
        var ex = Assert.Throws<SqlCompilationException>(() => CompileQuery(
            "SELECT name AS label FROM users ORDER BY label ASC NULLS LAST",
            SqlAgentToolType.Postgres,
            targetProvider));

        Assert.Contains("ordering.nulls", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    public void Compile_SetTailInverseNullOrdering_RemainsFailClosed(SqlAgentToolType targetProvider)
    {
        var ex = Assert.Throws<SqlCompilationException>(() => CompileQuery(
            "SELECT name FROM users UNION ALL SELECT name FROM archived_users ORDER BY name ASC NULLS LAST",
            SqlAgentToolType.Postgres,
            targetProvider));

        Assert.Contains("ordering.nulls", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    public void Compile_WindowDefaultEquivalentNullOrdering_IsCanonicalizedRecursively(
        SqlAgentToolType targetProvider)
    {
        var command = CompileQuery(
            "SELECT ROW_NUMBER() OVER (ORDER BY name ASC NULLS FIRST) AS rn FROM users",
            SqlAgentToolType.Postgres,
            targetProvider);

        Assert.Contains("OVER", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ORDER BY", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("NULLS FIRST", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    public void Compile_WindowInverseNullOrderingOnDirectColumn_UsesNullRankRewrite(
        SqlAgentToolType targetProvider)
    {
        var command = CompileQuery(
            "SELECT ROW_NUMBER() OVER (ORDER BY name DESC NULLS FIRST) AS rn FROM users",
            SqlAgentToolType.Postgres,
            targetProvider);

        Assert.Contains("OVER", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CASE", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("IS NULL", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("NULLS FIRST", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_InsertSelectDefaultEquivalentNullOrdering_UsesSharedDmlRewrite()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "INSERT INTO user_archive (id) SELECT id FROM users ORDER BY id ASC NULLS FIRST LIMIT 1",
            SqlAgentToolType.Postgres);

        var command = CoreDmlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.MySQL,
            new SqlPlanValidationContext("policy-v1"),
            new DmlCompilationPolicy());

        Assert.Contains("INSERT", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ORDER BY", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("NULLS FIRST", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_UpdateScalarSubqueryDefaultEquivalentNullOrdering_UsesSharedDmlRewrite()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "UPDATE users SET manager_id = (SELECT id FROM managers ORDER BY id ASC NULLS FIRST LIMIT 1) WHERE id = 7",
            SqlAgentToolType.Postgres);

        var command = CoreDmlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.MySQL,
            new SqlPlanValidationContext("policy-v1"),
            new DmlCompilationPolicy());

        Assert.Contains("UPDATE", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SELECT", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ORDER BY", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("NULLS FIRST", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_InsertSelectInverseNullOrderingOnDirectColumn_UsesSharedDmlRewrite()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "INSERT INTO user_archive (id) SELECT id FROM users ORDER BY id ASC NULLS LAST LIMIT 1",
            SqlAgentToolType.Postgres);

        var command = CoreDmlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.MySQL,
            new SqlPlanValidationContext("policy-v1"),
            new DmlCompilationPolicy());

        Assert.Contains("INSERT", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CASE", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("IS NULL", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("NULLS LAST", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_UpdateScalarSubqueryInverseNullOrderingOnDirectColumn_UsesSharedDmlRewrite()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "UPDATE users SET manager_id = (SELECT id FROM managers ORDER BY id DESC NULLS FIRST LIMIT 1) WHERE id = 7",
            SqlAgentToolType.Postgres);

        var command = CoreDmlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.MySQL,
            new SqlPlanValidationContext("policy-v1"),
            new DmlCompilationPolicy());

        Assert.Contains("UPDATE", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CASE", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("IS NULL", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("NULLS FIRST", command.Sql, StringComparison.OrdinalIgnoreCase);
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
