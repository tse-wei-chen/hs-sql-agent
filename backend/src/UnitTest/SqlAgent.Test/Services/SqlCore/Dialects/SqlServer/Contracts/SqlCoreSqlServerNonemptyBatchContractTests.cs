using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// An empty GO-delimited SQL Server batch has no analyzable statements.
/// BatchCount and BatchIndex describe nonempty batches, not raw GO tokens.
/// </summary>
public sealed class SqlCoreSqlServerNonemptyBatchContractTests
{
    [Theory]
    [InlineData("\r")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void LeadingRepeatedAndTrailingGoOnlyCountNonemptyBatches(string newline)
    {
        var sql = string.Join(newline,
            " GO -- leading comment",
            "GO",
            "SELECT 1;",
            "GO",
            "GO /* empty batch */",
            "SELECT 2;",
            "GO");

        var parsed = SqlCoreFacade.ParseScript(sql, SqlAgentToolType.MsSqlServer);
        Assert.Equal(2, parsed.BatchCount);
        Assert.Equal(new[] { 0, 1 }, parsed.Statements.Select(s => s.BatchIndex));
        Assert.Equal(new[] { "SELECT 1", "SELECT 2" },
            parsed.Statements.Select(s => s.Text));
        Assert.All(parsed.Statements, s =>
            Assert.Equal(SqlScriptParseStatus.Parsed, s.Status));
        Assert.Empty(parsed.Diagnostics);

        var analyzed = SqlCoreFacade.AnalyzeScript(sql,
            SqlAgentToolType.MsSqlServer, SqlCatalogState.KnownEmpty());
        Assert.Equal(2, analyzed.Statements.Count);
        Assert.Equal(new[] { 0, 1 },
            analyzed.Statements.Select(s => s.Syntax.BatchIndex));
    }

    [Theory]
    [InlineData("\r")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void StandaloneTrailingGoDoesNotCreateAnExtraBatch(string newline)
    {
        var sql = "SELECT 1" + newline + "GO";
        var parsed = SqlCoreFacade.ParseScript(sql, SqlAgentToolType.MsSqlServer);

        Assert.Equal(1, parsed.BatchCount);
        Assert.Equal(0, Assert.Single(parsed.Statements).BatchIndex);
        Assert.Empty(parsed.Diagnostics);
    }

    [Theory]
    [InlineData("\r")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void GoOnlyAndEmptyStatementScriptsHaveZeroBatches(string newline)
    {
        var sql = string.Join(newline,
            "-- comment", ";", "GO", "GO /* trivia */", ";");
        var parsed = SqlCoreFacade.ParseScript(sql, SqlAgentToolType.MsSqlServer);

        Assert.Equal(0, parsed.BatchCount);
        Assert.Empty(parsed.Statements);
        Assert.Empty(parsed.Diagnostics);

        var analyzed = SqlCoreFacade.AnalyzeScript(sql,
            SqlAgentToolType.MsSqlServer, SqlCatalogState.KnownEmpty());
        Assert.Empty(analyzed.Statements);
        Assert.Empty(analyzed.Diagnostics);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void NonSqlServerDialectsNeverTreatGoAsBatchDelimiter(SqlAgentToolType provider)
    {
        const string sql = "SELECT 1\nGO\nSELECT 2";
        var parsed = SqlCoreFacade.ParseScript(sql, provider);
        Assert.Equal(1, parsed.BatchCount);
        Assert.Equal(0, Assert.Single(parsed.Statements).BatchIndex);
    }
}
