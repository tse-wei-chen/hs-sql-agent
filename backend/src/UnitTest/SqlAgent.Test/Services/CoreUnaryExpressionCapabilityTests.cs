using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreUnaryExpressionCapabilityTests
{
    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void Parse_UnaryMinusColumn_IsStructured(SqlAgentToolType provider)
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT -amount FROM orders",
            provider);
        var select = Assert.IsType<SelectStatement>(parsed.Statement);
        var unary = Assert.IsType<UnaryExpr>(
            Assert.Single(select.Select).Expression);

        Assert.Equal("-", unary.Operator);
        Assert.IsType<ColumnExpr>(unary.Operand);
    }

    [Fact]
    public void Parse_UnaryPlusArithmeticExpression_IsStructured()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT +(amount * 2) FROM orders",
            SqlAgentToolType.Postgres);
        var select = Assert.IsType<SelectStatement>(parsed.Statement);
        var unary = Assert.IsType<UnaryExpr>(
            Assert.Single(select.Select).Expression);

        Assert.Equal("+", unary.Operator);
        Assert.IsType<BinaryExpr>(unary.Operand);
    }

    [Fact]
    public void Compile_UnaryMinusColumn_PreservesNegation()
    {
        var command = Compile(
            "SELECT -amount FROM orders",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres);

        Assert.Contains("-", command.Sql, StringComparison.Ordinal);
        Assert.Contains("amount", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_UnaryPlus_IsNormalizedAsIdentity()
    {
        var command = Compile(
            "SELECT +(amount * 2) FROM orders",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres);

        Assert.Contains("*", command.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("(+", command.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_UnaryMinusAcrossProviders_RemainsStructuredArithmetic()
    {
        var command = Compile(
            "SELECT -amount FROM orders",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Oracle);

        Assert.Contains("-", command.Sql, StringComparison.Ordinal);
        Assert.Contains("AMOUNT", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_SignedNumericLiteral_PreservesExistingLiteralShape()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT -12.5",
            SqlAgentToolType.Postgres);
        var select = Assert.IsType<SelectStatement>(parsed.Statement);

        Assert.IsType<LiteralExpr>(Assert.Single(select.Select).Expression);
    }

    [Fact]
    public void Matrix_TracksUnaryNumericCapability()
    {
        foreach (var provider in Enum.GetValues<SqlAgentToolType>())
        {
            var capability = Assert.Single(
                SqlCapabilityMatrix.ForProvider(provider).Capabilities,
                item => item.Id == "expression.unary_numeric");

            Assert.Equal(SqlCapabilityStatus.Translated, capability.Status);
        }
    }

    private static CompiledSqlCommand Compile(
        string sql,
        SqlAgentToolType source,
        SqlAgentToolType target)
    {
        var parsed = CoreSqlTextParser.ParseQuery(sql, source);
        return CoreSqlCompiler.CreateDefault().Compile(
            parsed,
            target,
            new SqlPlanValidationContext("unary-expression-v1"),
            new SqlExecutionPlanPolicy());
    }
}
