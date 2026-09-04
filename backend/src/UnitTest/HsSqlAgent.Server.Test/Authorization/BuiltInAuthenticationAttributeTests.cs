using System.Security.Claims;
using HsSqlAgent.Server.Attributes;
using HsSqlAgent.Server.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace HsSqlAgent.Server.Test.Authorization;

public class BuiltInAuthenticationAttributeTests
{
    [Fact]
    public async Task AccessAuthorize_AuthenticatesNamespacedBearerAndSetsPrincipal()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("typ", "access"),
            new Claim("sub", "42")
        ], HsSqlAgentAuthenticationSchemes.Bearer));
        var authenticationService = CreateAuthenticationService(
            HsSqlAgentAuthenticationSchemes.Bearer,
            AuthenticateResult.Success(new AuthenticationTicket(principal, HsSqlAgentAuthenticationSchemes.Bearer)));
        using var provider = CreateProvider(authenticationService.Object);
        var context = CreateFilterContext(provider);

        await new AccessAuthorizeAttribute().OnAuthorizationAsync(context);

        Assert.Null(context.Result);
        Assert.Same(principal, context.HttpContext.User);
        authenticationService.Verify(service => service.AuthenticateAsync(
            context.HttpContext,
            HsSqlAgentAuthenticationSchemes.Bearer), Times.Once);
    }

    [Fact]
    public async Task AccessAuthorize_ForbidsAuthenticatedWrongTokenType()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("typ", "refresh")
        ], HsSqlAgentAuthenticationSchemes.Bearer));
        var authenticationService = CreateAuthenticationService(
            HsSqlAgentAuthenticationSchemes.Bearer,
            AuthenticateResult.Success(new AuthenticationTicket(principal, HsSqlAgentAuthenticationSchemes.Bearer)));
        using var provider = CreateProvider(authenticationService.Object);
        var context = CreateFilterContext(provider);

        await new AccessAuthorizeAttribute().OnAuthorizationAsync(context);

        var forbid = Assert.IsType<ForbidResult>(context.Result);
        Assert.Contains(HsSqlAgentAuthenticationSchemes.Bearer, forbid.AuthenticationSchemes);
        Assert.Same(principal, context.HttpContext.User);
    }

    [Fact]
    public async Task RefreshAuthorize_ChallengesWhenNamespacedBearerAuthenticationFails()
    {
        var authenticationService = CreateAuthenticationService(
            HsSqlAgentAuthenticationSchemes.Bearer,
            AuthenticateResult.NoResult());
        using var provider = CreateProvider(authenticationService.Object);
        var context = CreateFilterContext(provider);

        await new RefreshAuthorizeAttribute().OnAuthorizationAsync(context);

        var challenge = Assert.IsType<ChallengeResult>(context.Result);
        Assert.Contains(HsSqlAgentAuthenticationSchemes.Bearer, challenge.AuthenticationSchemes);
    }

    [Fact]
    public async Task ExternalLoginAuthorize_UsesOnlyNamespacedExternalCookie()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "external-user")
        ], HsSqlAgentAuthenticationSchemes.ExternalCookie));
        var authenticationService = CreateAuthenticationService(
            HsSqlAgentAuthenticationSchemes.ExternalCookie,
            AuthenticateResult.Success(new AuthenticationTicket(principal, HsSqlAgentAuthenticationSchemes.ExternalCookie)));
        using var provider = CreateProvider(authenticationService.Object);
        var context = CreateFilterContext(provider);

        await new ExternalLoginAuthorizeAttribute().OnAuthorizationAsync(context);

        Assert.Null(context.Result);
        Assert.Same(principal, context.HttpContext.User);
        authenticationService.Verify(service => service.AuthenticateAsync(
            context.HttpContext,
            HsSqlAgentAuthenticationSchemes.ExternalCookie), Times.Once);
    }

    private static Mock<IAuthenticationService> CreateAuthenticationService(
        string scheme,
        AuthenticateResult result)
    {
        var authenticationService = new Mock<IAuthenticationService>();
        authenticationService
            .Setup(service => service.AuthenticateAsync(It.IsAny<HttpContext>(), scheme))
            .ReturnsAsync(result);
        return authenticationService;
    }

    private static ServiceProvider CreateProvider(IAuthenticationService authenticationService)
    {
        var services = new ServiceCollection();
        services.AddSingleton(authenticationService);
        return services.BuildServiceProvider();
    }

    private static AuthorizationFilterContext CreateFilterContext(IServiceProvider provider)
    {
        var httpContext = new DefaultHttpContext { RequestServices = provider };
        var actionContext = new ActionContext(
            httpContext,
            new RouteData(),
            new ActionDescriptor(),
            new ModelStateDictionary());
        return new AuthorizationFilterContext(actionContext, []);
    }
}
