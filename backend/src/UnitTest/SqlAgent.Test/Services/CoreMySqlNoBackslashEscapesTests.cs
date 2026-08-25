using SqlAgent.Service.Core.Ast;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Models;
using SqlAgent.Service.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreMySqlNoBackslashEscapesTests
{
    [Fact]
    public void Parse_BackslashStringWithoutSourceProfile_RemainsFailClosed()
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery(
                "SELECT 'C:\\temp\\file' AS path",
                SqlAgentToolType.MySQL));

        Assert.Contains("NO_BACKSLASH_ESCAPES", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("source profile", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_AnsiMode_DoesNotImplyNoBackslashEscapes()
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery(
                "SELECT 'C:\\temp\\file' AS path",
                SqlAgentToolType.MySQL,
                MySqlProfile("ANSI")));

        Assert.Contains("NO_BACKSLASH_ESCAPES", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_NoBackslashEscapes_PreservesLiteralBackslashesAndDoubledQuote()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT 'C:\\Users\\O''Brien' AS path",
            SqlAgentToolType.MySQL,
            MySqlProfile("NO_BACKSLASH_ESCAPES"));

        var select = Assert.IsType<SelectStatement>(parsed.Statement);
        var literal = Assert.IsType<LiteralExpr>(Assert.Single(select.Select).Expression);

        Assert.Equal("C:\\Users\\O'Brien", Assert.IsType<string>(literal.Value));
    }

    [Fact]
    public void Parse_NoBackslashEscapes_DoesNotTreatBackslashAsQuoteEscape()
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery(
                "SELECT 'a\\'xyz'",
                SqlAgentToolType.MySQL,
                MySqlProfile("NO_BACKSLASH_ESCAPES")));

        Assert.Contains("Unterminated", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_BacktickIdentifierWithBackslashWithoutMode_RemainsFailClosed()
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery(
                "SELECT `path\\name` FROM users",
                SqlAgentToolType.MySQL));

        Assert.Contains("NO_BACKSLASH_ESCAPES", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("quoted identifiers", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_BacktickIdentifierWithNoBackslashEscapes_PreservesBackslash()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT `path\\name` FROM users",
            SqlAgentToolType.MySQL,
            MySqlProfile("NO_BACKSLASH_ESCAPES"));

        var select = Assert.IsType<SelectStatement>(parsed.Statement);
        var column = Assert.IsType<ColumnExpr>(Assert.Single(select.Select).Expression);
        var part = Assert.Single(column.Name.Parts);

        Assert.Equal("path\\name", part.Value);
        Assert.True(part.WasQuoted);
    }

    [Fact]
    public void Parse_AnsiQuotedIdentifierWithBackslash_RequiresBothRelevantModes()
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery(
                "SELECT \"path\\name\" FROM users",
                SqlAgentToolType.MySQL,
                MySqlProfile("ANSI_QUOTES")));

        Assert.Contains("NO_BACKSLASH_ESCAPES", error.Message, StringComparison.OrdinalIgnoreCase);

        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT \"path\\name\" FROM users",
            SqlAgentToolType.MySQL,
            MySqlProfile("ANSI_QUOTES", "NO_BACKSLASH_ESCAPES"));
        var select = Assert.IsType<SelectStatement>(parsed.Statement);
        var column = Assert.IsType<ColumnExpr>(Assert.Single(select.Select).Expression);

        Assert.Equal("path\\name", Assert.Single(column.Name.Parts).Value);
    }

    [Fact]
    public void Parse_NoBackslashEscapesLike_WithoutExplicitEscape_RemainsFailClosed()
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery(
                "SELECT name FROM users WHERE name LIKE 'A%'",
                SqlAgentToolType.MySQL,
                MySqlProfile("NO_BACKSLASH_ESCAPES")));

        Assert.Contains("LIKE", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NO_BACKSLASH_ESCAPES", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ESCAPE", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseDml_NoBackslashEscapes_UsesSameLiteralContract()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "UPDATE users SET home_path = 'C:\\Users\\Ada' WHERE id = 7",
            SqlAgentToolType.MySQL,
            MySqlProfile("NO_BACKSLASH_ESCAPES"));

        var update = Assert.IsType<UpdateStatement>(parsed.Statement);
        var assignment = Assert.Single(update.Assignments);
        var literal = Assert.IsType<LiteralExpr>(assignment.Value);

        Assert.Equal("C:\\Users\\Ada", Assert.IsType<string>(literal.Value));
    }

    [Fact]
    public void Tokenizer_NoBackslashEscapesFlagCannotBeUsedForAnotherProvider()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            new SqlTokenizer(
                "SELECT 'x'",
                SqlAgentToolType.Postgres,
                mysqlNoBackslashEscapes: true));

        Assert.Contains("NO_BACKSLASH_ESCAPES", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("mysqlNoBackslashEscapes", error.ParamName);
    }

    private static SqlProviderCapabilityProfile MySqlProfile(params string[] modes) =>
        new(
            SqlAgentToolType.MySQL,
            ServerVersion: new Version(8, 4),
            SessionModes: new HashSet<string>(modes, StringComparer.OrdinalIgnoreCase));
}
