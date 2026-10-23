using System.Text.Json;
using HsSqlAgent.Server.Middleware;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using SqlAgent.Service.Core.Execution;
using Xunit;

namespace HsSqlAgent.Server.Test.Middleware;

public sealed class GlobalExceptionHandlerTests
{
    [Fact]
    public async Task CommitOutcomeUnknown_ReturnsStableNonRetryableHttpContract()
    {
        var environment = new Mock<IWebHostEnvironment>();
        environment.SetupGet(x => x.EnvironmentName).Returns("Production");
        using var services = new ServiceCollection()
            .AddSingleton(environment.Object)
            .BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            RequestServices = services
        };
        using var responseStream = new MemoryStream();
        context.Response.Body = responseStream;

        var providerError = new IOException("Socket closed after the provider sent COMMIT.");
        var unknown = new DmlCommitOutcomeUnknownException(providerError);
        var handler = new GlobalExceptionHandler(
            Mock.Of<ILogger<GlobalExceptionHandler>>());

        Assert.True(await handler.TryHandleAsync(
            context, unknown, TestContext.Current.CancellationToken));
        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);

        responseStream.Position = 0;
        using var document = await JsonDocument.ParseAsync(
            responseStream, cancellationToken: TestContext.Current.CancellationToken);
        var result = document.RootElement;
        Assert.Equal("dml.commit_outcome_unknown", result.GetProperty("code").GetString());
        Assert.Equal("outcome_unknown", result.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("committed").ValueKind);
        Assert.False(result.GetProperty("retryable").GetBoolean());
        Assert.Contains("may already have committed", result.GetProperty("error").GetString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.False(result.TryGetProperty("detail", out var detail) &&
                     detail.ValueKind == JsonValueKind.String);
    }

    [Fact]
    public async Task RollbackOutcomeUnknown_ReturnsStableNonRetryableHttpContract()
    {
        var environment = new Mock<IWebHostEnvironment>();
        environment.SetupGet(x => x.EnvironmentName).Returns("Production");
        using var services = new ServiceCollection()
            .AddSingleton(environment.Object)
            .BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        using var responseStream = new MemoryStream();
        context.Response.Body = responseStream;

        var unknown = new DmlRollbackOutcomeUnknownException(
            new IOException("Target DML failed."),
            new IOException("Rollback acknowledgement was lost."));
        var handler = new GlobalExceptionHandler(
            Mock.Of<ILogger<GlobalExceptionHandler>>());

        Assert.True(await handler.TryHandleAsync(
            context, unknown, TestContext.Current.CancellationToken));
        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);

        responseStream.Position = 0;
        using var document = await JsonDocument.ParseAsync(
            responseStream, cancellationToken: TestContext.Current.CancellationToken);
        var result = document.RootElement;
        Assert.Equal("dml.rollback_outcome_unknown", result.GetProperty("code").GetString());
        Assert.Equal("outcome_unknown", result.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("committed").ValueKind);
        Assert.False(result.GetProperty("retryable").GetBoolean());
        Assert.Contains("could not be confirmed rolled back", result.GetProperty("error").GetString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.False(result.TryGetProperty("detail", out var detail) &&
                     detail.ValueKind == JsonValueKind.String);
    }

    [Fact]
    public async Task OrdinaryInvalidOperation_KeepsLegacyErrorShape()
    {
        var environment = new Mock<IWebHostEnvironment>();
        environment.SetupGet(x => x.EnvironmentName).Returns("Production");
        using var services = new ServiceCollection()
            .AddSingleton(environment.Object)
            .BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        using var stream = new MemoryStream();
        context.Response.Body = stream;
        var handler = new GlobalExceptionHandler(Mock.Of<ILogger<GlobalExceptionHandler>>());

        Assert.True(await handler.TryHandleAsync(
            context, new InvalidOperationException("ordinary conflict"),
            TestContext.Current.CancellationToken));
        Assert.Equal(StatusCodes.Status409Conflict, context.Response.StatusCode);
        stream.Position = 0;
        using var document = await JsonDocument.ParseAsync(
            stream, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("ordinary conflict", document.RootElement.GetProperty("error").GetString());
        Assert.False(document.RootElement.TryGetProperty("status", out _));
        Assert.False(document.RootElement.TryGetProperty("code", out _));
        Assert.False(document.RootElement.TryGetProperty("retryable", out _));
    }
}
