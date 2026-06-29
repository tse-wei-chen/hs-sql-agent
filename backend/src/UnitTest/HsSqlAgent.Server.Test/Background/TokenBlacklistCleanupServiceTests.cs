using Auth.Service.Data;
using Auth.Service.Data.Entites;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Moq.EntityFrameworkCore;
using HsSqlAgent.Server.Background;
using Xunit;

namespace HsSqlAgent.Server.Test.Background;

public class TokenBlacklistCleanupServiceTests
{
    [Fact]
    public async Task ExecuteAsync_RemovesExpiredEntriesAndSaves()
    {
        var entries = new List<TokenBlacklistEntry>
        {
            new() { Id = 1, Jti = "expired-1", ExpiresAt = DateTime.UtcNow.AddDays(-1), RevokedAt = DateTime.UtcNow },
            new() { Id = 2, Jti = "expired-2", ExpiresAt = DateTime.UtcNow.AddHours(-2), RevokedAt = DateTime.UtcNow }
        };
        var contextMock = CreateContextMock(entries);
        var service = CreateService(contextMock);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        await service.StopAsync(TestContext.Current.CancellationToken);

        contextMock.Verify(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotRemoveNonExpiredEntries()
    {
        var entries = new List<TokenBlacklistEntry>
        {
            new() { Id = 3, Jti = "active-1", ExpiresAt = DateTime.UtcNow.AddHours(1), RevokedAt = DateTime.UtcNow },
            new() { Id = 4, Jti = "active-2", ExpiresAt = DateTime.UtcNow.AddDays(1), RevokedAt = DateTime.UtcNow }
        };

        var contextMock = new Mock<IAuthContext>();
        contextMock.Setup(c => c.TokenBlacklistEntries).ReturnsDbSet(entries);
        contextMock.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var service = CreateService(contextMock);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        await service.StopAsync(TestContext.Current.CancellationToken);

        contextMock.Verify(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_SwallowsException_WhenScopeCreationFails()
    {
        var scopeFactoryMock = new Mock<IServiceScopeFactory>();
        scopeFactoryMock.Setup(s => s.CreateScope()).Throws<InvalidOperationException>();

        var service = new TokenBlacklistCleanupService(scopeFactoryMock.Object);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    private static TokenBlacklistCleanupService CreateService(Mock<IAuthContext> contextMock)
    {
        var scopeFactoryMock = new Mock<IServiceScopeFactory>();
        var scopeMock = new Mock<IServiceScope>();
        var serviceProviderMock = new Mock<IServiceProvider>();

        scopeFactoryMock.Setup(s => s.CreateScope()).Returns(scopeMock.Object);
        scopeMock.Setup(s => s.ServiceProvider).Returns(serviceProviderMock.Object);
        serviceProviderMock.Setup(p => p.GetService(typeof(IAuthContext))).Returns(contextMock.Object);

        return new TokenBlacklistCleanupService(scopeFactoryMock.Object);
    }

    private static Mock<IAuthContext> CreateContextMock(List<TokenBlacklistEntry> entries)
    {
        var contextMock = new Mock<IAuthContext>();
        contextMock.Setup(c => c.TokenBlacklistEntries).ReturnsDbSet(entries);
        contextMock.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        return contextMock;
    }
}
