using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.SqlTranslation.DateFormats;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class DateFormatTranslatorTests
{
    private readonly DateFormatTranslator _translator = new();

    [Fact]
    public void Translate_ResolvesPercentMFromSourceDialect()
    {
        Assert.Equal(
            "MONTH",
            _translator.Translate(
                "%M",
                SqlAgentToolType.MySQL,
                SqlAgentToolType.Postgres));
        Assert.Equal(
            "MI",
            _translator.Translate(
                "%M",
                SqlAgentToolType.Sqlite,
                SqlAgentToolType.Postgres));
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL, "%Y-%m-%d %H:%i:%S", SqlAgentToolType.Postgres, "YYYY-MM-DD HH24:MI:SS")]
    [InlineData(SqlAgentToolType.Sqlite, "%Y-%m-%d %H:%M:%S", SqlAgentToolType.MsSqlServer, "yyyy-MM-dd HH:mm:ss")]
    [InlineData(SqlAgentToolType.MsSqlServer, "yyyy-MM-dd HH:mm:ss", SqlAgentToolType.MySQL, "%Y-%m-%d %H:%i:%S")]
    [InlineData(SqlAgentToolType.Oracle, "YYYY-MM-DD HH24:MI:SS", SqlAgentToolType.Sqlite, "%Y-%m-%d %H:%M:%S")]
    public void Translate_RoundTripsCanonicalTokens(
        SqlAgentToolType source,
        string input,
        SqlAgentToolType target,
        string expected)
    {
        Assert.Equal(expected, _translator.Translate(input, source, target));
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL, "%j")]
    [InlineData(SqlAgentToolType.Sqlite, "%Q")]
    [InlineData(SqlAgentToolType.MsSqlServer, "yyyy-QQ")]
    [InlineData(SqlAgentToolType.Postgres, "YYYY-J")]
    public void Parse_RejectsUnknownToken(
        SqlAgentToolType dialect,
        string format)
    {
        Assert.Throws<FormatException>(() => _translator.Parse(format, dialect));
    }
}
