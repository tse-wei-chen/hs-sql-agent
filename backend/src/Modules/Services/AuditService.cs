using Microsoft.EntityFrameworkCore;
using Modules.Data;
using Modules.Data.Entites;
using Modules.Interfaces;
using Modules.Models;

namespace Modules.Services;

public class AuditService(IAdminContext context) : IAuditService
{
    private readonly IAdminContext _context = context;

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

        _context.AuditLogs.Add(item);
        await _context.SaveChangesAsync(cancellationToken);
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

        return bucket
            .OrderBy(x => x.Key)
            .Select(x => x.Value)
            .ToList();
    }
}
