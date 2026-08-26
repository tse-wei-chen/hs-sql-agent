using Xunit;

namespace SqlAgent.Test.Services;

public class SpecializedFunctionTranslatorTests
{
    private static readonly TranslationContext Context = new(SqlAgentToolType.MySQL, SqlAgentToolType.Postgres);
    [Fact]
    public void JsonExtract_NormalizesValidatedPathInSegmentOrder()
    {
        var translator = new JsonFunctionTranslator();
        var result = Assert.IsType<JsonExtractExpression>(translator.Normalize(new FunctionSelectCondition
        {
            FunctionName = "JSON_EXTRACT",
            Arguments =
            [
                new FieldSelectCondition { FieldName = "payload" },
                new ConstantSelectCondition { Constant = "$.customer.orders[0].id" }
            ]
        }, Context));

        Assert.Collection(result.Path.Segments,
            segment => Assert.Equal(new JsonPropertySegment("customer"), segment),
            segment => Assert.Equal(new JsonPropertySegment("orders"), segment),
            segment => Assert.Equal(new JsonArrayIndexSegment(0), segment),
            segment => Assert.Equal(new JsonPropertySegment("id"), segment));
        Assert.Equal("$.customer.orders[0].id", result.Path.RenderDollarPath());
        Assert.Equal("{customer,orders,0,id}", result.Path.RenderPostgresPath());
    }

    [Theory]
    [InlineData("$..secret")]
    [InlineData("$['unsafe']")]
    [InlineData("customer.name")]
    public void JsonExtract_RejectsNonPortablePath(string path)
    {
        var translator = new JsonFunctionTranslator();
        Assert.Throws<InvalidOperationException>(() => translator.Normalize(new FunctionSelectCondition
        {
            FunctionName = "JSON_EXTRACT",
            Arguments =
            [
                new FieldSelectCondition { FieldName = "payload" },
                new ConstantSelectCondition { Constant = path }
            ]
        }, Context));
    }

    [Fact]
    public void SpecializedRegistry_DispatchesJsonRegexAndTemporalFunctions()
    {
        var registry = new SpecializedFunctionTranslatorRegistry(
        [new TemporalFunctionTranslator(), new JsonFunctionTranslator(), new RegexFunctionTranslator()]);

        Assert.True(registry.CanTranslate("DATEADD"));
        Assert.True(registry.CanTranslate("JSON_SET"));
        Assert.True(registry.CanTranslate("REGEXP_LIKE"));
        Assert.False(registry.CanTranslate("MY_UDF"));
    }
}
