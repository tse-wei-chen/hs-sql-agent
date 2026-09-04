using Auth.Service.Authorization;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace HsSqlAgent.Server.Authorization;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public class HasPermissionAttribute : Attribute, IFilterFactory, IOrderedFilter
{
    public HasPermissionAttribute(string path, string? action = null)
    {
        PermissionCanonicalPaths.RequireCanonical(path, nameof(path));
        if (action is not null)
            PermissionCanonicalPaths.RequirePermissionKey($"{path}.{action}", nameof(action));

        Permission = action is not null ? $"{path}.{action}" : path;
    }

    public string Permission { get; }

    public bool IsReusable => false;

    public int Order => -1000;

    public IFilterMetadata CreateInstance(IServiceProvider serviceProvider)
        => new HsSqlAgentPermissionAuthorizationFilter(
            serviceProvider.GetRequiredService<IHsSqlAgentPermissionAuthorizer>(),
            [Permission]);
}
