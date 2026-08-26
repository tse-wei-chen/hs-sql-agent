using Xunit;

namespace SqlAgent.Test.Services;

public class ParserP0RegressionTests
{
    [Fact]
    public void ParseQuery_CommaFrom_BecomesCrossJoinWithoutLosingEitherTable()
    {
        var definition = SqlDefinitionParser.ParseQuery(
            "SELECT a.id, b.id FROM alpha a, beta b WHERE a.id = b.id");

        Assert.Equal("alpha", definition.TableName);
        Assert.Equal("a", definition.Alias);
        var join = Assert.Single(definition.Joins!);
        Assert.Equal(JoinType.Cross, join.Type);
        Assert.Equal("beta", join.Table);
        Assert.Equal("b", join.Alias);

        var facts = BindFacts(definition);
        Assert.Contains("alpha", facts.ReferencedTables);
        Assert.Contains("beta", facts.ReferencedTables);
    }

    [Fact]
    public void ParseQuery_NestedCommaFrom_NormalizesAtNestedScope()
    {
        var definition = SqlDefinitionParser.ParseQuery(
            "SELECT q.id FROM (SELECT a.id FROM alpha a, beta b WHERE a.id = b.id) q");

        Assert.NotNull(definition.FromQuery);
        var nested = definition.FromQuery!;
        Assert.Equal("alpha", nested.TableName);
        var join = Assert.Single(nested.Joins!);
        Assert.Equal(JoinType.Cross, join.Type);
        Assert.Equal("beta", join.Table);

        var facts = BindFacts(definition);
        Assert.Contains("alpha", facts.ReferencedTables);
        Assert.Contains("beta", facts.ReferencedTables);
    }

    [Theory]
    [InlineData("SELECT id FROM users WHERE id IN (other_id)")]
    [InlineData("SELECT id FROM users WHERE id NOT IN (other_id)")]
    [InlineData("SELECT id FROM users WHERE id IN (1, other_id)")]
    [InlineData("SELECT id FROM users WHERE id IN (-other_id)")]
    [InlineData("SELECT id FROM users WHERE id IN (+ABS(1))")]
    public void ParseQuery_InListRejectsAnythingParserCannotRepresentAsScalarLiteral(string sql)
    {
        var error = Assert.Throws<SqlParseException>(() => SqlDefinitionParser.ParseQuery(sql));

        Assert.Contains("IN lists", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("scalar literals", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static QueryFacts BindFacts(HsSqlAgent.SqlCore.Models.QueryDefinition definition)
    {
        var parsed = new ParsedStatement(
            QueryDefinitionCoreMapper.Map(definition),
            SqlAgentToolType.Postgres);
        return new SqlAstBinder().Bind(parsed).Facts;
    }
}
