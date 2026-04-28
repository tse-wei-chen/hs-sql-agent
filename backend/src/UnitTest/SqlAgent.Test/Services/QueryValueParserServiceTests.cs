using System.Text.Json;
using SqlAgent.Service.Services;
using Xunit;

namespace SqlAgent.Test.Services;

public class QueryValueParserServiceTests
{
    private readonly QueryValueParserService _service = new();

    #region UnwrapJsonElement Tests

    [Fact]
    public void UnwrapJsonElement_ShouldReturnString_WhenJsonIsString()
    {
        // Arrange
        var json = JsonDocument.Parse("\"hello world\"").RootElement;
        
        // Act
        var result = _service.UnwrapJsonElement(json);

        // Assert
        Assert.Equal("hello world", result);
    }

    [Fact]
    public void UnwrapJsonElement_ShouldReturnInt64_WhenJsonIsInteger()
    {
        // Arrange
        var json = JsonDocument.Parse("42").RootElement;
        
        // Act
        var result = _service.UnwrapJsonElement(json);

        // Assert
        Assert.IsType<long>(result);
        Assert.Equal(42L, result);
    }

    [Fact]
    public void UnwrapJsonElement_ShouldReturnDouble_WhenJsonIsFloat()
    {
        // Arrange
        var json = JsonDocument.Parse("3.14").RootElement;
        
        // Act
        var result = _service.UnwrapJsonElement(json);

        // Assert
        Assert.IsType<double>(result);
        Assert.Equal(3.14, result);
    }

    [Fact]
    public void UnwrapJsonElement_ShouldReturnBoolean_WhenJsonIsTrueOrFalse()
    {
        // Arrange
        var jsonTrue = JsonDocument.Parse("true").RootElement;
        var jsonFalse = JsonDocument.Parse("false").RootElement;
        
        // Act & Assert
        Assert.Equal(true, _service.UnwrapJsonElement(jsonTrue));
        Assert.Equal(false, _service.UnwrapJsonElement(jsonFalse));
    }

    [Fact]
    public void UnwrapJsonElement_ShouldReturnArray_WhenJsonIsArray()
    {
        // Arrange
        var json = JsonDocument.Parse("[1, \"two\", true]").RootElement;
        
        // Act
        var result = _service.UnwrapJsonElement(json);

        // Assert
        var arr = Assert.IsType<object[]>(result);
        Assert.Equal(3, arr.Length);
        Assert.Equal(1L, arr[0]);
        Assert.Equal("two", arr[1]);
        Assert.Equal(true, arr[2]);
    }

    [Fact]
    public void UnwrapJsonElement_ShouldReturnStringRepresentation_WhenJsonIsObject()
    {
        // Arrange
        var json = JsonDocument.Parse("{\"key\":\"value\"}").RootElement;
        
        // Act
        var result = _service.UnwrapJsonElement(json);

        // Assert
        Assert.IsType<string>(result);
        Assert.Contains("\"key\"", (string)result);
        Assert.Contains("\"value\"", (string)result);
    }

    #endregion

    #region TryToDateTime Tests

    [Fact]
    public void TryToDateTime_ShouldReturnFalse_WhenValueIsNull()
    {
        var result = _service.TryToDateTime(null, out var dt);
        Assert.False(result);
        Assert.Equal(default, dt);
    }

    [Fact]
    public void TryToDateTime_ShouldReturnTrueAndSameObject_WhenValueIsDateTime()
    {
        var expectedDt = new DateTime(2023, 10, 25, 10, 0, 0);
        var result = _service.TryToDateTime(expectedDt, out var dt);
        
        Assert.True(result);
        Assert.Equal(expectedDt, dt);
    }

    [Fact]
    public void TryToDateTime_ShouldReturnTrueAndParsedDate_WhenValueIsValidDateString()
    {
        var result = _service.TryToDateTime("2023-10-25T10:00:00Z", out var dt);
        
        Assert.True(result);
        Assert.Equal(new DateTime(2023, 10, 25, 10, 0, 0, DateTimeKind.Utc).ToLocalTime(), dt);
    }

    [Fact]
    public void TryToDateTime_ShouldReturnFalse_WhenValueIsInvalidDateString()
    {
        var result = _service.TryToDateTime("Not-a-date", out var dt);
        
        Assert.False(result);
        Assert.Equal(default, dt);
    }

    #endregion

    #region TryGetInValues Tests

    [Fact]
    public void TryGetInValues_ShouldReturnFalse_WhenValueIsNull()
    {
        var result = _service.TryGetInValues(null, out var values);
        
        Assert.False(result);
        Assert.Empty(values);
    }

    [Fact]
    public void TryGetInValues_ShouldReturnTrueAndUnwrapElements_WhenValueIsJsonArray()
    {
        var json = JsonDocument.Parse("[1, \"test\", false]").RootElement;
        
        var result = _service.TryGetInValues(json, out var values);
        
        Assert.True(result);
        var arr = values.ToArray();
        Assert.Equal(3, arr.Length);
        Assert.Equal(1L, arr[0]);
        Assert.Equal("test", arr[1]);
        Assert.Equal(false, arr[2]);
    }

    [Fact]
    public void TryGetInValues_ShouldReturnFalse_WhenValueIsEmptyJsonArray()
    {
        var json = JsonDocument.Parse("[]").RootElement;
        
        var result = _service.TryGetInValues(json, out var values);
        
        Assert.False(result);
    }

    [Fact]
    public void TryGetInValues_ShouldParseStringContents_WhenValueIsJsonStringContainingArray()
    {
        var json = JsonDocument.Parse("\"(1, 2, 3)\"").RootElement;
        
        var result = _service.TryGetInValues(json, out var values);
        
        Assert.True(result);
        var arr = values.ToArray();
        Assert.Equal(3, arr.Length);
        Assert.Equal("1", arr[0]); // Extracted as strings
    }

    [Fact]
    public void TryGetInValues_ShouldReturnTrue_WhenValueIsIEnumerableObject()
    {
        IEnumerable<object> input = new List<object> { 1, "test", null! }; // Contains null which should be filtered
        
        var result = _service.TryGetInValues(input, out var values);
        
        Assert.True(result);
        var arr = values.ToArray();
        Assert.Equal(2, arr.Length);
        Assert.Equal(1, arr[0]);
        Assert.Equal("test", arr[1]);
    }

    [Fact]
    public void TryGetInValues_ShouldReturnFalse_WhenValueIsIEnumerableObjectWithOnlyNulls()
    {
        IEnumerable<object> input = new List<object> { null!, null! };
        
        var result = _service.TryGetInValues(input, out var values);
        
        Assert.False(result);
    }

    [Theory]
    [InlineData("1, 2, 3", 3)]
    [InlineData("(1, 2, 3)", 3)]
    [InlineData("[1, 2, 3]", 3)]
    [InlineData("'a', 'b', \"c\"", 3)] // Quotes should be trimmed
    public void TryGetInValues_ShouldParseStringWithDifferentFormats(string input, int expectedCount)
    {
        var result = _service.TryGetInValues(input, out var values);
        
        Assert.True(result);
        Assert.Equal(expectedCount, values.Count());
    }

    [Fact]
    public void TryGetInValues_ShouldParseDatesInString_WhenApplicable()
    {
        var result = _service.TryGetInValues("2023-01-01, not-a-date", out var values);
        
        Assert.True(result);
        var arr = values.ToArray();
        Assert.Equal(2, arr.Length);
        Assert.IsType<DateTime>(arr[0]);
        Assert.Equal("not-a-date", arr[1]);
    }

    [Fact]
    public void TryGetInValues_ShouldReturnFalse_WhenStringOnlyContainsEmptyValues()
    {
        var result = _service.TryGetInValues(" ( , , ) ", out var values);
        
        Assert.False(result);
    }

    [Fact]
    public void TryGetInValues_ShouldReturnFalse_WhenValueIsUnsupportedType()
    {
        var result = _service.TryGetInValues(42, out var values);
        
        Assert.False(result);
    }

    #endregion

    #region TryGetRangeValues Tests

    [Fact]
    public void TryGetRangeValues_ShouldReturnFalse_WhenValueIsNull()
    {
        var result = _service.TryGetRangeValues(null, out var start, out var end);
        
        Assert.False(result);
        Assert.Null(start);
        Assert.Null(end);
    }

    [Fact]
    public void TryGetRangeValues_ShouldReturnTrue_WhenJsonIsObjectWithStartAndEnd()
    {
        var json = JsonDocument.Parse("{\"start\": 10, \"end\": 20}").RootElement;
        
        var result = _service.TryGetRangeValues(json, out var start, out var end);
        
        Assert.True(result);
        Assert.Equal(10L, start);
        Assert.Equal(20L, end);
    }

    [Fact]
    public void TryGetRangeValues_ShouldReturnFalse_WhenJsonIsObjectMissingStartOrEnd()
    {
        var json = JsonDocument.Parse("{\"start\": 10}").RootElement;
        
        var result = _service.TryGetRangeValues(json, out var start, out var end);
        
        Assert.False(result);
        Assert.Null(start);
        Assert.Null(end);
    }

    [Fact]
    public void TryGetRangeValues_ShouldReturnTrue_WhenJsonIsArrayWithAtLeastTwoElements()
    {
        var json = JsonDocument.Parse("[10, 20, 30]").RootElement;
        
        var result = _service.TryGetRangeValues(json, out var start, out var end);
        
        Assert.True(result);
        Assert.Equal(10L, start);
        Assert.Equal(20L, end);
    }

    [Fact]
    public void TryGetRangeValues_ShouldReturnFalse_WhenJsonIsArrayWithFewerThanTwoElements()
    {
        var json = JsonDocument.Parse("[10]").RootElement;
        
        var result = _service.TryGetRangeValues(json, out var start, out var end);
        
        Assert.False(result);
    }

    [Fact]
    public void TryGetRangeValues_ShouldReturnTrue_WhenValueIsIEnumerableWithAtLeastTwoElements()
    {
        IEnumerable<object> input = new List<object> { "A", "B", "C" };
        
        var result = _service.TryGetRangeValues(input, out var start, out var end);
        
        Assert.True(result);
        Assert.Equal("A", start);
        Assert.Equal("B", end);
    }

    [Fact]
    public void TryGetRangeValues_ShouldParseDates_WhenStartOrEndAreDateStrings()
    {
        var json = JsonDocument.Parse("[\"2023-01-01\", \"2023-12-31\"]").RootElement;
        
        var result = _service.TryGetRangeValues(json, out var start, out var end);
        
        Assert.True(result);
        Assert.IsType<DateTime>(start);
        Assert.IsType<DateTime>(end);
    }

    [Fact]
    public void TryGetRangeValues_ShouldReturnFalse_WhenValueIsUnsupportedType()
    {
        var result = _service.TryGetRangeValues("10, 20", out var start, out var end);
        
        Assert.False(result); // According to the source, string is not handled for Range Values directly
    }

    #endregion
}
