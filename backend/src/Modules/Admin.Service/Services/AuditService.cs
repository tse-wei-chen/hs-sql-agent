using Admin.Service.Data;
using Admin.Service.Data.Entites;
using Admin.Service.Interfaces;
using Admin.Service.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using Common.Models;

namespace Admin.Service.Services;

public class AuditService(IAdminContext context, IHttpContextAccessor httpContextAccessor, IAuditQueue auditQueue) : IAuditService
{
    private readonly IAdminContext _context = context;
    private readonly IHttpContextAccessor _httpContextAccessor = httpContextAccessor;
    private readonly IAuditQueue _auditQueue = auditQueue;

    public async Task WriteAsync(
        string action,
        string target,
        string result,
        string? detail = null,
        string? actorType = null,
        string? actorId = null,
        string? ipAddress = null,
        string? userAgent = null,
        CancellationToken cancellationToken = default)
    {
        var item = new AuditLog
        {
            Action = action,
            Target = target,
            Result = result,
            Detail = detail,
            ActorType = string.IsNullOrWhiteSpace(actorType) ? "system" : actorType,
            ActorId = actorId,
            IpAddress = ipAddress,
            UserAgent = userAgent,
            CreatedAt = DateTime.UtcNow
        };

        // Offload to background queue instead of waiting for DB save
        _auditQueue.TryEnqueue(item);
        await Task.CompletedTask;
    }

    public async Task WriteLogAsync(
        string action,
        string target,
        string result,
        string? detail = null,
        CancellationToken cancellationToken = default)
    {
        var httpContext = _httpContextAccessor.HttpContext;
        string? actorId = null;
        string? actorType = "system";
        string? ipAddress = null;
        string? userAgent = null;

        if (httpContext != null)
        {
            // Try to get ActorId from JWT
            actorId = httpContext.User?.FindFirstValue(JwtRegisteredClaimNames.Sub)
                      ?? httpContext.User?.FindFirstValue(ClaimTypes.NameIdentifier);

            if (actorId != null)
            {
                actorType = "admin";
            }
            else
            {
                // Try to get ActorId from MCP context
                if (httpContext.Items.TryGetValue(McpContextItemKeys.AccessKeyId, out var keyIdObj) && keyIdObj != null)
                {
                    actorId = keyIdObj.ToString();
                    actorType = "mcp-key";
                }
            }

            ipAddress = httpContext.Connection.RemoteIpAddress?.ToString();
            userAgent = httpContext.Request.Headers.UserAgent.ToString();
        }

        await WriteAsync(
            action: action,
            target: target,
            result: result,
            detail: detail,
            actorType: actorType,
            actorId: actorId,
            ipAddress: ipAddress,
            userAgent: userAgent,
            cancellationToken: cancellationToken);
    }

    public async Task<AuditLogQueryResult> QueryAsync(int page, int pageSize, string? action = null, string? keyword = null, CancellationToken cancellationToken = default)
    {
        var safePage = page <= 0 ? 1 : page;
        var safePageSize = pageSize <= 0 ? 20 : Math.Min(pageSize, 200);

        var query = _context.AuditLogs.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(action))
        {
            query = query.Where(x => x.Action == action);
        }

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            query = query.Where(x =>
                (x.Target != null && x.Target.Contains(keyword)) ||
                (x.Detail != null && x.Detail.Contains(keyword)) ||
                (x.ActorId != null && x.ActorId.Contains(keyword)));
        }

        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(x => x.CreatedAt)
            .Skip((safePage - 1) * safePageSize)
            .Take(safePageSize)
            .Select(x => new AuditLogItem
            {
                Id = x.Id,
                ActorType = x.ActorType,
                ActorId = x.ActorId,
                Action = x.Action,
                Target = x.Target,
                Detail = x.Detail,
                Result = x.Result,
                IpAddress = x.IpAddress,
                UserAgent = x.UserAgent,
                CreatedAt = x.CreatedAt
            })
            .ToListAsync(cancellationToken);

        return new AuditLogQueryResult
        {
            Items = items,
            Page = safePage,
            PageSize = safePageSize,
            TotalCount = totalCount
        };
    }

    public async Task<IReadOnlyCollection<AuditDailySummaryItem>> QueryDailySummaryAsync(int days, string? action = null, string? keyword = null, CancellationToken cancellationToken = default)
    {
        var safeDays = days <= 0 ? 7 : Math.Min(days, 30);
        var startDay = DateTime.UtcNow.Date.AddDays(-(safeDays - 1));

        var query = _context.AuditLogs
            .AsNoTracking()
            .Where(x => x.CreatedAt >= startDay)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(action))
        {
            query = query.Where(x => x.Action == action);
        }

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            query = query.Where(x =>
                (x.Target != null && x.Target.Contains(keyword)) ||
                (x.Detail != null && x.Detail.Contains(keyword)) ||
                (x.ActorId != null && x.ActorId.Contains(keyword)));
        }

        var records = await query
            .Select(x => new { x.CreatedAt, x.Result })
            .ToListAsync(cancellationToken);

        var bucket = new Dictionary<DateTime, AuditDailySummaryItem>();
        for (var i = 0; i < safeDays; i++)
        {
            var day = startDay.AddDays(i);
            bucket[day] = new AuditDailySummaryItem
            {
                Day = day,
                SuccessCount = 0,
                FailedCount = 0
            };
        }

        foreach (var record in records)
        {
            var day = record.CreatedAt.Date;
            if (!bucket.TryGetValue(day, out var item))
            {
                continue;
            }

            if (string.Equals(record.Result, "success", StringComparison.OrdinalIgnoreCase))
            {
                item.SuccessCount += 1;
            }
            else
            {
                item.FailedCount += 1;
            }
        }

        return [.. bucket
            .OrderBy(x => x.Key)
            .Select(x => x.Value)];
    }
}
