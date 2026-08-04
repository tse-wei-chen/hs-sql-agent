using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Auth.Service.Data;
using Auth.Service.Data.Entites;
using Auth.Service.Interfaces;
using Auth.Service.Services;
using Microsoft.AspNetCore.Http;
using Moq;
using HsSqlAgent.Server.Middleware;
using Moq.EntityFrameworkCore;
using Xunit;

namespace HsSqlAgent.Server.Test.Middleware;

public class TokenRevocationMiddlewareTests
{
    private static readonly Guid ActiveSessionId = Guid.NewGuid();
    private readonly Mock<ITokenRevocationService> _revocationMock;
    private readonly Mock<IAuthContext> _authContextMock;

    public TokenRevocationMiddlewareTests()
    {
        _revocationMock = new Mock<ITokenRevocationService>();
        _authContextMock = new Mock<IAuthContext>();
        _authContextMock.Setup(x => x.Members).ReturnsDbSet(new List<Member>
        {
            new() { Id = 1, Username = "user", Mail = "user@test.com", PasswordHash = "hash", IsActive = true, SecurityVersion = 1 }
        });
        _authContextMock.Setup(x => x.AuthSessions).ReturnsDbSet(new List<AuthSession>
        {
            new() { Id = ActiveSessionId, MemberId = 1, ExpiresAt = DateTime.UtcNow.AddDays(1) }
        });
    }

    [Fact]
    public async Task InvokeAsync_Returns401_WhenJtiIsRevoked()
    {
        var jti = "revoked-jti";
        var context = CreateContextWithJti(jti);
        _revocationMock.Setup(r => r.IsRevokedAsync(jti, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var nextCalled = false;
        RequestDelegate next = _ => { nextCalled = true; return Task.CompletedTask; };

        var middleware = new TokenRevocationMiddleware(next);
        await middleware.InvokeAsync(context, _revocationMock.Object, _authContextMock.Object);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
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
        await middleware.InvokeAsync(context, _revocationMock.Object, _authContextMock.Object);

        Assert.NotEqual(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.True(nextCalled);
    }

    [Fact]
    public async Task InvokeAsync_CallsNext_WhenNoJtiClaim()
    {
        var context = CreateContextWithoutJti();
        var nextCalled = false;
        RequestDelegate next = _ => { nextCalled = true; return Task.CompletedTask; };

        var middleware = new TokenRevocationMiddleware(next);
        await middleware.InvokeAsync(context, _revocationMock.Object, _authContextMock.Object);

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
        await middleware.InvokeAsync(context, _revocationMock.Object, _authContextMock.Object);

        Assert.True(nextCalled);
        _revocationMock.Verify(r => r.IsRevokedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task InvokeAsync_Returns401_WhenSecurityVersionIsStale()
    {
        _authContextMock.Setup(x => x.Members).ReturnsDbSet(new List<Member>
        {
            new() { Id = 1, Username = "user", Mail = "user@test.com", PasswordHash = "hash", IsActive = true, SecurityVersion = 2 }
        });
        var context = CreateContextWithJti("valid-jti");
        _revocationMock.Setup(r => r.IsRevokedAsync("valid-jti", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var nextCalled = false;
        var middleware = new TokenRevocationMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context, _revocationMock.Object, _authContextMock.Object);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.False(nextCalled);
    }

    private static DefaultHttpContext CreateContextWithJti(string jti)
    {
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Jti, jti),
            new Claim(JwtRegisteredClaimNames.Sub, "1"),
            new Claim(AuthService.SecurityVersionClaim, "1"),
            new Claim(AuthService.SessionIdClaim, ActiveSessionId.ToString())
        };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        var principal = new ClaimsPrincipal(identity);
        return new DefaultHttpContext { User = principal };
    }

    private static DefaultHttpContext CreateContextWithoutJti()
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(JwtRegisteredClaimNames.Sub, "1"),
            new Claim(AuthService.SecurityVersionClaim, "1"),
            new Claim(AuthService.SessionIdClaim, ActiveSessionId.ToString())
        ], "TestAuth");
        var principal = new ClaimsPrincipal(identity);
        return new DefaultHttpContext { User = principal };
    }
}
