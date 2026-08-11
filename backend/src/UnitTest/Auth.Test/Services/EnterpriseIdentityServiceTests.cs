using Auth.Service.Data;
using Auth.Service.Data.Entites;
using Auth.Service.Interfaces;
using Auth.Service.Models;
using Auth.Service.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Auth.Test.Services;

public class EnterpriseIdentityServiceTests
{
    [Fact]
    public async Task ExternalLoginCode_CanOnlyBeExchangedOnce()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var context = new AuthContext(new DbContextOptionsBuilder<AuthContext>().UseSqlite(connection).Options);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        var auth = new Mock<IAuthService>();
        auth.Setup(x => x.BeginMemberSignInAsync(It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(new AuthResult { Email = "user@test.com" });
        var settings = Options.Create(new EnterpriseIdentitySettings { DefaultRoleNames = [] });
        var service = new EnterpriseIdentityService(context, auth.Object, settings);

        var code = await service.CreateExternalLoginCodeAsync("oidc", "subject-1", "user@test.com", "User", [], TestContext.Current.CancellationToken);
        var result = await service.ExchangeExternalLoginCodeAsync(code, TestContext.Current.CancellationToken);

        Assert.Equal("user@test.com", result.Email);
        Assert.Single(context.ExternalIdentities);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ExchangeExternalLoginCodeAsync(code, TestContext.Current.CancellationToken));
    }
}
