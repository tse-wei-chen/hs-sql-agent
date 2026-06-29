using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Auth.Service.Interfaces;
using Microsoft.AspNetCore.Http;
using Moq;
using HsSqlAgent.Server.Middleware;
using Xunit;

namespace HsSqlAgent.Server.Test.Middleware;

public class TokenRevocationMiddlewareTests
{
    private readonly Mock<ITokenRevocationService> _revocationMock;

    public TokenRevocationMiddlewareTests()
    {
        _revocationMock = new Mock<ITokenRevocationService>();
    }

    [Fact]
    public async Task InvokeAsync_Returns403_WhenJtiIsRevoked()
    {
        var jti = "revoked-jti";
        var context = CreateContextWithJti(jti);
        _revocationMock.Setup(r => r.IsRevokedAsync(jti, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var nextCalled = false;
        RequestDelegate next = _ => { nextCalled = true; return Task.CompletedTask; };

        var middleware = new TokenRevocationMiddleware(next);
        await middleware.InvokeAsync(context, _revocationMock.Object);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task InvokeAsync_CallsNext_WhenJtiIsNotRevoked()
    {
        var jti = "valid-jti";
        var context = CreateContextWithJti(jti);
        _revocationMock.Setup(r => r.IsRevokedAsync(jti, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var nextCalled = false;
        RequestDelegate next = _ => { nextCalled = true; return Task.CompletedTask; };

        var middleware = new TokenRevocationMiddleware(next);
        await middleware.InvokeAsync(context, _revocationMock.Object);

        Assert.NotEqual(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.True(nextCalled);
    }

    [Fact]
    public async Task InvokeAsync_CallsNext_WhenNoJtiClaim()
    {
        var context = CreateContextWithoutJti();
        var nextCalled = false;
        RequestDelegate next = _ => { nextCalled = true; return Task.CompletedTask; };

        var middleware = new TokenRevocationMiddleware(next);
        await middleware.InvokeAsync(context, _revocationMock.Object);

        Assert.True(nextCalled);
        _revocationMock.Verify(r => r.IsRevokedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task InvokeAsync_CallsNext_WhenJtiIsEmpty()
    {
        var context = CreateContextWithJti("");
        var nextCalled = false;
        RequestDelegate next = _ => { nextCalled = true; return Task.CompletedTask; };

        var middleware = new TokenRevocationMiddleware(next);
        await middleware.InvokeAsync(context, _revocationMock.Object);

        Assert.True(nextCalled);
        _revocationMock.Verify(r => r.IsRevokedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static DefaultHttpContext CreateContextWithJti(string jti)
    {
        var claims = new[] { new Claim(JwtRegisteredClaimNames.Jti, jti) };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        var principal = new ClaimsPrincipal(identity);
        return new DefaultHttpContext { User = principal };
    }

    private static DefaultHttpContext CreateContextWithoutJti()
    {
        var identity = new ClaimsIdentity("TestAuth");
        var principal = new ClaimsPrincipal(identity);
        return new DefaultHttpContext { User = principal };
    }
}
