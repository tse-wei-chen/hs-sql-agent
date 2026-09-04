using HsSqlAgent.Server.Authorization;
using HsSqlAgent.Server.Middleware;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Moq;
using Xunit;

namespace HsSqlAgent.Server.Test.Middleware;

public class HsSqlAgentOidcCallbackMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_DispatchesOnlyHsSqlAgentOidcHandler_AndStopsWhenHandled()
    {
        var context = new DefaultHttpContext();
        var requestHandler = new Mock<IAuthenticationRequestHandler>();
        requestHandler.Setup(handler => handler.HandleRequestAsync()).ReturnsAsync(true);

        var handlerProvider = new Mock<IAuthenticationHandlerProvider>();
        handlerProvider
            .Setup(provider => provider.GetHandlerAsync(context, HsSqlAgentAuthenticationSchemes.Oidc))
            .ReturnsAsync(requestHandler.Object);

        var nextCalled = false;
        var middleware = new HsSqlAgentOidcCallbackMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context, handlerProvider.Object);

        Assert.False(nextCalled);
        requestHandler.Verify(handler => handler.HandleRequestAsync(), Times.Once);
        handlerProvider.Verify(provider => provider.GetHandlerAsync(
            context,
            HsSqlAgentAuthenticationSchemes.Oidc), Times.Once);
        handlerProvider.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task InvokeAsync_CallsNext_WhenHsSqlAgentOidcHandlerIsUnavailable()
    {
        var context = new DefaultHttpContext();
        var handlerProvider = new Mock<IAuthenticationHandlerProvider>();
        handlerProvider
            .Setup(provider => provider.GetHandlerAsync(context, HsSqlAgentAuthenticationSchemes.Oidc))
            .ReturnsAsync((IAuthenticationHandler?)null);

        var nextCalled = false;
        var middleware = new HsSqlAgentOidcCallbackMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context, handlerProvider.Object);

        Assert.True(nextCalled);
        handlerProvider.Verify(provider => provider.GetHandlerAsync(
            context,
            HsSqlAgentAuthenticationSchemes.Oidc), Times.Once);
        handlerProvider.VerifyNoOtherCalls();
    }
}
