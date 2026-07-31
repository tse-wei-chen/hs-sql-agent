using Admin.Service.Data;
using Auth.Service.Data;
using HsSqlAgent.Server.Extensions;
using HsSqlAgent.Server.Models;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HsSqlAgent.Server.Test.Extensions;

public class AdminDatabaseRegistrationTests
{
    [Fact]
    public void AddHsSqlAgent_ShouldRegisterSqliteAdminContexts()
    {
        var services = new ServiceCollection();

        services.AddHsSqlAgent(CreateOptions("Sqlite"));

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.Equal(
            "Microsoft.EntityFrameworkCore.Sqlite",
            scope.ServiceProvider.GetRequiredService<AdminContext>().Database.ProviderName);
        Assert.Equal(
            "Microsoft.EntityFrameworkCore.Sqlite",
            scope.ServiceProvider.GetRequiredService<AuthContext>().Database.ProviderName);
    }

    [Fact]
    public void AddHsSqlAgent_ShouldRejectUnsupportedAdminProvider()
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(
            () => services.AddHsSqlAgent(CreateOptions("Unknown")));

        Assert.Contains("Unsupported admin database provider", exception.Message);
    }

    private static HsSqlAgentServiceOptions CreateOptions(string provider) => new()
    {
        AdminDatabaseProvider = provider,
        AdminConnectionString = "Data Source=:memory:",
        HmacSecretKey = "test-hmac-key-that-is-at-least-32-bytes",
        JwtSecretKey = "test-jwt-key-that-is-at-least-32-bytes"
    };
}
