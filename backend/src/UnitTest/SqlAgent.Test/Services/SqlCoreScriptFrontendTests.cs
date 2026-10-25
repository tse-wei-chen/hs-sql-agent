using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class SqlCoreScriptFrontendTests
{
    [Fact]
    public void Script_ParsesQueryAndMutationWithoutCompilingOrExecuting()
    {
        const string sql = "SELECT 1; INSERT INTO users(id) VALUES (1);";
        var script = SqlCoreFacade.ParseScript(sql, SqlAgentToolType.Postgres);

        Assert.Equal(1, script.BatchCount);
        Assert.Equal(2, script.Statements.Count);
        Assert.Equal(SqlScriptStatementKind.Query, script.Statements[0].Kind);
        Assert.Equal(SqlScriptStatementKind.Mutation, script.Statements[1].Kind);
        Assert.All(script.Statements, statement => Assert.Equal(SqlScriptParseStatus.Parsed, statement.Status));
        Assert.Empty(script.Diagnostics);
        Assert.Equal("SELECT 1", script.Statements[0].Text);
        Assert.Equal("INSERT INTO users(id) VALUES (1)", script.Statements[1].Text);
    }

    [Fact]
    public void Script_RetainsUnimplementedDdlAndDclInsteadOfTreatingThemAsValidated()
    {
        const string sql = "CREATE TABLE demo (id INTEGER); GRANT SELECT ON demo TO reader; SELECT 1";
        var script = SqlCoreFacade.ParseScript(sql, SqlAgentToolType.Postgres);

        Assert.Equal(3, script.Statements.Count);
        Assert.Equal(SqlScriptStatementKind.Schema, script.Statements[0].Kind);
        Assert.Equal(SqlScriptStatementKind.Security, script.Statements[1].Kind);
        Assert.Equal(SqlScriptParseStatus.Unsupported, script.Statements[0].Status);
        Assert.Equal(SqlScriptParseStatus.Unsupported, script.Statements[1].Status);
        Assert.Equal(SqlScriptParseStatus.Parsed, script.Statements[2].Status);
        Assert.Equal(2, script.Diagnostics.Count);
        Assert.All(script.Diagnostics, diagnostic => Assert.Equal("SQL_SCRIPT_UNSUPPORTED_STATEMENT", diagnostic.Code));
    }

    [Fact]
    public void Script_SemicolonsInStringsAndCommentsDoNotSplitStatements()
    {
        const string sql = "/* ; */ SELECT 'left;right' AS value; -- ;\nSELECT 2";
        var script = SqlCoreFacade.ParseScript(sql, SqlAgentToolType.Postgres);

        Assert.Equal(2, script.Statements.Count);
        Assert.All(script.Statements, statement => Assert.Equal(SqlScriptParseStatus.Parsed, statement.Status));
        Assert.Contains("'left;right'", script.Statements[0].Text, StringComparison.Ordinal);
        Assert.Equal("SELECT 2", script.Statements[1].Text);
    }

    [Fact]
    public void Script_DollarQuotedPostgresTextDoesNotSplitStatements()
    {
        const string sql = "SELECT $$left;right$$ AS value; SELECT 2";
        var script = SqlCoreFacade.ParseScript(sql, SqlAgentToolType.Postgres);

        Assert.Equal(2, script.Statements.Count);
        Assert.All(script.Statements, statement => Assert.Equal(SqlScriptParseStatus.Parsed, statement.Status));
    }

    [Fact]
    public void Script_SqlServerGoOnItsOwnLineSeparatesBatches()
    {
        const string sql = "SELECT 1\nGO\nSELECT 2;\nGO\nSELECT 3";
        var script = SqlCoreFacade.ParseScript(sql, SqlAgentToolType.MsSqlServer);

        Assert.Equal(3, script.BatchCount);
        Assert.Equal(new[] { 0, 1, 2 }, script.Statements.Select(x => x.BatchIndex).ToArray());
        Assert.All(script.Statements, statement => Assert.Equal(SqlScriptParseStatus.Parsed, statement.Status));
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void Script_PreservesAbsoluteSourceSpansAcrossDialects(SqlAgentToolType dialect)
    {
        const string sql = "\n-- comment\n  SELECT 1;\n  UPDATE users SET name = 'x' WHERE id = 2;";
        var script = SqlCoreFacade.ParseScript(sql, dialect);

        Assert.Equal(2, script.Statements.Count);
        foreach (var statement in script.Statements)
        {
            Assert.Equal(statement.Text, sql.Substring(statement.Span.Start, statement.Span.Length));
            Assert.Equal(SqlScriptParseStatus.Parsed, statement.Status);
        }
        Assert.True(script.Statements[0].Span.Start < script.Statements[1].Span.Start);
    }

    [Fact]
    public void Script_InvalidQueryIsNotMistakenForAParsedStatement()
    {
        const string sql = "SELECT FROM users; SELECT 1;";
        var script = SqlCoreFacade.ParseScript(sql, SqlAgentToolType.Postgres);

        Assert.Equal(SqlScriptParseStatus.Invalid, script.Statements[0].Status);
        Assert.Equal(SqlScriptParseStatus.Parsed, script.Statements[1].Status);
        Assert.Single(script.Diagnostics);
        Assert.NotNull(script.Statements[0].Diagnostic);
        Assert.Equal(script.Statements[0].Diagnostic, script.Diagnostics[0]);
        Assert.True(script.Diagnostics[0].Span?.Start >= script.Statements[0].Span.Start);
    }

    [Fact]
    public void Script_UnknownProceduralBodyIsOpaqueUntilBatchBoundary()
    {
        const string sql = "CREATE PROCEDURE p AS BEGIN SELECT 1; END; SELECT 2";
        var script = SqlCoreFacade.ParseScript(sql, SqlAgentToolType.MsSqlServer);

        Assert.Single(script.Statements);
        Assert.Equal(SqlScriptStatementKind.Procedural, script.Statements[0].Kind);
        Assert.Equal(SqlScriptParseStatus.Unsupported, script.Statements[0].Status);
    }

    [Fact]
    public void Script_CommentsOnlyProduceNoExecutableOrValidatedStatements()
    {
        var script = SqlCoreFacade.ParseScript("/* no SQL; */ -- just a comment", SqlAgentToolType.Postgres);
        Assert.Empty(script.Statements);
        Assert.Empty(script.Diagnostics);
        Assert.Equal(0, script.BatchCount);
    }
}
