using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using HsSqlAgent.SqlCore.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// SQL Server GO terminates a physical batch, even when the preceding
/// batch is syntactically incomplete. Broken SQL must not consume the
/// next batch or accidentally turn subsequent statements into evidence.
/// </summary>
public sealed class SqlCoreScriptGoErrorIsolationTests
{
    [Theory]
    [InlineData("\r")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void StandaloneGoRecoversFromUnclosedParenthesesInEarlierBatch(string newline)
    {
        var sql = string.Join(newline, "SELECT (1", "GO", "SELECT 2");
        var parsed = SqlCoreFacade.ParseScript(sql, new SqlScriptParseOptions(
            SqlAgentToolType.MsSqlServer, TestContext.Current.CancellationToken));

        Assert.Equal(2, parsed.BatchCount);
        Assert.Equal(2, parsed.Statements.Count);
        Assert.Equal(new[] { 0, 1 }, parsed.Statements.Select(s => s.BatchIndex));
        Assert.Equal(SqlScriptStatementKind.Query, parsed.Statements[0].Kind);
        Assert.Equal(SqlScriptParseStatus.Invalid, parsed.Statements[0].Status);
        Assert.Equal(SqlScriptParseStatus.Parsed, parsed.Statements[1].Status);
        Assert.Equal("SELECT (1", parsed.Statements[0].Text);
        Assert.Equal("SELECT 2", parsed.Statements[1].Text);
        Assert.Single(parsed.Diagnostics);
        Assert.NotNull(parsed.Statements[0].Diagnostic);
        Assert.Null(parsed.Statements[1].Diagnostic);

        foreach (var statement in parsed.Statements)
            Assert.Equal(statement.Text, sql.Substring(statement.Span.Start, statement.Span.Length));

        var analyzed = SqlCoreFacade.AnalyzeScript(sql, new SqlScriptAnalysisOptions(
            SqlAgentToolType.MsSqlServer, SqlCatalogState.KnownEmpty(),
            TestContext.Current.CancellationToken));
        Assert.Equal(2, analyzed.Statements.Count);
        Assert.Equal(SqlScriptParseStatus.Invalid, analyzed.Statements[0].Syntax.Status);
        Assert.Equal(SqlScriptParseStatus.Parsed, analyzed.Statements[1].Syntax.Status);
        Assert.Equal(1, analyzed.Statements[1].Syntax.BatchIndex);
    }

    [Theory]
    [InlineData("\r")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void StandaloneGoTerminatesOpaqueProcedureEvenWithOpenParen(string newline)
    {
        var sql = string.Join(newline,
            "CREATE PROCEDURE p AS BEGIN SELECT (1;",
            "GO",
            "SELECT 2");
        var parsed = SqlCoreFacade.ParseScript(sql, new SqlScriptParseOptions(
            SqlAgentToolType.MsSqlServer, TestContext.Current.CancellationToken));

        Assert.Equal(2, parsed.BatchCount);
        Assert.Equal(2, parsed.Statements.Count);
        Assert.Equal(new[] { 0, 1 }, parsed.Statements.Select(s => s.BatchIndex));
        Assert.Equal(SqlScriptStatementKind.Procedural, parsed.Statements[0].Kind);
        Assert.Equal(SqlScriptParseStatus.Unsupported, parsed.Statements[0].Status);
        Assert.Equal(SqlScriptStatementKind.Query, parsed.Statements[1].Kind);
        Assert.Equal(SqlScriptParseStatus.Parsed, parsed.Statements[1].Status);
        Assert.Equal("SELECT 2", parsed.Statements[1].Text);
        Assert.Single(parsed.Diagnostics);

        foreach (var statement in parsed.Statements)
            Assert.Equal(statement.Text, sql.Substring(statement.Span.Start, statement.Span.Length));
    }

    [Fact]
    public void SqlServerUnclosedGroupingWithoutGoRemainsAnInvalidStatement()
    {
        const string sql = "SELECT (1";
        var parsed = SqlCoreFacade.ParseScript(sql, new SqlScriptParseOptions(
            SqlAgentToolType.MsSqlServer, TestContext.Current.CancellationToken));

        Assert.Equal(1, parsed.BatchCount);
        var statement = Assert.Single(parsed.Statements);
        Assert.Equal(SqlScriptParseStatus.Invalid, statement.Status);
        Assert.Equal(SqlScriptStatementKind.Query, statement.Kind);
        Assert.Equal(sql, statement.Text);
        Assert.Equal("SQL_LEXICAL_UNBALANCED_PARENTHESIS",
            Assert.Single(parsed.Diagnostics).Code);
    }

    [Fact]
    public void OtherDialectScriptLexerKeepsStrictGlobalGroupingValidation()
    {
        Assert.Throws<SqlParseException>(() => SqlCoreFacade.ParseScript(
            "SELECT (1", new SqlScriptParseOptions(
                SqlAgentToolType.Postgres, TestContext.Current.CancellationToken)));
    }
}
