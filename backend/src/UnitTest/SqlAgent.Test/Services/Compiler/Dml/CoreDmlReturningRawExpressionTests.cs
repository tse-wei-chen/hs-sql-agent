using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreDmlReturningRawExpressionTests
{
    [Fact]
    public void Parse_PostgresRawReturningExpression_ProducesCanonicalExpressionItem()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "DELETE FROM users WHERE id = 1 RETURNING id + id AS doubled_id",
            SqlAgentToolType.Postgres);

        var delete = Assert.IsType<DeleteStatement>(parsed.Statement);
        var item = Assert.IsType<DmlReturningExpressionItem>(Assert.Single(delete.Returning));
        Assert.IsType<BinaryExpr>(item.Expression);
        Assert.Equal("doubled_id", item.Alias?.Value);
    }

    [Fact]
    public void Compile_PostgresRawReturningExpression_LowersBindingFreeExpression()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "DELETE FROM users WHERE id = 1 RETURNING id + id AS doubled_id",
            SqlAgentToolType.Postgres);

        var command = CoreDmlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("policy-v1"));

        Assert.True(command.ReturnsRows);
        Assert.Contains("doubled_id", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Single(command.Parameters);
        Assert.Equal(1, command.Parameters[0].Value);
    }

    [Fact]
    public void Compile_PostgresRawReturningLiteralExpression_ParameterizesInsideNativeDmlFragment()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "DELETE FROM users WHERE id = 1 RETURNING id + 2",
            SqlAgentToolType.Postgres);

        var command = CoreDmlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("policy-v1"));

        Assert.True(command.ReturnsRows);
        Assert.DoesNotContain(" + 2", command.Sql, StringComparison.Ordinal);
        Assert.Equal(new object?[] { 1, 2 }, command.Parameters.Select(x => x.Value).ToArray());
    }

    [Fact]
    public void ParseAndCompile_Sqlite335RawReturningExpression_UsesCanonicalExpressionItem()
    {
        var profile = new SqlProviderCapabilityProfile(
            SqlAgentToolType.Sqlite,
            ServerVersion: new Version(3, 35));

        var parsed = CoreSqlTextParser.ParseDml(
            "DELETE FROM users WHERE id = 1 RETURNING id + id AS doubled_id",
            SqlAgentToolType.Sqlite,
            profile);

        var delete = Assert.IsType<DeleteStatement>(parsed.Statement);
        var item = Assert.IsType<DmlReturningExpressionItem>(Assert.Single(delete.Returning));
        Assert.IsType<BinaryExpr>(item.Expression);
        Assert.Equal("doubled_id", item.Alias?.Value);

        var command = CoreDmlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.Sqlite,
            new SqlPlanValidationContext("sqlite-rich-returning-raw-v1"),
            targetProfile: profile);

        Assert.True(command.ReturnsRows);
        Assert.Contains("RETURNING", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("doubled_id", command.Sql, StringComparison.OrdinalIgnoreCase);
    }
}
