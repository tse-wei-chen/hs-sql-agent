using Xunit;

namespace SqlAgent.Test.Services;

public class SqlSyntaxGuardTests
{
    [Fact]
    public void Parse_CommaSeparatedFromSource_NormalizesToCrossJoin()
    {
        var definition = SqlDefinitionParser.ParseQuery("SELECT * FROM a, b");

        Assert.Equal("a", definition.TableName);
        var join = Assert.Single(definition.Joins!);
        Assert.Equal(JoinType.Cross, join.Type);
        Assert.Equal("b", join.Table);
        Assert.Empty(join.OnConditions);
    }

    [Fact]
    public void Parse_CteColumnAliasList_IsRejectedInsteadOfDiscarded()
    {
        var ex = Assert.Throws<SqlParseException>(
            () => SqlDefinitionParser.ParseQuery("WITH x(a) AS (SELECT id FROM users) SELECT a FROM x"));

        Assert.Contains("CTE column alias", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_InIdentifierExpression_IsRejectedInsteadOfBecomingStringLiteral()
    {
        var ex = Assert.Throws<SqlParseException>(
            () => SqlDefinitionParser.ParseQuery("SELECT * FROM users WHERE id IN (other_id)"));

        Assert.Contains("scalar literals only", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("SELECT * FROM users WHERE id IN (1, 2, 3)")]
    [InlineData("SELECT * FROM users WHERE id IN (-1, +2, -3.5)")]
    [InlineData("SELECT * FROM users WHERE id IN ('a', 'b')")]
    [InlineData("SELECT * FROM users WHERE id IN (NULL, TRUE, FALSE)")]
    public void Parse_InScalarLiteralList_RemainsSupported(string sql)
    {
        var definition = SqlDefinitionParser.ParseQuery(sql);

        Assert.NotNull(definition.WhereColumnsAndValues);
        Assert.NotEmpty(definition.WhereColumnsAndValues);
    }

    [Theory]
    [InlineData("SELECT * FROM users WHERE id IN (-other_id)")]
    [InlineData("SELECT * FROM users WHERE id IN (+ABS(1))")]
    [InlineData("SELECT * FROM users WHERE id IN ()")]
    [InlineData("SELECT * FROM users WHERE id IN (1,)")]
    public void Parse_InFormsLegacyParserCannotPreserve_AreRejected(string sql)
    {
        Assert.Throws<SqlParseException>(() => SqlDefinitionParser.ParseQuery(sql));
    }

    [Fact]
    public void Parse_ExplicitCrossJoin_RemainsSupported()
    {
        var definition = SqlDefinitionParser.ParseQuery("SELECT * FROM a CROSS JOIN b");

        Assert.NotNull(definition.Joins);
        Assert.Single(definition.Joins);
    }
}
