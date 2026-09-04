using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Auth.Service.Data;
using Common.Interfaces;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace HsSqlAgent.Server.Authorization;

public class PermissionAuthorizationHandler(IAuthContext context, ICacheService cache)
    : AuthorizationHandler<PermissionRequirement>, IHsSqlAgentPermissionAuthorizer
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private static readonly object RequestSnapshotKey = new();

    public string? AuthenticationScheme => HsSqlAgentAuthenticationSchemes.Bearer;

    public async ValueTask<bool> AuthorizeAsync(
        HttpContext httpContext,
        IReadOnlyCollection<string> permissions,
        CancellationToken cancellationToken = default)
    {
        var user = httpContext.User;

        // Built-in permissions are bound to HsSqlAgent's own namespaced bearer scheme. Never trust a
        // host principal merely because it happens to contain similarly named typ/role_id claims.
        // Direct unit tests can still supply an already-authenticated principal without constructing
        // an IAuthenticationService; real ASP.NET Core requests always have one after AddAuthentication().
        if (httpContext.RequestServices?.GetService(typeof(IAuthenticationService)) is IAuthenticationService authenticationService)
        {
            var authentication = await authenticationService.AuthenticateAsync(
                httpContext,
                HsSqlAgentAuthenticationSchemes.Bearer);
            if (!authentication.Succeeded || authentication.Principal is null)
                return false;

            user = authentication.Principal;
            httpContext.User = user;
        }

        return await AuthorizeCoreAsync(
            user,
            httpContext,
            permissions,
            requireAccessTokenType: true,
            cancellationToken);
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext ctx,
        PermissionRequirement req)
    {
        var httpContext = ctx.Resource as HttpContext;
        var cancellationToken = httpContext?.RequestAborted ?? CancellationToken.None;
        if (await AuthorizeCoreAsync(
                ctx.User,
                httpContext,
                req.Permissions,
                requireAccessTokenType: false,
                cancellationToken))
        {
            ctx.Succeed(req);
        }
    }

    private async Task<bool> AuthorizeCoreAsync(
        ClaimsPrincipal user,
        HttpContext? httpContext,
        IReadOnlyCollection<string> requestedPermissions,
        bool requireAccessTokenType,
        CancellationToken cancellationToken)
    {
        if (user.Identity?.IsAuthenticated != true)
            return false;

        if (requireAccessTokenType &&
            !string.Equals(user.FindFirst(JwtRegisteredClaimNames.Typ)?.Value, "access", StringComparison.Ordinal))
            return false;

        var roleIds = user.FindAll("role_id")
            .Select(c => int.TryParse(c.Value, out var id) ? id : (int?)null)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .OrderBy(x => x)
            .ToList();

        if (roleIds.Count == 0)
            return false;

        var memberId = user.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        var securityVersion = user.FindFirst(Auth.Service.Services.AuthService.SecurityVersionClaim)?.Value;
        if (string.IsNullOrWhiteSpace(memberId) || string.IsNullOrWhiteSpace(securityVersion))
            return false;

        var cacheKey = $"perm:user:{memberId}:v{securityVersion}:roles:{string.Join("|", roleIds)}";
        HashSet<string> permissions;
        if (httpContext is not null &&
            TryGetRequestSnapshot(httpContext, cacheKey, out var requestPermissions))
        {
            permissions = requestPermissions;
        }
        else
        {
            permissions = await LoadPermissionsAsync(cacheKey, roleIds, cancellationToken);
            if (httpContext is not null)
                GetRequestSnapshot(httpContext)[cacheKey] = permissions;
        }

        return requestedPermissions.Any(permissions.Contains);
    }

    private async Task<HashSet<string>> LoadPermissionsAsync(
        string cacheKey,
        IReadOnlyCollection<int> roleIds,
        CancellationToken cancellationToken)
    {
        var permissions = await cache.GetAsync<HashSet<string>>(cacheKey, cancellationToken);
        if (permissions is not null)
            return permissions;

        var rows = await context.PermissionActions
            .AsNoTracking()
            .Where(x => roleIds.Contains(x.RoleId))
            .Select(x => x.Permission.Path + "." + x.Action.Code)
            .Distinct()
            .ToListAsync(cancellationToken);

        permissions = [.. rows];
        await cache.SetAsync(cacheKey, permissions, CacheTtl, cancellationToken);
        return permissions;
    }

    private static bool TryGetRequestSnapshot(
        HttpContext httpContext,
        string cacheKey,
        out HashSet<string> permissions)
    {
        if (httpContext.Items.TryGetValue(RequestSnapshotKey, out var value) &&
            value is Dictionary<string, HashSet<string>> snapshot &&
            snapshot.TryGetValue(cacheKey, out var found))
        {
            permissions = found;
            return true;
        }

        permissions = null!;
        return false;
    }

    private static Dictionary<string, HashSet<string>> GetRequestSnapshot(HttpContext httpContext)
    {
        if (httpContext.Items.TryGetValue(RequestSnapshotKey, out var value) &&
            value is Dictionary<string, HashSet<string>> snapshot)
            return snapshot;

        snapshot = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        httpContext.Items[RequestSnapshotKey] = snapshot;
        return snapshot;
    }
}
