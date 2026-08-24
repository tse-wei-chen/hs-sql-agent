using SqlAgent.Service.Core.Compilation;
using SqlAgent.Service.Core.Pipeline;
using SqlAgent.Service.Enums;
using SqlAgent.Service.SqlParsing;
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
    public void Compile_PostgresNonDefaultNullOrdering_RemainsFailClosed(
        SqlAgentToolType targetProvider,
        string ordering)
    {
        var ex = Assert.Throws<SqlCompilationException>(() => CompileQuery(
            $"SELECT name FROM users ORDER BY name {ordering}",
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
    public void Compile_InsertSelectNonDefaultNullOrdering_RemainsFailClosed()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "INSERT INTO user_archive (id) SELECT id FROM users ORDER BY id ASC NULLS LAST LIMIT 1",
            SqlAgentToolType.Postgres);

        var ex = Assert.Throws<SqlCompilationException>(() =>
            CoreDmlCompiler.CreateDefault().Compile(
                parsed,
                SqlAgentToolType.MySQL,
                new SqlPlanValidationContext("policy-v1"),
                new DmlCompilationPolicy()));

        Assert.Contains("ordering.nulls", ex.Message, StringComparison.OrdinalIgnoreCase);
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
