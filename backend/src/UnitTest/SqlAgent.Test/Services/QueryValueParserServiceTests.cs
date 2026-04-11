using System.Text.Json;
using SqlAgent.Service.Services;
using Xunit;

namespace SqlAgent.Test.Services;

public class QueryValueParserServiceTests
{
    private readonly QueryValueParserService _service = new();

    [Fact]
    public void TryGetBetweenValues_WithValidJsonArray_ReturnsTrue()
    {
        var json = JsonDocument.Parse("[1, 10]").RootElement;
        
        var result = _service.TryGetBetweenValues(json, out var start, out var end);
        
        Assert.True(result);
        Assert.Equal(1L, start);
        Assert.Equal(10L, end);
    }

    [Fact]
    public void TryGetBetweenValues_WithValidString_ReturnsTrue()
    {
        var result = _service.TryGetBetweenValues("[1, 10]", out var start, out var end);
        
        Assert.True(result);
        Assert.Equal(1L, start);
        Assert.Equal(10L, end);
    }

    [Fact]
    public void TryGetBetweenValues_WithInvalidValue_ReturnsFalse()
    {
        var result = _service.TryGetBetweenValues("invalid", out var start, out var end);
        
        Assert.False(result);
    }
    
    [Fact]
    public void TryToDateTime_WithValidString_ReturnsTrue()
    {
        var result = _service.TryToDateTime("2023-10-25T10:00:00Z", out var dt);
        
        Assert.True(result);
        Assert.Equal(new DateTime(2023, 10, 25, 10, 0, 0, DateTimeKind.Utc).ToLocalTime(), dt);
    }

    [Fact]
    public void TryGetInValues_WithValidJsonArray_ReturnsTrue()
    {
        var json = JsonDocument.Parse("[1, 2, 3]").RootElement;
        
        var result = _service.TryGetInValues(json, out var values);
        
        Assert.True(result);
        Assert.Equal(3, values.Count());
    }

    [Fact]
    public void TryGetInValues_WithValidString_ReturnsTrue()
    {
        var result = _service.TryGetInValues("(1, 2, 3)", out var values);
        
        Assert.True(result);
        Assert.Equal(3, values.Count());
    }
}
