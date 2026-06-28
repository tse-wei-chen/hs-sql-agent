using Auth.Service.Data;
using Auth.Service.Data.Entites;
using Auth.Service.Services;
using Common.Interfaces;
using Microsoft.EntityFrameworkCore;
using Moq;
using Moq.EntityFrameworkCore;
using Xunit;

namespace Auth.Test.Services;

public class TokenRevocationServiceTests
{
    private readonly Mock<ICacheService> _cacheMock;
    private readonly Mock<IAuthContext> _contextMock;
    private readonly TokenRevocationService _service;

    public TokenRevocationServiceTests()
    {
        _cacheMock = new Mock<ICacheService>();
        _contextMock = new Mock<IAuthContext>();
        _service = new TokenRevocationService(_cacheMock.Object, _contextMock.Object);
    }

    [Fact]
    public async Task IsRevokedAsync_ReturnsFalse_WhenNotCachedAndNotInDb()
    {
        var jti = "jti-not-revoked";
        _cacheMock.Setup(c => c.GetAsync<bool?>("revoked:" + jti, It.IsAny<CancellationToken>()))
            .ReturnsAsync((bool?)null);
        _contextMock.Setup(c => c.TokenBlacklistEntries)
            .ReturnsDbSet(new List<TokenBlacklistEntry>());

        var result = await _service.IsRevokedAsync(jti, TestContext.Current.CancellationToken);

        Assert.False(result);
    }

    [Fact]
    public async Task IsRevokedAsync_ReturnsTrue_WhenNotCachedButInDb()
    {
        var jti = "jti-in-db";
        var entries = new List<TokenBlacklistEntry>
        {
            new() { Id = 1, Jti = jti, ExpiresAt = DateTime.UtcNow.AddHours(1), RevokedAt = DateTime.UtcNow }
        };
        _cacheMock.Setup(c => c.GetAsync<bool?>("revoked:" + jti, It.IsAny<CancellationToken>()))
            .ReturnsAsync((bool?)null);
        _contextMock.Setup(c => c.TokenBlacklistEntries)
            .ReturnsDbSet(entries);

        var result = await _service.IsRevokedAsync(jti, TestContext.Current.CancellationToken);

        Assert.True(result);
    }

    [Fact]
    public async Task IsRevokedAsync_ReturnsTrue_ForExpiredEntryInDb()
    {
        var jti = "jti-expired";
        var entries = new List<TokenBlacklistEntry>
        {
            new() { Id = 2, Jti = jti, ExpiresAt = DateTime.UtcNow.AddDays(-1), RevokedAt = DateTime.UtcNow }
        };
        _cacheMock.Setup(c => c.GetAsync<bool?>("revoked:" + jti, It.IsAny<CancellationToken>()))
            .ReturnsAsync((bool?)null);
        _contextMock.Setup(c => c.TokenBlacklistEntries)
            .ReturnsDbSet(entries);

        var result = await _service.IsRevokedAsync(jti, TestContext.Current.CancellationToken);

        Assert.True(result);
    }

    [Fact]
    public async Task IsRevokedAsync_ReturnsCachedTrue_WithoutQueryingDb()
    {
        var jti = "jti-cached-true";
        _cacheMock.Setup(c => c.GetAsync<bool?>("revoked:" + jti, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await _service.IsRevokedAsync(jti, TestContext.Current.CancellationToken);

        Assert.True(result);
        _contextMock.Verify(c => c.TokenBlacklistEntries, Times.Never);
    }

    [Fact]
    public async Task IsRevokedAsync_ReturnsCachedFalse_WithoutQueryingDb()
    {
        var jti = "jti-cached-false";
        _cacheMock.Setup(c => c.GetAsync<bool?>("revoked:" + jti, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await _service.IsRevokedAsync(jti, TestContext.Current.CancellationToken);

        Assert.False(result);
        _contextMock.Verify(c => c.TokenBlacklistEntries, Times.Never);
    }

    [Fact]
    public async Task RevokeAsync_AddsEntryAndSavesAndSetsCache()
    {
        var jti = "revoke-test-jti";
        var expiresAt = DateTime.UtcNow.AddHours(2);
        var entries = new List<TokenBlacklistEntry>();

        TokenBlacklistEntry? captured = null;
        var dbSetMock = new Mock<DbSet<TokenBlacklistEntry>>();
        dbSetMock.Setup(d => d.Add(It.IsAny<TokenBlacklistEntry>()))
            .Callback<TokenBlacklistEntry>(e => captured = e)
            .Returns((TokenBlacklistEntry e) => null!);
        dbSetMock.As<IQueryable<TokenBlacklistEntry>>()
            .Setup(m => m.Provider).Returns(entries.AsQueryable().Provider);
        dbSetMock.As<IQueryable<TokenBlacklistEntry>>()
            .Setup(m => m.Expression).Returns(entries.AsQueryable().Expression);
        dbSetMock.As<IQueryable<TokenBlacklistEntry>>()
            .Setup(m => m.ElementType).Returns(entries.AsQueryable().ElementType);
        dbSetMock.As<IQueryable<TokenBlacklistEntry>>()
            .Setup(m => m.GetEnumerator()).Returns(entries.AsQueryable().GetEnumerator());

        _contextMock.Setup(c => c.TokenBlacklistEntries).Returns(dbSetMock.Object);
        _contextMock.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        await _service.RevokeAsync(jti, expiresAt, TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.Equal(jti, captured!.Jti);
        Assert.Equal(expiresAt, captured.ExpiresAt);
        _cacheMock.Verify(c => c.SetAsync("revoked:" + jti, true,
            It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RevokeAsync_WithExpiredToken_SetsShortCache()
    {
        var jti = "revoke-expired-jti";
        var expiresAt = DateTime.UtcNow.AddHours(-1);
        var entries = new List<TokenBlacklistEntry>();

        var dbSetMock = new Mock<DbSet<TokenBlacklistEntry>>();
        dbSetMock.Setup(d => d.Add(It.IsAny<TokenBlacklistEntry>()))
            .Returns((TokenBlacklistEntry e) => null!);
        dbSetMock.As<IQueryable<TokenBlacklistEntry>>()
            .Setup(m => m.Provider).Returns(entries.AsQueryable().Provider);
        dbSetMock.As<IQueryable<TokenBlacklistEntry>>()
            .Setup(m => m.Expression).Returns(entries.AsQueryable().Expression);
        dbSetMock.As<IQueryable<TokenBlacklistEntry>>()
            .Setup(m => m.ElementType).Returns(entries.AsQueryable().ElementType);
        dbSetMock.As<IQueryable<TokenBlacklistEntry>>()
            .Setup(m => m.GetEnumerator()).Returns(entries.AsQueryable().GetEnumerator());

        _contextMock.Setup(c => c.TokenBlacklistEntries).Returns(dbSetMock.Object);
        _contextMock.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        await _service.RevokeAsync(jti, expiresAt, TestContext.Current.CancellationToken);

        _cacheMock.Verify(c => c.SetAsync("revoked:" + jti, true,
            It.Is<TimeSpan>(t => t == TimeSpan.FromMinutes(1)), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task IsRevokedAsync_CachesNegativeResult()
    {
        var jti = "negative-cache-jti";
        _cacheMock.Setup(c => c.GetAsync<bool?>("revoked:" + jti, It.IsAny<CancellationToken>()))
            .ReturnsAsync((bool?)null);
        _contextMock.Setup(c => c.TokenBlacklistEntries)
            .ReturnsDbSet(new List<TokenBlacklistEntry>());

        await _service.IsRevokedAsync(jti, TestContext.Current.CancellationToken);

        _cacheMock.Verify(c => c.SetAsync("revoked:" + jti, false,
            It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
