using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// SQL Server GO is a client-side batch separator. Comments on the GO line are
/// lexical trivia, not another SQL statement or authorization evidence.
/// </summary>
public sealed class SqlCoreSqlServerGoCommentTests
{
    [Theory]
    [InlineData("SELECT 1\nGO -- first batch\nSELECT 2")]
    [InlineData("SELECT 1\r\ngo\t-- case insensitive\r\nSELECT 2")]
    [InlineData("SELECT 1\nGO /* inline batch comment */\nSELECT 2")]
    [InlineData("SELECT 1\n/* leading comment */ GO -- trailing comment\nSELECT 2")]
    public void SqlServer_GoLineWithCommentsStillSeparatesBatches(string sql)
    {
        var parsed = SqlCoreFacade.ParseScript(sql,
            new SqlScriptParseOptions(SqlAgentToolType.MsSqlServer,
                TestContext.Current.CancellationToken));

        Assert.Equal(2, parsed.BatchCount);
        Assert.Equal(2, parsed.Statements.Count);
        Assert.Equal(new[] { 0, 1 }, parsed.Statements.Select(s => s.BatchIndex).ToArray());
        Assert.All(parsed.Statements, statement =>
        {
            Assert.Equal(SqlScriptStatementKind.Query, statement.Kind);
            Assert.Equal(SqlScriptParseStatus.Parsed, statement.Status);
            Assert.Equal(statement.Text, sql.Substring(statement.Span.Start, statement.Span.Length));
        });
        Assert.Equal("SELECT 1", parsed.Statements[0].Text);
        Assert.Equal("SELECT 2", parsed.Statements[1].Text);
        Assert.Empty(parsed.Diagnostics);
    }

    [Theory]
    [InlineData("SELECT 1\nGO SELECT 2\nSELECT 3")]
    [InlineData("SELECT 1\nGO /* comment */ SELECT 2\nSELECT 3")]
    [InlineData("SELECT 1 GO -- same SQL line\nSELECT 2")]
    [InlineData("SELECT 1\nGO 2 -- repetition is not supported\nSELECT 2")]
    public void SqlServer_GoFollowedBySqlOrRepetitionIsNotASilentSeparator(string sql)
    {
        var parsed = SqlCoreFacade.ParseScript(sql,
            new SqlScriptParseOptions(SqlAgentToolType.MsSqlServer,
                TestContext.Current.CancellationToken));

        Assert.Equal(1, parsed.BatchCount);
        Assert.DoesNotContain(parsed.Statements, s => s.BatchIndex != 0);
        Assert.DoesNotContain(parsed.Statements, s => s.Status == SqlScriptParseStatus.Parsed);
    }

    [Fact]
    public void OtherDialects_DoNotInterpretCommentedGoAsBatchDelimiter()
    {
        const string sql = "SELECT 1\nGO -- SQL Server only\nSELECT 2";
        foreach (var dialect in new[]
                 {
                     SqlAgentToolType.Postgres,
                     SqlAgentToolType.MySQL,
                     SqlAgentToolType.Sqlite,
                     SqlAgentToolType.Oracle,
                     SqlAgentToolType.Firebird
                 })
        {
            var parsed = SqlCoreFacade.ParseScript(sql,
                new SqlScriptParseOptions(dialect, TestContext.Current.CancellationToken));
            Assert.Equal(1, parsed.BatchCount);
            Assert.DoesNotContain(parsed.Statements, statement => statement.BatchIndex != 0);
        }
    }
}
