using HsSqlAgent.Server.Controllers;
using HsSqlAgent.Server.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Xunit;

namespace HsSqlAgent.Server.Test.Controllers;

public class McpClientConfigControllerTests
{
    [Fact]
    public void Get_ShouldReturnConfiguredPublicEndpoint()
    {
        var controller = new McpClientConfigController(Options.Create(new McpOptions
        {
            PublicEndpoint = "https://sql-agent.example.com/gateway/mcp"
        }));

        var result = controller.Get();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var config = Assert.IsType<McpClientConfigResponse>(ok.Value);
        Assert.Equal("https://sql-agent.example.com/gateway/mcp", config.McpEndpoint);
    }
}
