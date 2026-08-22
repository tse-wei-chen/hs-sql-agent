using SqlAgent.Service.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

public class SqlSyntaxGuardTests
{
    [Fact]
    public void Parse_CommaSeparatedFromSource_IsRejectedFailClosed()
    {
        var ex = Assert.Throws<SqlParseException>(
            () => SqlDefinitionParser.ParseQuery("SELECT * FROM a, b"));

        Assert.Contains("explicit CROSS JOIN", ex.Message, StringComparison.OrdinalIgnoreCase);
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
    [InlineData("SELECT * FROM users WHERE id IN ('a', 'b')")]
    [InlineData("SELECT * FROM users WHERE id IN (NULL, TRUE, FALSE)")]
    [InlineData("SELECT * FROM users WHERE id IN (-1, +2)")]
    public void Parse_InScalarLiteralList_RemainsSupported(string sql)
    {
        var definition = SqlDefinitionParser.ParseQuery(sql);

        Assert.NotNull(definition.WhereColumnsAndValues);
        Assert.NotEmpty(definition.WhereColumnsAndValues);
    }

    [Fact]
    public void Parse_ExplicitCrossJoin_RemainsSupported()
    {
        var definition = SqlDefinitionParser.ParseQuery("SELECT * FROM a CROSS JOIN b");

        Assert.NotNull(definition.Joins);
        Assert.Single(definition.Joins);
    }
}
