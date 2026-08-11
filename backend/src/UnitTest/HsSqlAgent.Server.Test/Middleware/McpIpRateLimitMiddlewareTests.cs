using System.Net;
using Admin.Service.Interfaces;
using Admin.Service.Models;
using HsSqlAgent.Server.Middleware;
using HsSqlAgent.Server.Services;
using Microsoft.AspNetCore.Http;
using Moq;
using Xunit;

namespace HsSqlAgent.Server.Test.Middleware;

public class McpIpRateLimitMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_ShouldLimitRequestsBeforeCallingNext()
    {
        var runtimeState = new Mock<IRateLimitingRuntimeState>();
        runtimeState.Setup(x => x.GetCurrent()).Returns(new RateLimitingSettings
        {
            PermitLimit = 1,
            WindowSeconds = 60
        });
        var metrics = new OperationalMetricRecorder();
        var middleware = new McpIpRateLimitMiddleware(
            runtimeState.Object,
            new MemoryRequestRateLimiter(TimeProvider.System),
            metrics);
        var nextCallCount = 0;
        RequestDelegate next = _ =>
        {
            nextCallCount++;
            return Task.CompletedTask;
        };

        await middleware.InvokeAsync(CreateContext(), next);
        var rejectedContext = CreateContext();
        await middleware.InvokeAsync(rejectedContext, next);

        Assert.Equal(1, nextCallCount);
        Assert.Equal(StatusCodes.Status429TooManyRequests, rejectedContext.Response.StatusCode);
        Assert.True(rejectedContext.Response.Headers.ContainsKey("Retry-After"));
        Assert.Equal(1, Assert.Single(metrics.Drain()).RejectedCount);
    }

    private static DefaultHttpContext CreateContext()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.10");
        context.Response.Body = new MemoryStream();
        return context;
    }
}
