using Auth.Service.Data;
using Common.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace HsSqlAgent.Server.Authorization;

public class PermissionAuthorizationHandler(IAuthContext context, ICacheService cache)
    : AuthorizationHandler<PermissionRequirement>
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private static readonly object RequestSnapshotKey = new();

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext ctx,
        PermissionRequirement req)
    {
        if (ctx.User.Identity?.IsAuthenticated != true)
            return;

        var roleIds = ctx.User.FindAll("role_id")
            .Select(c => int.TryParse(c.Value, out var id) ? id : (int?)null)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .OrderBy(x => x)
            .ToList();

        if (roleIds.Count == 0)
            return;

        var memberId = ctx.User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
        var securityVersion = ctx.User.FindFirst(Auth.Service.Services.AuthService.SecurityVersionClaim)?.Value;
        if (string.IsNullOrWhiteSpace(memberId) || string.IsNullOrWhiteSpace(securityVersion))
            return;

        var cacheKey = $"perm:user:{memberId}:v{securityVersion}:roles:{string.Join("|", roleIds)}";
        var httpContext = ctx.Resource as HttpContext;
        var cancellationToken = httpContext?.RequestAborted ?? CancellationToken.None;

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

        if (req.Permissions.Any(permissions.Contains))
            ctx.Succeed(req);
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
