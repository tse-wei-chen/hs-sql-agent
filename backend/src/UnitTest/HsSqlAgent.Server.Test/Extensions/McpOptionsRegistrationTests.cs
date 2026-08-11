using HsSqlAgent.Server.Extensions;
using HsSqlAgent.Server.Models;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HsSqlAgent.Server.Test.Extensions;

public class McpOptionsRegistrationTests
{
    [Theory]
    [InlineData("")]
    [InlineData("/mcp")]
    [InlineData("ftp://sql-agent.example.com/mcp")]
    public void AddHsSqlAgent_ShouldRejectInvalidPublicEndpoint(string endpoint)
    {
        var options = ValidOptions();
        options.Mcp.PublicEndpoint = endpoint;

        var exception = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddHsSqlAgent(options));

        Assert.Contains("Mcp:PublicEndpoint", exception.Message);
    }

    private static HsSqlAgentServiceOptions ValidOptions() => new()
    {
        AdminConnectionString = "Data Source=:memory:",
        HmacSecretKey = "test-hmac-key-that-is-at-least-32-bytes",
        JwtSecretKey = "test-jwt-key-that-is-at-least-32-bytes"
    };
}
