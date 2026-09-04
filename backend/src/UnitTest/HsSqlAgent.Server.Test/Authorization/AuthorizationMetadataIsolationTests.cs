using System.Reflection;
using HsSqlAgent.Server.Authorization;
using HsSqlAgent.Server.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Filters;
using Xunit;

namespace HsSqlAgent.Server.Test.Authorization;

public class AuthorizationMetadataIsolationTests
{
    [Fact]
    public void HsSqlAgentControllers_DoNotDependOnHostDefaultAuthorizationPolicy()
    {
        var assembly = typeof(AuthController).Assembly;
        var bareAuthorization = assembly.GetTypes()
            .Where(type => type.Namespace?.StartsWith("HsSqlAgent.Server.Controllers", StringComparison.Ordinal) == true)
            .SelectMany(type =>
                type.GetCustomAttributes<AuthorizeAttribute>(inherit: false)
                    .Select(attribute => $"{type.Name}: {attribute.Policy ?? "<default>"}")
                    .Concat(type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                        .SelectMany(method => method.GetCustomAttributes<AuthorizeAttribute>(inherit: false)
                            .Select(attribute => $"{type.Name}.{method.Name}: {attribute.Policy ?? "<default>"}"))))
            .Where(entry => entry.EndsWith(": <default>", StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(bareAuthorization);
    }

    [Fact]
    public void PermissionAttributes_AreMvcFilters_NotAspNetNamedPolicyMetadata()
    {
        Assert.True(typeof(IFilterFactory).IsAssignableFrom(typeof(HasPermissionAttribute)));
        Assert.True(typeof(IFilterFactory).IsAssignableFrom(typeof(HasAnyPermissionAttribute)));
        Assert.False(typeof(IAuthorizeData).IsAssignableFrom(typeof(HasPermissionAttribute)));
        Assert.False(typeof(IAuthorizeData).IsAssignableFrom(typeof(HasAnyPermissionAttribute)));
    }
}
