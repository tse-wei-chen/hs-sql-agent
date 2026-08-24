using SqlAgent.Service.Core.Ast;
using SqlAgent.Service.Core.Pipeline;
using SqlAgent.Service.Enums;
using SqlAgent.Service.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreStandardOffsetFetchSourceSyntaxTests
{
    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void ParseQuery_StandardFetchWithoutOffset_NormalizesToLimit(SqlAgentToolType sourceDialect)
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT id FROM users FETCH FIRST 7 ROWS ONLY",
            sourceDialect);
        var select = Assert.IsType<SelectStatement>(parsed.Statement);

        Assert.Equal(7, select.Limit);
        Assert.Null(select.Offset);

        var command = Compile(parsed, SqlAgentToolType.Postgres);
        Assert.Contains("LIMIT", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void ParseQuery_StandardOffsetFetch_NormalizesToCanonicalTail(SqlAgentToolType sourceDialect)
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT id FROM users OFFSET 5 ROWS FETCH NEXT 10 ROWS ONLY",
            sourceDialect);
        var select = Assert.IsType<SelectStatement>(parsed.Statement);

        Assert.Equal(10, select.Limit);
        Assert.Equal(5, select.Offset);

        var command = Compile(parsed, SqlAgentToolType.Postgres);
        Assert.Contains("LIMIT", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OFFSET", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseQuery_PostgresOffsetCanOmitRowKeywordAndFetchCount()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT id FROM users OFFSET 5 FETCH FIRST ROW ONLY",
            SqlAgentToolType.Postgres);
        var select = Assert.IsType<SelectStatement>(parsed.Statement);

        Assert.Equal(1, select.Limit);
        Assert.Equal(5, select.Offset);
    }

    [Fact]
    public void ParseQuery_SqlServerOffsetFetch_RequiresOrderByAndNormalizes()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT id FROM users ORDER BY id OFFSET 5 ROWS FETCH NEXT 10 ROWS ONLY",
            SqlAgentToolType.MsSqlServer);
        var select = Assert.IsType<SelectStatement>(parsed.Statement);

        Assert.Equal(10, select.Limit);
        Assert.Equal(5, select.Offset);
        Assert.Single(select.OrderBy);

        var command = Compile(parsed, SqlAgentToolType.Postgres);
        Assert.Contains("LIMIT", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OFFSET", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseQuery_SqlServerOffsetWithoutOrderBy_FailsAtSourceBoundary()
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery(
                "SELECT id FROM users OFFSET 5 ROWS",
                SqlAgentToolType.MsSqlServer));

        Assert.Contains("ORDER BY", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OFFSET/FETCH", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseQuery_SqlServerFetchWithoutOffset_FailsAtSourceBoundary()
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery(
                "SELECT id FROM users ORDER BY id FETCH NEXT 10 ROWS ONLY",
                SqlAgentToolType.MsSqlServer));

        Assert.Contains("preceding OFFSET", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseQuery_SqlServerTopCannotShareScopeWithOffsetFetch()
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery(
                "SELECT TOP 3 id FROM users ORDER BY id OFFSET 5 ROWS FETCH NEXT 10 ROWS ONLY",
                SqlAgentToolType.MsSqlServer));

        Assert.Contains("TOP", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OFFSET/FETCH", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    public void ParseQuery_FetchSpelling_RemainsRejectedForLimitDialects(SqlAgentToolType sourceDialect)
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery(
                "SELECT id FROM users FETCH FIRST 5 ROWS ONLY",
                sourceDialect));

        Assert.Contains("FETCH FIRST/NEXT", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(sourceDialect.ToString(), error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    public void ParseQuery_FetchIdentifierAndAlias_RemainAvailableOutsideClauseShape(SqlAgentToolType sourceDialect)
    {
        var columnParsed = CoreSqlTextParser.ParseQuery(
            "SELECT fetch FROM users",
            sourceDialect);
        var column = Assert.IsType<ColumnExpr>(
            Assert.Single(Assert.IsType<SelectStatement>(columnParsed.Statement).Select).Expression);
        Assert.Equal("fetch", Assert.Single(column.Name.Parts).Value, ignoreCase: true);

        var aliasParsed = CoreSqlTextParser.ParseQuery(
            "SELECT id fetch FROM users",
            sourceDialect);
        var alias = Assert.Single(Assert.IsType<SelectStatement>(aliasParsed.Statement).Select).Alias;
        Assert.NotNull(alias);
        Assert.Equal("fetch", alias.Value, ignoreCase: true);

        var tableAliasParsed = CoreSqlTextParser.ParseQuery(
            "SELECT fetch.id FROM users fetch",
            sourceDialect);
        var tableAlias = Assert.IsType<NamedTableSource>(
            Assert.IsType<SelectStatement>(tableAliasParsed.Statement).From).Alias;
        Assert.NotNull(tableAlias);
        Assert.Equal("fetch", tableAlias.Value, ignoreCase: true);
    }

    [Fact]
    public void ParseQuery_FetchPercent_RemainsFailClosed()
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery(
                "SELECT id FROM users FETCH FIRST 10 PERCENT ROWS ONLY",
                SqlAgentToolType.Oracle));

        Assert.Contains("PERCENT", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not represented", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseQuery_FetchWithTies_RemainsFailClosed()
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery(
                "SELECT id FROM users ORDER BY id FETCH FIRST 10 ROWS WITH TIES",
                SqlAgentToolType.Postgres));

        Assert.Contains("WITH TIES", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not represented", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseDml_InsertSelectStandardOffsetFetch_UsesSameSourceDialect()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "INSERT INTO archived_users (id) SELECT id FROM users OFFSET 5 ROWS FETCH NEXT 10 ROWS ONLY",
            SqlAgentToolType.Oracle);
        var insert = Assert.IsType<InsertStatement>(parsed.Statement);
        var source = Assert.IsType<InsertQuerySource>(insert.Source);
        var select = Assert.IsType<SelectStatement>(source.Query);

        Assert.Equal(10, select.Limit);
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
