using Infrastructure.Caching;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace Infrastructure.Test.Caching;

public class MemoryCacheServiceTests
{
    private readonly MemoryCacheService _service;

    public MemoryCacheServiceTests()
    {
        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        _service = new MemoryCacheService(memoryCache);
    }

    [Fact]
    public async Task GetAsync_ReturnsDefault_WhenKeyNotFound()
    {
        var result = await _service.GetAsync<string>("nonexistent", TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetAsync_ReturnsDefault_ForNullableBool_WhenNotCached()
    {
        var result = await _service.GetAsync<bool?>("nonexistent", TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task SetAndGetAsync_ReturnsValue_ForReferenceType()
    {
        await _service.SetAsync("key1", "hello", null, TestContext.Current.CancellationToken);

        var result = await _service.GetAsync<string>("key1", TestContext.Current.CancellationToken);

        Assert.Equal("hello", result);
    }

    [Fact]
    public async Task SetAndGetAsync_ReturnsValue_ForValueType()
    {
        await _service.SetAsync("key2", 42, null, TestContext.Current.CancellationToken);

        var result = await _service.GetAsync<int>("key2", TestContext.Current.CancellationToken);

        Assert.Equal(42, result);
    }

    [Fact]
    public async Task SetAndGetAsync_ReturnsValue_ForNullableBool()
    {
        await _service.SetAsync("key3", true, null, TestContext.Current.CancellationToken);

        var result = await _service.GetAsync<bool?>("key3", TestContext.Current.CancellationToken);

        Assert.True(result);
    }

    [Fact]
    public async Task SetAndGetAsync_ReturnsFalse_ForNullableBool()
    {
        await _service.SetAsync("key4", false, null, TestContext.Current.CancellationToken);

        var result = await _service.GetAsync<bool?>("key4", TestContext.Current.CancellationToken);

        Assert.False(result);
    }

    [Fact]
    public async Task SetAsync_WithAbsoluteExpiry_StoresValue()
    {
        await _service.SetAsync("expiry_key", "will-exist", TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);

        var result = await _service.GetAsync<string>("expiry_key", TestContext.Current.CancellationToken);

        Assert.Equal("will-exist", result);
    }

    [Fact]
    public async Task RemoveAsync_RemovesExistingValue()
    {
        await _service.SetAsync("remove_me", "to-remove", null, TestContext.Current.CancellationToken);
        await _service.RemoveAsync("remove_me", TestContext.Current.CancellationToken);

        var result = await _service.GetAsync<string>("remove_me", TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task RemoveAsync_NoOp_WhenKeyNotFound()
    {
        await _service.RemoveAsync("does_not_exist", TestContext.Current.CancellationToken);

        var result = await _service.GetAsync<string>("does_not_exist", TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task SetAsync_OverwritesExistingValue()
    {
        await _service.SetAsync("overwrite", "first", null, TestContext.Current.CancellationToken);
        await _service.SetAsync("overwrite", "second", null, TestContext.Current.CancellationToken);

        var result = await _service.GetAsync<string>("overwrite", TestContext.Current.CancellationToken);

        Assert.Equal("second", result);
    }

    [Fact]
    public async Task GetAsync_DifferentKeys_DoNotInterfere()
    {
        await _service.SetAsync("a", 1, null, TestContext.Current.CancellationToken);
        await _service.SetAsync("b", 2, null, TestContext.Current.CancellationToken);

        var resultA = await _service.GetAsync<int>("a", TestContext.Current.CancellationToken);
        var resultB = await _service.GetAsync<int>("b", TestContext.Current.CancellationToken);
        var resultC = await _service.GetAsync<int>("c", TestContext.Current.CancellationToken);

        Assert.Equal(1, resultA);
        Assert.Equal(2, resultB);
        Assert.Equal(0, resultC);
    }
}
