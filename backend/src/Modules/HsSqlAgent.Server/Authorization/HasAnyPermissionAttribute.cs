using Auth.Service.Authorization;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace HsSqlAgent.Server.Authorization;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public sealed class HasAnyPermissionAttribute : Attribute, IFilterFactory, IOrderedFilter
{
    internal const string Prefix = "__perm_any__";
    private readonly string[] _permissions;

    public HasAnyPermissionAttribute(params string[] permissions)
    {
        if (permissions.Length == 0 || permissions.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("At least one permission is required.", nameof(permissions));

        foreach (var permission in permissions)
            PermissionCanonicalPaths.RequirePermissionKey(permission, nameof(permissions));

        _permissions = [.. permissions];
    }

    public bool IsReusable => false;

    public int Order => -1000;

    public IFilterMetadata CreateInstance(IServiceProvider serviceProvider)
        => new HsSqlAgentPermissionAuthorizationFilter(
            serviceProvider.GetRequiredService<IHsSqlAgentPermissionAuthorizer>(),
            _permissions);
}
