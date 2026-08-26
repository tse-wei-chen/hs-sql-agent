using Xunit;

namespace SqlAgent.Test.Services;

public class SqlParserLiteralListTests
{
    [Fact]
    public void Parse_InList_AcceptsPortableLiteralSetAndSignedNumbers()
    {
        var definition = SqlDefinitionParser.ParseQuery(
            "SELECT id FROM users WHERE id IN (-1, +2, 3.5, -4.25, 'x', NULL, TRUE, FALSE)",
            SqlAgentToolType.Postgres);

        var condition = Assert.IsType<BasicWhereCondition>(Assert.Single(definition.WhereColumnsAndValues!));
        Assert.Equal("IN", condition.Operator);
        Assert.Collection(
            condition.Values,
            value => Assert.Equal(-1, value),
            value => Assert.Equal(2, value),
            value => Assert.Equal(3.5m, value),
            value => Assert.Equal(-4.25m, value),
            value => Assert.Equal("x", value),
            value => Assert.Null(value),
            value => Assert.Equal(true, value),
            value => Assert.Equal(false, value));
    }

    [Fact]
    public void Parse_NotInList_AcceptsSignedNumbers()
    {
        var definition = SqlDefinitionParser.ParseQuery(
            "SELECT id FROM users WHERE id NOT IN (-10, +20)",
            SqlAgentToolType.Postgres);

        var condition = Assert.IsType<BasicWhereCondition>(Assert.Single(definition.WhereColumnsAndValues!));
        Assert.True(condition.IsNot);
        Assert.Equal([-10, 20], condition.Values);
    }

    [Theory]
    [InlineData("SELECT id FROM users WHERE id IN (other_id)")]
    [InlineData("SELECT id FROM users WHERE id NOT IN (other_id)")]
    [InlineData("SELECT id FROM users WHERE id IN (ABS(1))")]
    [InlineData("SELECT id FROM users WHERE id IN (1 + 2)")]
    [InlineData("SELECT id FROM users WHERE id IN (1 2)")]
    [InlineData("SELECT id FROM users WHERE id IN ()")]
    [InlineData("SELECT id FROM users WHERE id IN (1,)")]
    [InlineData("SELECT id FROM users WHERE id IN (-other_id)")]
    public void Parse_InList_NonLiteralOrMalformedInput_FailsClosed(string sql)
    {
        Assert.Throws<SqlParseException>(() =>
            SqlDefinitionParser.ParseQuery(sql, SqlAgentToolType.Postgres));
    }

    [Theory]
    [InlineData("SELECT id FROM users WHERE id IN (other_id)")]
    [InlineData("SELECT id FROM users WHERE id IN (1 + 2)")]
    [InlineData("SELECT id FROM users WHERE id IN (1,)")]
    [InlineData("SELECT id FROM users WHERE id IN ()")]
    public void LowLevelParser_InList_FailsClosedWithoutOuterSyntaxGuard(string sql)
    {
        var tokens = new SqlTokenizer(sql).Tokenize();

        Assert.Throws<SqlParseException>(() => new SqlParser(tokens).Parse());
    }
}
