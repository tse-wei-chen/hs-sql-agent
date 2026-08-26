using Xunit;

namespace SqlAgent.Test.Services;

public class TemporalFunctionTranslatorTests
{
    private readonly TemporalFunctionTranslator _translator = new();
    private static readonly TranslationContext Context = new(SqlAgentToolType.MsSqlServer, SqlAgentToolType.Postgres);

    [Fact]
    public void Normalize_DateAdd_ProducesCanonicalExpressionWithoutMutatingFunction()
    {
        var unit = new FieldSelectCondition { FieldName = "dd" };
        var amount = new ConstantSelectCondition { Constant = 3 };
        var value = new FieldSelectCondition { FieldName = "created_at" };
        var source = new FunctionSelectCondition
        {
            FunctionName = "DATEADD",
            Arguments = [unit, amount, value]
        };

        var result = Assert.IsType<DateAddExpression>(_translator.Normalize(source, Context));

        Assert.Equal(SqlDatePart.Day, result.Unit);
        Assert.Same(amount, result.Amount);
        Assert.Same(value, result.Value);
        Assert.Same(unit, source.Arguments![0]);
        Assert.Equal("dd", unit.FieldName);
    }

    [Fact]
    public void Normalize_TwoArgumentDateDiff_PreservesEndMinusStartSemantics()
    {
        var end = new FieldSelectCondition { FieldName = "ended_at" };
        var start = new FieldSelectCondition { FieldName = "started_at" };

        var result = Assert.IsType<DateDiffExpression>(_translator.Normalize(new FunctionSelectCondition
        {
            FunctionName = "DATEDIFF",
            Arguments = [end, start]
        }, Context));

        Assert.Equal(SqlDatePart.Day, result.Unit);
        Assert.Same(start, result.Start);
        Assert.Same(end, result.End);
    }

    [Theory]
    [InlineData("DATEADD", 2)]
    [InlineData("DATEDIFF", 1)]
    [InlineData("DATEDIFF", 4)]
    public void Normalize_KnownTemporalFunctionWithInvalidArity_FailsClosed(string name, int count)
    {
        var function = new FunctionSelectCondition
        {
            FunctionName = name,
            Arguments = Enumerable.Range(0, count)
                .Select(_ => (SelectCondition)new ConstantSelectCondition { Constant = 1 })
                .ToList()
        };

        Assert.Throws<InvalidOperationException>(() => _translator.Normalize(function, Context));
    }
}
