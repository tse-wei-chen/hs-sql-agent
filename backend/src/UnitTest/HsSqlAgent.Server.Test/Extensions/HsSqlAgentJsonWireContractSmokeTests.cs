using System.Text.Json;
using Admin.Service.Models;
using HsSqlAgent.Server.Formatting;
using Xunit;

namespace HsSqlAgent.Server.Test.Extensions;

public sealed class HsSqlAgentJsonWireContractSmokeTests
{
    [Fact]
    public void StableContract_UsesCamelCaseAndStringEnums()
    {
        var json = JsonSerializer.Serialize(
            new IssueMcpAccessKeyRequest
            {
                Name = "agent",
                DbManagementId = 7,
                RateLimitMode = McpKeyRateLimitMode.Custom
            },
            HsSqlAgentJsonContract.CreateSerializerOptions());

        using var document = JsonDocument.Parse(json);
        Assert.Equal("agent", document.RootElement.GetProperty("name").GetString());
        Assert.Equal(7, document.RootElement.GetProperty("dbManagementId").GetInt32());
        Assert.Equal("Custom", document.RootElement.GetProperty("rateLimitMode").GetString());
    }

    [Fact]
    public void StableContract_AcceptsCaseInsensitiveCamelCaseInput()
    {
        var request = JsonSerializer.Deserialize<IssueMcpAccessKeyRequest>(
            "{\"NAME\":\"agent\",\"DBMANAGEMENTID\":7,\"RATELIMITMODE\":\"custom\"}",
            HsSqlAgentJsonContract.CreateSerializerOptions());

        Assert.NotNull(request);
        Assert.Equal("agent", request.Name);
        Assert.Equal(7, request.DbManagementId);
        Assert.Equal(McpKeyRateLimitMode.Custom, request.RateLimitMode);
    }
}
