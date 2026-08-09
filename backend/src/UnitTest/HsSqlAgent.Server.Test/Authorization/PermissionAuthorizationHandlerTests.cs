using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using Auth.Service.Data;
using Auth.Service.Data.Entites;
using Common.Interfaces;
using HsSqlAgent.Server.Authorization;
using Microsoft.AspNetCore.Authorization;
using Moq;
using Moq.EntityFrameworkCore;
using Xunit;

namespace HsSqlAgent.Server.Test.Authorization;

public class PermissionAuthorizationHandlerTests
{
    private readonly Mock<IAuthContext> _contextMock;
    private readonly Mock<ICacheService> _cacheMock;

    public PermissionAuthorizationHandlerTests()
    {
        _contextMock = new Mock<IAuthContext>();
        _cacheMock = new Mock<ICacheService>();
    }

    [Fact]
    public async Task HandleAsync_Succeeds_WhenUserHasPermission()
    {
        var role = new Role { Id = 1, Name = "Admin" };
        var permission = new Permission { Id = 1, Name = "Test", Path = "/test/path" };
        var action = new AuthAction { Id = 1, Code = "view", Name = "view" };

        _contextMock.Setup(c => c.PermissionActions).ReturnsDbSet(new List<PermissionAction>
        {
            new()
            {
                Id = 1,
                RoleId = 1,
                PermissionId = 1,
                ActionId = 1,
                Role = role,
                Permission = permission,
                Action = action
            }
        });

        _cacheMock.Setup(c => c.GetAsync<HashSet<string>>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((HashSet<string>?)null);

        var handler = new PermissionAuthorizationHandler(_contextMock.Object, _cacheMock.Object);
        var context = CreateContext("/test/path", "view", "1");

        await handler.HandleAsync(context);

        Assert.True(context.HasSucceeded);
        _cacheMock.Verify(c => c.SetAsync(
            It.IsAny<string>(),
            It.IsAny<HashSet<string>>(),
            It.IsAny<TimeSpan>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_DoesNotSucceed_WhenUserLacksPermission()
    {
        var role = new Role { Id = 1, Name = "Admin" };
        var permission = new Permission { Id = 1, Name = "Test", Path = "/test/path" };
        var action = new AuthAction { Id = 1, Code = "view", Name = "view" };

        _contextMock.Setup(c => c.PermissionActions).ReturnsDbSet(new List<PermissionAction>
        {
            new()
            {
                Id = 1,
                RoleId = 1,
                PermissionId = 1,
                ActionId = 1,
                Role = role,
                Permission = permission,
                Action = action
            }
        });

        _cacheMock.Setup(c => c.GetAsync<HashSet<string>>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((HashSet<string>?)null);

        var handler = new PermissionAuthorizationHandler(_contextMock.Object, _cacheMock.Object);
        var context = CreateContext("/test/path", "delete", "1");

        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task HandleAsync_DoesNotSucceed_WhenUserNotAuthenticated()
    {
        _cacheMock.Setup(c => c.GetAsync<HashSet<string>>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((HashSet<string>?)null);

        var handler = new PermissionAuthorizationHandler(_contextMock.Object, _cacheMock.Object);
        var requirement = new PermissionRequirement("/test/path", "view");
        var user = new ClaimsPrincipal(new ClaimsIdentity());
        var context = new AuthorizationHandlerContext([requirement], user, null);

        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
        _contextMock.Verify(c => c.PermissionActions, Times.Never);
    }

    [Fact]
    public async Task HandleAsync_DoesNotSucceed_WhenNoRoleIdClaims()
    {
        _cacheMock.Setup(c => c.GetAsync<HashSet<string>>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((HashSet<string>?)null);

        var handler = new PermissionAuthorizationHandler(_contextMock.Object, _cacheMock.Object);
        var requirement = new PermissionRequirement("/test/path", "view");
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Role, "Admin")], "test"));
        var context = new AuthorizationHandlerContext([requirement], user, null);

        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
        _contextMock.Verify(c => c.PermissionActions, Times.Never);
    }

    [Fact]
    public async Task HandleAsync_UsesCache_WhenCacheHit()
    {
        _cacheMock.Setup(c => c.GetAsync<HashSet<string>>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string> { "/test/path.view" });

        var handler = new PermissionAuthorizationHandler(_contextMock.Object, _cacheMock.Object);
        var context = CreateContext("/test/path", "view", "1");

        await handler.HandleAsync(context);

        Assert.True(context.HasSucceeded);
        _contextMock.Verify(c => c.PermissionActions, Times.Never);
    }

    [Fact]
    public async Task HandleAsync_Succeeds_WhenUserHasAnyOneOfRequiredPermissions()
    {
        _cacheMock.Setup(c => c.GetAsync<HashSet<string>>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string> { "/runtime/db-management.edit" });

        var user = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("role_id", "1"),
            new Claim(ClaimTypes.Role, "Operator"),
            new Claim(JwtRegisteredClaimNames.Sub, "42"),
            new Claim(Auth.Service.Services.AuthService.SecurityVersionClaim, "1")
        ], "test"));
        var requirement = new PermissionRequirement(
        [
            "/runtime/mcp-keys.create",
            "/runtime/db-management.create",
            "/runtime/db-management.edit"
        ]);
        var context = new AuthorizationHandlerContext([requirement], user, null);

        var handler = new PermissionAuthorizationHandler(_contextMock.Object, _cacheMock.Object);
        await handler.HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task HandleAsync_PopulatesCache_WhenCacheMiss()
    {
        var role = new Role { Id = 1, Name = "Admin" };
        var permission = new Permission { Id = 1, Name = "Test", Path = "/test/path" };
        var action = new AuthAction { Id = 1, Code = "view", Name = "view" };

        _contextMock.Setup(c => c.PermissionActions).ReturnsDbSet(new List<PermissionAction>
        {
            new()
            {
                Id = 1,
                RoleId = 1,
                PermissionId = 1,
                ActionId = 1,
                Role = role,
                Permission = permission,
                Action = action
            }
        });

        _cacheMock.SetupSequence(c => c.GetAsync<HashSet<string>>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((HashSet<string>?)null)
            .ReturnsAsync(new HashSet<string> { "/test/path.view" });

        var handler = new PermissionAuthorizationHandler(_contextMock.Object, _cacheMock.Object);
        var context = CreateContext("/test/path", "view", "1");

        await handler.HandleAsync(context);

        Assert.True(context.HasSucceeded);
        _cacheMock.Verify(c => c.SetAsync(
            It.IsAny<string>(),
            It.Is<HashSet<string>>(s => s.Contains("/test/path.view")),
            It.IsAny<TimeSpan>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private static AuthorizationHandlerContext CreateContext(string path, string action, params string[] roleIds)
    {
        var claims = new List<Claim>();
        foreach (var id in roleIds)
            claims.Add(new Claim("role_id", id));
        claims.Add(new Claim(ClaimTypes.Role, "Admin"));
        claims.Add(new Claim(JwtRegisteredClaimNames.Sub, "42"));
        claims.Add(new Claim(Auth.Service.Services.AuthService.SecurityVersionClaim, "1"));

        var user = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        var requirement = new PermissionRequirement(path, action);
        return new AuthorizationHandlerContext([requirement], user, null);
    }
}
