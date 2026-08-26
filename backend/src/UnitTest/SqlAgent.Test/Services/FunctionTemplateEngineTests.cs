using Xunit;

namespace SqlAgent.Test.Services;

public class FunctionTemplateEngineTests
{
    [Fact]
    public void Translate_IntervalArithmetic_ProducesTypedAst()
    {
        var result = new FunctionTemplateEngine("$3 + ($2 * INTERVAL '1 day')").Translate(
        [
            new FieldSelectCondition { FieldName = "DAY" },
            new ConstantSelectCondition { Constant = 2 },
            new FieldSelectCondition { FieldName = "created_at" }
        ]);

        var addition = Assert.IsType<OperationSelectCondition>(result);
        Assert.Equal(ArithmeticOperator.Add, addition.Operator);
        Assert.Equal("created_at", Assert.IsType<FieldSelectCondition>(addition.Left).FieldName);
        var multiplication = Assert.IsType<OperationSelectCondition>(addition.Right);
        Assert.Equal(ArithmeticOperator.Multiply, multiplication.Operator);
        Assert.Equal("1 day", Assert.IsType<IntervalSelectCondition>(multiplication.Right).Literal);
    }

    [Fact]
    public void Translate_CastAndNestedFunction_ProducesTypedAst()
    {
        var result = new FunctionTemplateEngine("COALESCE(CAST($1 AS DECIMAL(12,2)), 0)").Translate(
            [new FieldSelectCondition { FieldName = "amount" }]);

        var coalesce = Assert.IsType<FunctionSelectCondition>(result);
        var cast = Assert.IsType<CastSelectCondition>(coalesce.Arguments![0]);
        Assert.Equal("DECIMAL(12,2)", cast.TypeName);
        Assert.Equal("amount", Assert.IsType<FieldSelectCondition>(cast.Expression).FieldName);
    }

    [Fact]
    public void Translate_Extract_ProducesDedicatedTypedAst()
    {
        var result = new FunctionTemplateEngine("EXTRACT(@Year FROM $1)").Translate(
            [new FieldSelectCondition { FieldName = "created_at" }]);

        Assert.Equal("TemplateExtractSelectCondition", result!.GetType().Name);
    }

    [Fact]
    public void Translate_CaseExpression_ProducesDedicatedTypedAst()
    {
        var result = new FunctionTemplateEngine(
            "CASE WHEN $1 >= 90 THEN 'A' WHEN $1 >= 80 THEN 'B' ELSE 'C' END").Translate(
            [new FieldSelectCondition { FieldName = "score" }]);

        Assert.Equal("TemplateCaseSelectCondition", result!.GetType().Name);
    }

    [Theory]
    [InlineData("DATE '2026-08-22'", typeof(SqlDateValue))]
    [InlineData("TIME '13:45:09'", typeof(SqlTimeValue))]
    [InlineData("TIMESTAMP '2026-08-22 13:45:09'", typeof(SqlLocalDateTimeValue))]
    public void Translate_TemporalLiteral_ProducesCanonicalValue(string template, Type expectedType)
    {
        var result = Assert.IsType<ConstantSelectCondition>(
            new FunctionTemplateEngine(template).Translate([]));

        Assert.IsType(expectedType, result.Constant);
    }

    [Fact]
    public void Translate_StringLiteral_UnescapesDoubledQuote()
    {
        var result = Assert.IsType<ConstantSelectCondition>(
            new FunctionTemplateEngine("'can''t'").Translate([]));

        Assert.Equal("can't", result.Constant);
    }

    [Fact]
    public void Translate_ComplexExpression_PreservesOperatorPrecedence()
    {
        var result = new FunctionTemplateEngine("$1 + $2 * 3 >= $3 AND $4 <> 0 OR $5 || 'x' = 'yx'").Translate(
        [
            new ConstantSelectCondition { Constant = 1 },
            new ConstantSelectCondition { Constant = 2 },
            new ConstantSelectCondition { Constant = 7 },
            new ConstantSelectCondition { Constant = 1 },
            new ConstantSelectCondition { Constant = "y" }
        ]);

        var or = Assert.IsType<OperationSelectCondition>(result);
        Assert.Equal(ArithmeticOperator.Or, or.Operator);
        var and = Assert.IsType<OperationSelectCondition>(or.Left);
        Assert.Equal(ArithmeticOperator.And, and.Operator);
        var comparison = Assert.IsType<OperationSelectCondition>(and.Left);
        Assert.Equal(ArithmeticOperator.GreaterThanOrEqual, comparison.Operator);
        var addition = Assert.IsType<OperationSelectCondition>(comparison.Left);
        Assert.Equal(ArithmeticOperator.Add, addition.Operator);
        Assert.Equal(ArithmeticOperator.Multiply, Assert.IsType<OperationSelectCondition>(addition.Right).Operator);
        var equality = Assert.IsType<OperationSelectCondition>(or.Right);
        Assert.Equal(ArithmeticOperator.Equal, equality.Operator);
        Assert.Equal(ArithmeticOperator.Concat, Assert.IsType<OperationSelectCondition>(equality.Left).Operator);
    }

    [Theory]
    [InlineData("$1 garbage")]
    [InlineData("COALESCE($1, 0")]
    [InlineData("'unterminated")]
    [InlineData("$2")]
    [InlineData("$1:unknown('x')")]
    public void Translate_InvalidTemplate_FailsFast(string template)
    {
        Assert.Throws<FormatException>(() =>
            new FunctionTemplateEngine(template).Translate(
                [new ConstantSelectCondition { Constant = 1 }]));
    }

    [Theory]
    [InlineData("$0", "position 0")]
    [InlineData("$1 | $1", "position 3")]
    [InlineData("$1 ! $1", "position 3")]
    [InlineData("$1 + #", "position 5")]
    public void Parse_InvalidToken_ReportsLexerPosition(string template, string expectedPosition)
    {
        var error = Assert.Throws<FormatException>(() => new FunctionTemplateEngine(template).Parse());

        Assert.Contains(expectedPosition, error.Message);
    }

    [Fact]
    public void Parse_CastTypeTokens_PreservePrecisionAndMultiWordType()
    {
        var result = Assert.IsType<CastSelectCondition>(
            new FunctionTemplateEngine("CAST($1 AS DOUBLE PRECISION)").Translate(
                [new FieldSelectCondition { FieldName = "amount" }]));

        Assert.Equal("DOUBLE PRECISION", result.TypeName);
    }

    [Fact]
    public void Parse_ProducesImmutableTemplateAstBeforeResolution()
    {
        var parsed = Assert.IsType<TemplateFunctionExpression>(
            new FunctionTemplateEngine("TARGET($2, $1:date_format)").Parse());

        Assert.Equal("TARGET", parsed.Name);
        Assert.Equal(2, parsed.Arguments.Count);
        Assert.Equal(1, Assert.IsType<TemplateArgumentReferenceExpression>(parsed.Arguments[0]).Index);
        var modified = Assert.IsType<TemplateArgumentReferenceExpression>(parsed.Arguments[1]);
        Assert.Equal(0, modified.Index);
        Assert.Equal("date_format", modified.Modifier);
        Assert.Empty(modified.ModifierArguments);
    }
}
