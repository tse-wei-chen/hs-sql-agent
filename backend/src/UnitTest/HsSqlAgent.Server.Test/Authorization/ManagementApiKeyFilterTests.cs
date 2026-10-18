using System.Security.Claims;
using HsSqlAgent.Server.Authorization;
using HsSqlAgent.Server.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace HsSqlAgent.Server.Test.Authorization;

public sealed class ManagementApiKeyFilterTests
{
    [Fact]
    public async Task AllowedMachineKey_AuthorizesExistingCanonicalPermissionWithoutHumanJwt()
    {
        var valid = new ValidManagementApiKey(14, "saaS", ["/runtime/custom-tools.create"]);
        var (context, human, machine) = await Invoke(
            "/runtime/custom-tools", "create", "hsa_mgmt_" + new string('A', 43), valid);
        Assert.Null(context.Result);
        Assert.True(context.HttpContext.User.Identity?.IsAuthenticated);
        Assert.Equal("management-api-key:14", context.HttpContext.User.FindFirstValue("sub"));
        Assert.Equal("management-api-key", context.HttpContext.User.FindFirstValue("hsa_actor_type"));
        human.Verify(h => h.AuthorizeAsync(It.IsAny<HttpContext>(),
            It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()), Times.Never);
        machine.Verify(m => m.ValidateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ValidKeyWithoutGrant_IsForbiddenAndNeverFallsBackToHumanAuthorization()
    {
        var (context, human, _) = await Invoke(
            "/runtime/custom-tools", "edit", "hsa_mgmt_" + new string('A', 43),
            new ValidManagementApiKey(9, "restricted", ["/runtime/custom-tools.view"]));
        Assert.IsType<StatusCodeResult>(context.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, ((StatusCodeResult)context.Result!).StatusCode);
        human.Verify(h => h.AuthorizeAsync(It.IsAny<HttpContext>(),
            It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task InvalidManagementKey_IsUnauthorized()
    {
        var (context, human, _) = await Invoke(
            "/runtime/mcp-keys", "create", "hsa_mgmt_" + new string('A', 43), null);
        Assert.IsType<UnauthorizedResult>(context.Result);
        human.Verify(h => h.AuthorizeAsync(It.IsAny<HttpContext>(),
            It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MachineKeysCannotAccessSecurityOrIdentityEvenIfValidatorReturnsSuchGrants()
    {
        foreach (var path in new[] { "/runtime/security", "/auth/role" })
        {
            var (context, _, _) = await Invoke(
                path, "edit", "hsa_mgmt_" + new string('A', 43),
                new ValidManagementApiKey(1, "malformed", [path + ".edit"]));
            Assert.Equal(StatusCodes.Status403Forbidden, ((StatusCodeResult)context.Result!).StatusCode);
        }
    }

    [Fact]
    public async Task NonManagementCredential_ContinuesOriginalHumanAuthorizationPath()
    {
        var (context, human, machine) = await Invoke(
            "/runtime/custom-tools", "view", "regular.jwt.token", null, humanAllowed: true);
        Assert.Null(context.Result);
        human.Verify(h => h.AuthorizeAsync(It.IsAny<HttpContext>(),
            It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()), Times.Once);
        machine.Verify(m => m.ValidateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static async Task<(AuthorizationFilterContext Context,
        Mock<IHsSqlAgentPermissionAuthorizer> Human, Mock<IManagementApiKeyService> Machine)> Invoke(
        string path, string action, string token, ValidManagementApiKey? valid, bool humanAllowed = false)
    {
        var human = new Mock<IHsSqlAgentPermissionAuthorizer>();
        human.Setup(x => x.AuthorizeAsync(It.IsAny<HttpContext>(),
                It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<bool>(humanAllowed));
        var machine = new Mock<IManagementApiKeyService>();
        machine.Setup(x => x.ValidateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(valid);

        var services = new ServiceCollection();
        services.AddSingleton(human.Object);
        services.AddSingleton(machine.Object);
        using var provider = services.BuildServiceProvider();

        var http = new DefaultHttpContext { RequestServices = provider };
        http.Request.Headers.Authorization = "Bearer " + token;
        var context = new AuthorizationFilterContext(
            new ActionContext(http, new RouteData(), new ActionDescriptor()),
            new List<IFilterMetadata>());
        var filter = (IAsyncAuthorizationFilter)new HasPermissionAttribute(path, action)
            .CreateInstance(provider);
        await filter.OnAuthorizationAsync(context);
        return (context, human, machine);
    }
}
