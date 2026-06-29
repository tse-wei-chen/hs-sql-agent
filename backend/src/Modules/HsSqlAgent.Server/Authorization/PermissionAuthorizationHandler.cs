using System.Security.Claims;
using Auth.Service.Data;
using Common.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace HsSqlAgent.Server.Authorization;

public class PermissionAuthorizationHandler(IAuthContext context, ICacheService cache)
    : AuthorizationHandler<PermissionRequirement>
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

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
            .OrderBy(x => x)
            .ToList();

        if (roleIds.Count == 0)
            return;

        var cacheKey = $"perm:roles:{string.Join("|", roleIds)}";
        var permissions = await cache.GetAsync<HashSet<string>>(cacheKey);

        if (permissions is null)
        {
            var rows = await context.PermissionActions
                .AsNoTracking()
                .Where(x => roleIds.Contains(x.RoleId))
                .Select(x => x.Permission.Path + "." + x.Action.Code)
                .Distinct()
                .ToListAsync();

            permissions = [.. rows];
            await cache.SetAsync(cacheKey, permissions, CacheTtl);
        }

        if (permissions.Contains($"{req.Path}.{req.Action}"))
            ctx.Succeed(req);
    }
}
