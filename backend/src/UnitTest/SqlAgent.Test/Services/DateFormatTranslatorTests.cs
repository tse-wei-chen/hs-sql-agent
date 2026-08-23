using SqlAgent.Service.Enums;
using SqlAgent.Service.Models;
using SqlAgent.Service.SqlParsing;
using SqlAgent.Service.SqlTranslation.Context;
using SqlAgent.Service.SqlTranslation.DateFormats;
using Xunit;

namespace SqlAgent.Test.Services;

public class DateFormatTranslatorTests
{
    private readonly DateFormatTranslator _translator = new();

    [Fact]
    public void Translate_ShouldResolvePercentMFromSourceDialect()
    {
        Assert.Equal("MONTH", _translator.Translate("%M", SqlAgentToolType.MySQL, SqlAgentToolType.Postgres));
        Assert.Equal("MI", _translator.Translate("%M", SqlAgentToolType.Sqlite, SqlAgentToolType.Postgres));
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL, "%Y-%m-%d %H:%i:%S", SqlAgentToolType.Postgres, "YYYY-MM-DD HH24:MI:SS")]
    [InlineData(SqlAgentToolType.Sqlite, "%Y-%m-%d %H:%M:%S", SqlAgentToolType.MsSqlServer, "yyyy-MM-dd HH:mm:ss")]
    [InlineData(SqlAgentToolType.MsSqlServer, "yyyy-MM-dd HH:mm:ss", SqlAgentToolType.MySQL, "%Y-%m-%d %H:%i:%S")]
    [InlineData(SqlAgentToolType.Oracle, "YYYY-MM-DD HH24:MI:SS", SqlAgentToolType.Sqlite, "%Y-%m-%d %H:%M:%S")]
    public void Translate_ShouldRoundTripCanonicalTokens(
        SqlAgentToolType source,
        string input,
        SqlAgentToolType target,
        string expected)
    {
        Assert.Equal(expected, _translator.Translate(input, source, target));
    }

    [Fact]
    public void TemplateModifier_ShouldUseContextAndNotMutateSourceConstant()
    {
        var source = new ConstantSelectCondition { Constant = "%Y-%m-%d %H:%i" };
        var result = Assert.IsType<ConstantSelectCondition>(
            new FunctionTemplateEngine("$1:date_format").Translate(
                [source],
                new TranslationContext(SqlAgentToolType.MySQL, SqlAgentToolType.Postgres)));

        Assert.Equal("YYYY-MM-DD HH24:MI", result.Constant);
        Assert.Equal("%Y-%m-%d %H:%i", source.Constant);
        Assert.NotSame(source, result);
    }

    [Fact]
    public void TemplateModifier_ShouldRejectDialectArgument()
    {
        Assert.Throws<FormatException>(() =>
            new FunctionTemplateEngine("$1:date_format('pg')").Translate(
                [new ConstantSelectCondition { Constant = "%Y" }],
                new TranslationContext(SqlAgentToolType.MySQL, SqlAgentToolType.Postgres)));
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL, "%j")]
    [InlineData(SqlAgentToolType.Sqlite, "%Q")]
    [InlineData(SqlAgentToolType.MsSqlServer, "yyyy-QQ")]
    [InlineData(SqlAgentToolType.Postgres, "YYYY-J")]
    public void Parse_ShouldRejectUnknownToken(SqlAgentToolType dialect, string format)
    {
        Assert.Throws<FormatException>(() => _translator.Parse(format, dialect));
    }
}
