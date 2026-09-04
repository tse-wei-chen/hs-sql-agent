using HsSqlAgent.Server.Extensions;
using HsSqlAgent.Server.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HsSqlAgent.Server.Test.Extensions;

public class EndpointContractTests
{
    [Fact]
    public void CurrentHttpSurfaces_AreExplicitAndStable()
    {
        Assert.Equal("/", HsSqlAgentHttpPaths.AdminUi);
        Assert.Equal("/api", HsSqlAgentHttpPaths.AdminApi);
        Assert.Equal("/mcp", HsSqlAgentHttpPaths.Mcp);
    }

    [Fact]
    public void LegacyMapMcpEndpoint_RejectsAPathThatWouldNotMatchTheMappedEndpoint()
    {
        var builder = CreateBuilder();

#pragma warning disable CS0618
        var exception = Assert.Throws<InvalidOperationException>(() => builder.MapMcpEndpoint("/custom-mcp"));
#pragma warning restore CS0618

        Assert.Contains("fixed at '/mcp'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyMapAdminEndpoint_RejectsAPathThatWouldNotMatchControllerRoutes()
    {
        var builder = CreateBuilder();

#pragma warning disable CS0618
        var exception = Assert.Throws<InvalidOperationException>(() => builder.MapAdminEndpoint("/custom-api"));
#pragma warning restore CS0618

        Assert.Contains("fixed at '/api'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ServeAdminUi_RejectsSubPathUntilSpaIsRelocatable()
    {
        var builder = CreateBuilder();

        var exception = Assert.Throws<InvalidOperationException>(() => builder.ServeAdminUi("/sql-agent"));

        Assert.Contains("fixed at '/'", exception.Message, StringComparison.Ordinal);
    }

    private static HsSqlAgentBuilder CreateBuilder()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        return new HsSqlAgentBuilder(new ApplicationBuilder(services));
    }
}
