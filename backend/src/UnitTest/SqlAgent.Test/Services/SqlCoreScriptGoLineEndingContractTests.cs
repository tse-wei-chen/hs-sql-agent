using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// SQL Server GO is a lexical *line* command, not a SQL keyword.
/// CR-only, LF and CRLF scripts must agree with the lexer's newline contract.
/// </summary>
public sealed class SqlCoreScriptGoLineEndingContractTests
{
    [Theory]
    [InlineData("\r")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void StandaloneGoSeparatesThreeSqlServerBatchesRegardlessOfNewline(string newline)
    {
        var sql = string.Join(newline, "SELECT 1", "GO", "SELECT 2;", "GO", "SELECT 3");
        var parsed = SqlCoreFacade.ParseScript(sql, SqlAgentToolType.MsSqlServer);

        Assert.Equal(3, parsed.BatchCount);
        Assert.Equal(3, parsed.Statements.Count);
        Assert.Equal(new[] { 0, 1, 2 },
            parsed.Statements.Select(x => x.BatchIndex).ToArray());
        Assert.All(parsed.Statements, item =>
            Assert.Equal(SqlScriptParseStatus.Parsed, item.Status));
        Assert.Empty(parsed.Diagnostics);

        for (var index = 0; index < parsed.Statements.Count; index++)
        {
            var statement = parsed.Statements[index];
            Assert.Equal("SELECT " + (index + 1), statement.Text);
            Assert.Equal(statement.Text,
                sql.Substring(statement.Span.Start, statement.Span.Length));
        }

        var analyzed = SqlCoreFacade.AnalyzeScript(
            sql, SqlAgentToolType.MsSqlServer, SqlCatalogState.KnownEmpty());
        Assert.Equal(3, analyzed.Statements.Count);
        Assert.Equal(new[] { 0, 1, 2 },
            analyzed.Statements.Select(x => x.Syntax.BatchIndex).ToArray());
        Assert.All(analyzed.Statements, item =>
            Assert.Equal(SqlScriptParseStatus.Parsed, item.Syntax.Status));
    }

    [Theory]
    [InlineData("\r")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void GoWithOnlyInlineCommentsAndWhitespaceIsASeparator(string newline)
    {
        var sql = string.Join(newline,
            "SELECT 1;",
            " \t GO /* strictly line-local */ -- comment",
            "SELECT 2",
            "GO -- another comment",
            "SELECT 3");

        var parsed = SqlCoreFacade.ParseScript(sql, SqlAgentToolType.MsSqlServer);
        Assert.Equal(3, parsed.BatchCount);
        Assert.Equal(new[] { 0, 1, 2 },
            parsed.Statements.Select(x => x.BatchIndex).ToArray());
        Assert.All(parsed.Statements, item =>
            Assert.Equal(SqlScriptParseStatus.Parsed, item.Status));
        Assert.Empty(parsed.Diagnostics);
    }

    [Theory]
    [InlineData("\r")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void GoInSqlLiteralsAndSameLineIdentifierListsIsNotABatchSeparator(string newline)
    {
        var sql = "SELECT 'GO' AS marker, GO, GO FROM test_table;" +
                  newline + "GO" + newline + "SELECT 2";
        var parsed = SqlCoreFacade.ParseScript(sql, SqlAgentToolType.MsSqlServer);

        Assert.Equal(2, parsed.Statements.Count);
        Assert.Equal(2, parsed.BatchCount);
        Assert.Equal(new[] { 0, 1 }, parsed.Statements.Select(x => x.BatchIndex));
        Assert.Contains("'GO'", parsed.Statements[0].Text, StringComparison.Ordinal);
        Assert.Contains("GO, GO", parsed.Statements[0].Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\r")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void NonStandaloneGoWithRepetitionCountIsNeverAcceptedAsSeparator(string newline)
    {
        var sql = "SELECT 1" + newline + "GO 2" + newline + "SELECT 2";
        var parsed = SqlCoreFacade.ParseScript(sql, SqlAgentToolType.MsSqlServer);

        Assert.Single(parsed.Statements);
        Assert.Equal(1, parsed.BatchCount);
        Assert.Equal(0, parsed.Statements[0].BatchIndex);
        Assert.Equal(SqlScriptParseStatus.Invalid, parsed.Statements[0].Status);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void GoIsNotASeparatorOutsideSqlServer(SqlAgentToolType provider)
    {
        var parsed = SqlCoreFacade.ParseScript(
            "SELECT 1\rGO\rSELECT 2", provider);
        Assert.Single(parsed.Statements);
        Assert.Equal(1, parsed.BatchCount);
    }

    [Fact]
    public void ThousandsOfGoIdentifiersOnOnePhysicalLineCannotSplitBatches()
    {
        var sql = "SELECT " + string.Join(", ", Enumerable.Repeat("GO", 4_000))
                  + "\rGO\rSELECT 2";
        var parsed = SqlCoreFacade.ParseScript(sql, SqlAgentToolType.MsSqlServer);
        Assert.Equal(2, parsed.Statements.Count);
        Assert.Equal(2, parsed.BatchCount);
        Assert.Equal(new[] { 0, 1 }, parsed.Statements.Select(x => x.BatchIndex));
    }
}
