using System.Security.Claims;
using Auth.Service.Data;
using Common.Interfaces;
using HsSqlAgent.Server.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace HsSqlAgent.Server.Test.Authorization;

public class PermissionAuthorizerAuthenticationTests
{
    [Fact]
    public async Task AuthorizeAsync_AuthenticatesNamespacedBearerBeforePermissionEvaluation()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("typ", "access")
        ], HsSqlAgentAuthenticationSchemes.Bearer));
        var authenticationService = new Mock<IAuthenticationService>();
        authenticationService
            .Setup(service => service.AuthenticateAsync(
                It.IsAny<HttpContext>(),
                HsSqlAgentAuthenticationSchemes.Bearer))
            .ReturnsAsync(AuthenticateResult.Success(
                new AuthenticationTicket(principal, HsSqlAgentAuthenticationSchemes.Bearer)));

        var services = new ServiceCollection();
        services.AddSingleton<IAuthenticationService>(authenticationService.Object);
        using var provider = services.BuildServiceProvider();
        var httpContext = new DefaultHttpContext { RequestServices = provider };
        var handler = new PermissionAuthorizationHandler(Mock.Of<IAuthContext>(), Mock.Of<ICacheService>());

        var authorized = await handler.AuthorizeAsync(
            httpContext,
            ["/runtime/db-management.view"],
            TestContext.Current.CancellationToken);

        Assert.False(authorized); // no role_id claim, so permission evaluation still fails closed
        Assert.Same(principal, httpContext.User);
        authenticationService.Verify(service => service.AuthenticateAsync(
            httpContext,
            HsSqlAgentAuthenticationSchemes.Bearer), Times.Once);
    }

    [Fact]
    public async Task AuthorizeAsync_DoesNotTrustHostPrincipal_WhenNamespacedBearerFails()
    {
        var hostPrincipal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("typ", "access"),
            new Claim("role_id", "1"),
            new Claim("sub", "1"),
            new Claim(Auth.Service.Services.AuthService.SecurityVersionClaim, "1")
        ], "Host.Cookie"));
        var authenticationService = new Mock<IAuthenticationService>();
        authenticationService
            .Setup(service => service.AuthenticateAsync(
                It.IsAny<HttpContext>(),
                HsSqlAgentAuthenticationSchemes.Bearer))
            .ReturnsAsync(AuthenticateResult.NoResult());

        var services = new ServiceCollection();
        services.AddSingleton<IAuthenticationService>(authenticationService.Object);
        using var provider = services.BuildServiceProvider();
        var httpContext = new DefaultHttpContext
        {
            RequestServices = provider,
            User = hostPrincipal
        };
        var handler = new PermissionAuthorizationHandler(Mock.Of<IAuthContext>(), Mock.Of<ICacheService>());

        var authorized = await handler.AuthorizeAsync(
            httpContext,
            ["/runtime/db-management.view"],
            TestContext.Current.CancellationToken);

        Assert.False(authorized);
        Assert.Same(hostPrincipal, httpContext.User);
        authenticationService.Verify(service => service.AuthenticateAsync(
            httpContext,
            HsSqlAgentAuthenticationSchemes.Bearer), Times.Once);
    }
}
