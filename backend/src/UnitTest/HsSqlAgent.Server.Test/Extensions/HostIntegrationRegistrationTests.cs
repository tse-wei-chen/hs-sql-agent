using System.Security.Claims;
using HsSqlAgent.Server.Authorization;
using HsSqlAgent.Server.Extensions;
using HsSqlAgent.Server.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace HsSqlAgent.Server.Test.Extensions;

public class HostIntegrationRegistrationTests
{
    [Fact]
    public void AddHsSqlAgentBuiltInAuth_PreservesHostDefaultsPolicyProviderAndDataProtection()
    {
        var services = new ServiceCollection();
        var hostDefaultPolicy = new AuthorizationPolicyBuilder()
            .RequireClaim("host-user", "true")
            .Build();

        services.AddAuthentication(authentication =>
        {
            authentication.DefaultScheme = "Host.Scheme";
            authentication.DefaultAuthenticateScheme = "Host.Authenticate";
            authentication.DefaultChallengeScheme = "Host.Challenge";
        });
        services.AddAuthorizationBuilder().SetDefaultPolicy(hostDefaultPolicy);
        services.AddSingleton<IAuthorizationPolicyProvider, HostPolicyProvider>();
        services.AddDataProtection().SetApplicationName("Host.Application");

        services.AddHsSqlAgentCore(CreateOptions())
            .AddHsSqlAgentBuiltInAuth();

        using var provider = services.BuildServiceProvider();
        var authentication = provider.GetRequiredService<IOptions<AuthenticationOptions>>().Value;
        Assert.Equal("Host.Scheme", authentication.DefaultScheme);
        Assert.Equal("Host.Authenticate", authentication.DefaultAuthenticateScheme);
        Assert.Equal("Host.Challenge", authentication.DefaultChallengeScheme);

        var authorization = provider.GetRequiredService<IOptions<AuthorizationOptions>>().Value;
        Assert.Same(hostDefaultPolicy, authorization.DefaultPolicy);
        Assert.IsType<HostPolicyProvider>(provider.GetRequiredService<IAuthorizationPolicyProvider>());

        var dataProtection = provider.GetRequiredService<IOptions<DataProtectionOptions>>().Value;
        Assert.Equal("Host.Application", dataProtection.ApplicationDiscriminator);
    }

    [Fact]
    public async Task AddHsSqlAgentBuiltInAuth_UsesNamespacedSchemesWithoutCreatingDefaults()
    {
        var services = new ServiceCollection();
        services.AddHsSqlAgentCore(CreateOptions())
            .AddHsSqlAgentBuiltInAuth();

        using var provider = services.BuildServiceProvider();
        var authentication = provider.GetRequiredService<IOptions<AuthenticationOptions>>().Value;
        Assert.Null(authentication.DefaultScheme);
        Assert.Null(authentication.DefaultAuthenticateScheme);
        Assert.Null(authentication.DefaultChallengeScheme);

        var schemes = provider.GetRequiredService<IAuthenticationSchemeProvider>();
        Assert.NotNull(await schemes.GetSchemeAsync(HsSqlAgentAuthenticationSchemes.Bearer));
        Assert.NotNull(await schemes.GetSchemeAsync(HsSqlAgentAuthenticationSchemes.ExternalCookie));
        Assert.Null(await schemes.GetSchemeAsync("Bearer"));
        Assert.Null(await schemes.GetSchemeAsync("ExternalCookie"));
        Assert.Null(await schemes.GetSchemeAsync("oidc"));
    }

    [Fact]
    public void AddHsSqlAgentBuiltInAuth_UsesPermissionAuthorizerInsteadOfStaticPermissionPolicies()
    {
        var services = new ServiceCollection();
        services.AddHsSqlAgentCore(CreateOptions())
            .AddHsSqlAgentBuiltInAuth();

        using var provider = services.BuildServiceProvider();
        var authorization = provider.GetRequiredService<IOptions<AuthorizationOptions>>().Value;
        Assert.NotNull(authorization.GetPolicy(HsSqlAgentAuthorizationPolicies.Access));
        Assert.Null(authorization.GetPolicy("__perm__/auth/role.view"));

        using var scope = provider.CreateScope();
        var authorizer = scope.ServiceProvider.GetRequiredService<IHsSqlAgentPermissionAuthorizer>();
        Assert.Equal(HsSqlAgentAuthenticationSchemes.Bearer, authorizer.AuthenticationScheme);
    }

    [Fact]
    public async Task AddHsSqlAgentHostAuthorization_UsesHostPolicyProviderAndForwardsCanonicalPermissions()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization();
        services.AddSingleton<IAuthorizationPolicyProvider, HostPolicyProvider>();
        services.AddSingleton<IAuthorizationHandler, CanonicalPermissionHandler>();

        services.AddHsSqlAgentCore(CreateOptions())
            .AddHsSqlAgentHostAuthorization(HostPolicyProvider.PolicyName)
            .AddHsSqlAgentAdminApi();

        using var provider = services.BuildServiceProvider();
        Assert.IsType<HostPolicyProvider>(provider.GetRequiredService<IAuthorizationPolicyProvider>());

        using var scope = provider.CreateScope();
        var authorizer = scope.ServiceProvider.GetRequiredService<IHsSqlAgentPermissionAuthorizer>();
        Assert.Null(authorizer.AuthenticationScheme);

        var httpContext = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "host-user")],
                "Host.Authentication"))
        };

        var authorized = await authorizer.AuthorizeAsync(
            httpContext,
            ["/runtime/db-management.view"],
            CancellationToken.None);

        Assert.True(authorized);
    }

    [Fact]
    public void BuiltInAndHostAuthorizationModes_AreMutuallyExclusive()
    {
        var services = new ServiceCollection();
        var builder = services.AddHsSqlAgentCore(CreateOptions())
            .AddHsSqlAgentHostAuthorization(HostPolicyProvider.PolicyName);

        var error = Assert.Throws<InvalidOperationException>(() => builder.AddHsSqlAgentBuiltInAuth());
        Assert.Contains("mutually exclusive", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static HsSqlAgentServiceOptions CreateOptions() => new()
    {
        AdminConnectionString = "Data Source=:memory:",
        HmacSecretKey = "test-hmac-key-that-is-at-least-32-bytes",
        JwtSecretKey = "test-jwt-key-that-is-at-least-32-bytes"
    };

    private sealed class HostPolicyProvider : IAuthorizationPolicyProvider
    {
        public const string PolicyName = "Host.SqlAgentAdmin";

        private static readonly AuthorizationPolicy HostPolicy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .AddRequirements(new CanonicalPermissionRequirement())
            .Build();

        public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => Task.FromResult(HostPolicy);

        public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => Task.FromResult<AuthorizationPolicy?>(null);

        public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
            => Task.FromResult<AuthorizationPolicy?>(
                string.Equals(policyName, PolicyName, StringComparison.Ordinal) ? HostPolicy : null);
    }

    private sealed class CanonicalPermissionRequirement : IAuthorizationRequirement;

    private sealed class CanonicalPermissionHandler : AuthorizationHandler<CanonicalPermissionRequirement>
    {
        protected override Task HandleRequirementAsync(
            AuthorizationHandlerContext context,
            CanonicalPermissionRequirement requirement)
        {
            if (context.Resource is HsSqlAgentPermissionResource resource &&
                resource.Permissions.Contains("/runtime/db-management.view", StringComparer.Ordinal))
            {
                context.Succeed(requirement);
            }

            return Task.CompletedTask;
        }
    }
}
