using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// A malformed SQL Server statement with an extra closing parenthesis must
/// not consume the next semicolon-separated statement. This recovery applies
/// to Script inspection only; it never makes the malformed SQL compilable.
/// </summary>
public sealed class SqlCoreScriptSemicolonErrorIsolationTests
{
    [Theory]
    [InlineData("SELECT 1); SELECT 2")]
    [InlineData("SELECT (1)); SELECT 2")]
    [InlineData("SELECT 1))) /* comment ; */; SELECT 2")]
    public void UnexpectedClosingParenthesisDoesNotSwallowNextStatement(string sql)
    {
        var parsed = SqlCoreFacade.ParseScript(sql,
            new SqlScriptParseOptions(SqlAgentToolType.MsSqlServer,
                TestContext.Current.CancellationToken));

        Assert.Equal(1, parsed.BatchCount);
        Assert.Equal(2, parsed.Statements.Count);
        Assert.Equal(new[] { 0, 0 }, parsed.Statements.Select(s => s.BatchIndex));
        Assert.Equal(SqlScriptStatementKind.Query, parsed.Statements[0].Kind);
        Assert.Equal(SqlScriptParseStatus.Invalid, parsed.Statements[0].Status);
        Assert.Equal(SqlScriptStatementKind.Query, parsed.Statements[1].Kind);
        Assert.Equal(SqlScriptParseStatus.Parsed, parsed.Statements[1].Status);
        Assert.Equal("SELECT 2", parsed.Statements[1].Text);
        Assert.Equal("SQL_LEXICAL_UNBALANCED_PARENTHESIS",
            Assert.Single(parsed.Diagnostics).Code);

        foreach (var statement in parsed.Statements)
            Assert.Equal(statement.Text,
                sql.Substring(statement.Span.Start, statement.Span.Length));

        var analyzed = SqlCoreFacade.AnalyzeScript(sql,
            new SqlScriptAnalysisOptions(SqlAgentToolType.MsSqlServer,
                SqlCatalogState.KnownEmpty(),
                TestContext.Current.CancellationToken));
        Assert.Equal(2, analyzed.Statements.Count);
        Assert.Equal(SqlScriptParseStatus.Invalid, analyzed.Statements[0].Syntax.Status);
        Assert.Equal(SqlScriptParseStatus.Parsed, analyzed.Statements[1].Syntax.Status);
        Assert.Equal("SELECT 2", analyzed.Statements[1].Syntax.Text);
    }

    [Fact]
    public void ExtraClosingParenthesisCannotSwallowMultipleValidStatements()
    {
        const string sql = "SELECT 1); SELECT 2; SELECT 3;";
        var parsed = SqlCoreFacade.ParseScript(sql, SqlAgentToolType.MsSqlServer);

        Assert.Equal(1, parsed.BatchCount);
        Assert.Equal(3, parsed.Statements.Count);
        Assert.Equal(new[] { SqlScriptParseStatus.Invalid,
            SqlScriptParseStatus.Parsed, SqlScriptParseStatus.Parsed },
            parsed.Statements.Select(s => s.Status));
        Assert.Equal(new[] { "SELECT 1)", "SELECT 2", "SELECT 3" },
            parsed.Statements.Select(s => s.Text));
    }

    [Fact]
    public void UnclosedOpeningParenthesisStillCannotBeRecoveredBySemicolon()
    {
        const string sql = "SELECT (1; SELECT 2";
        var parsed = SqlCoreFacade.ParseScript(sql, SqlAgentToolType.MsSqlServer);

        var statement = Assert.Single(parsed.Statements);
        Assert.Equal(SqlScriptParseStatus.Invalid, statement.Status);
        Assert.Equal(sql, statement.Text);
    }

    [Fact]
    public void OpaqueProcedureStillOwnsItsInternalSemicolonsAfterMismatchedClose()
    {
        const string sql = "CREATE PROCEDURE p AS BEGIN SELECT 1); SELECT 2; END; SELECT 3";
        var parsed = SqlCoreFacade.ParseScript(sql, SqlAgentToolType.MsSqlServer);

        var statement = Assert.Single(parsed.Statements);
        Assert.Equal(SqlScriptStatementKind.Procedural, statement.Kind);
        Assert.Equal(SqlScriptParseStatus.Unsupported, statement.Status);
        Assert.Equal(sql, statement.Text);
    }
}
