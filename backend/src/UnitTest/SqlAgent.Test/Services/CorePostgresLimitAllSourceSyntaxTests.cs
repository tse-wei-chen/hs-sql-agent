using SqlAgent.Service.Core.Ast;
using SqlAgent.Service.Core.Pipeline;
using SqlAgent.Service.Enums;
using SqlAgent.Service.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CorePostgresLimitAllSourceSyntaxTests
{
    [Fact]
    public void ParseQuery_LimitAll_NormalizesToNoCanonicalLimit()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT id FROM users LIMIT ALL",
            SqlAgentToolType.Postgres);
        var select = Assert.IsType<SelectStatement>(parsed.Statement);

        Assert.Null(select.Limit);
        Assert.Null(select.Offset);

        var command = Compile(parsed, SqlAgentToolType.Postgres);
        Assert.DoesNotContain("LIMIT", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseQuery_LimitAllOffset_PreservesOnlyOffset()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT id FROM users LIMIT ALL OFFSET 5",
            SqlAgentToolType.Postgres);
        var select = Assert.IsType<SelectStatement>(parsed.Statement);

        Assert.Null(select.Limit);
        Assert.Equal(5, select.Offset);

        var command = Compile(parsed, SqlAgentToolType.Postgres);
        Assert.DoesNotContain("LIMIT", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OFFSET", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    public void ParseQuery_LimitAll_RemainsRejectedForOtherLimitDialects(SqlAgentToolType sourceDialect)
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery(
                "SELECT id FROM users LIMIT ALL",
                sourceDialect));

        Assert.Contains("LIMIT ALL", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("only for PostgreSQL", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(sourceDialect.ToString(), error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseQuery_LimitAllAndFetch_RemainMutuallyExclusive()
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery(
                "SELECT id FROM users LIMIT ALL FETCH FIRST 3 ROWS ONLY",
                SqlAgentToolType.Postgres));

        Assert.Contains("LIMIT and FETCH", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cannot be combined", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseQuery_LimitAllOffsetAndFetch_RemainMutuallyExclusive()
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery(
                "SELECT id FROM users LIMIT ALL OFFSET 5 ROWS FETCH NEXT 3 ROWS ONLY",
                SqlAgentToolType.Postgres));

        Assert.Contains("LIMIT and FETCH", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cannot be combined", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseDml_InsertSelectLimitAllOffset_UsesSameSourceDialect()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "INSERT INTO archived_users (id) SELECT id FROM users LIMIT ALL OFFSET 5",
            SqlAgentToolType.Postgres);
        var insert = Assert.IsType<InsertStatement>(parsed.Statement);
        var source = Assert.IsType<InsertQuerySource>(insert.Source);
        var select = Assert.IsType<SelectStatement>(source.Query);

        Assert.Null(select.Limit);
        Assert.Equal(5, select.Offset);
    }

    private static SqlAgent.Service.Core.Compilation.CompiledSqlCommand Compile(
        ParsedStatement parsed,
        SqlAgentToolType targetProvider) =>
        CoreSqlCompiler.CreateDefault().Compile(
            parsed,
            targetProvider,
            new SqlPlanValidationContext("policy-v1"),
            new SqlExecutionPlanPolicy());
}
