using SqlAgent.Service.Enums;
using SqlAgent.Service.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

public class SqlServerTopParserTests
{
    [Fact]
    public void ParseQuery_MsSqlServerTop_MapsToCanonicalLimit()
    {
        var definition = SqlDefinitionParser.ParseQuery(
            "SELECT TOP 1 id FROM users",
            SqlAgentToolType.MsSqlServer);

        Assert.Equal("users", definition.TableName);
        Assert.Equal(1, definition.Limit);
        Assert.Equal("id", Assert.Single(definition.SelectColumns!).GetType() == typeof(SqlAgent.Service.Models.FieldSelectCondition)
            ? ((SqlAgent.Service.Models.FieldSelectCondition)definition.SelectColumns![0]).FieldName
            : null);
    }

    [Fact]
    public void ParseQuery_MsSqlServerParenthesizedTop_MapsToCanonicalLimit()
    {
        var definition = SqlDefinitionParser.ParseQuery(
            "SELECT DISTINCT TOP (5) id FROM users ORDER BY id",
            SqlAgentToolType.MsSqlServer);

        Assert.True(definition.Distinct);
        Assert.Equal(5, definition.Limit);
    }

    [Fact]
    public void ParseQuery_TopWithoutMsSqlServerProvider_IsRejected()
    {
        Assert.Throws<SqlParseException>(() => SqlDefinitionParser.ParseQuery(
            "SELECT TOP 1 id FROM users",
            SqlAgentToolType.Postgres));
    }

    [Theory]
    [InlineData("SELECT TOP 10 PERCENT id FROM users")]
    [InlineData("SELECT TOP 10 WITH TIES id FROM users ORDER BY id")]
    public void ParseQuery_MsSqlServerTopUnsupportedModifiers_FailClosed(string sql)
    {
        var error = Assert.Throws<SqlParseException>(() =>
            SqlDefinitionParser.ParseQuery(sql, SqlAgentToolType.MsSqlServer));

        Assert.Contains("not yet represented", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
