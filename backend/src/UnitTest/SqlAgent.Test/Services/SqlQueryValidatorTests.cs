using SqlAgent.Service.Services;
using Xunit;


namespace SqlAgent.Test.Services;

public class SqlQueryValidatorTests
{
    private readonly SqlQueryValidator _validator = new();

    [Theory]
    [InlineData("COUNT", true)]
    [InlineData("sum", true)]
    [InlineData("MIN", true)]
    [InlineData("max", true)]
    [InlineData("avg", true)]
    [InlineData("INVALID", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsSupportedAggregation_ShouldReturnExpectedResult(string? aggregation, bool expected)
    {
        var result = _validator.IsSupportedAggregation(aggregation);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("inner", true)]
    [InlineData("LEFT ", true)]
    [InlineData("right", true)]
    [InlineData("CROSS", true)]
    [InlineData("outer", false)]
    [InlineData(null, false)]
    public void IsAllowedJoinType_ShouldReturnExpectedResult(string? joinType, bool expected)
    {
        var result = _validator.IsAllowedJoinType(joinType);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("ValidColumn")]
    [InlineData("table.column")]
    [InlineData("*")]
    public void RequireSafeIdentifier_WithValidIdentifier_ShouldReturnIdentifier(string identifier)
    {
        var result = _validator.RequireSafeIdentifier(identifier, "test");
        Assert.Equal(identifier, result);
    }

    [Theory]
    [InlineData("invalid column")]
    [InlineData("col; drop table")]
    [InlineData("")]
    [InlineData(null)]
    public void RequireSafeIdentifier_WithInvalidIdentifier_ShouldThrowException(string? identifier)
    {
        Assert.Throws<InvalidOperationException>(() => _validator.RequireSafeIdentifier(identifier, "test"));
    }

    [Theory]
    [InlineData("COUNT", "COUNT")]
    [InlineData("sum", "SUM")]
    public void RequireSafeAggregation_WithValidAggregation_ShouldReturnFormatted(string aggregation, string expected)
    {
        var result = _validator.RequireSafeAggregation(aggregation);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void RequireSafeAggregation_WithInvalidAggregation_ShouldThrowException()
    {
        Assert.Throws<InvalidOperationException>(() => _validator.RequireSafeAggregation("INVALID"));
    }

    [Theory]
    [InlineData("=", "=")]
    [InlineData(" ilike ", "ilike")]
    [InlineData(null, "=")]
    public void GetSafeOperator_WithValidOperator_ShouldReturnFormatted(string? op, string expected)
    {
        var result = _validator.GetSafeOperator(op);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void GetSafeOperator_WithInvalidOperator_ShouldThrowException()
    {
        Assert.Throws<InvalidOperationException>(() => _validator.GetSafeOperator("UNION"));
    }
}
