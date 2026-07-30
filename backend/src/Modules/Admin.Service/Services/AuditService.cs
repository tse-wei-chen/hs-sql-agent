using Admin.Service.Data;
using Admin.Service.Data.Entites;
using Admin.Service.Interfaces;
using Admin.Service.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using Common.Models;
using System.Text.RegularExpressions;

namespace Admin.Service.Services;

public class AuditService(IAdminContext context, IHttpContextAccessor httpContextAccessor) : IAuditService
{
    private readonly IAdminContext _context = context;
    private readonly IHttpContextAccessor _httpContextAccessor = httpContextAccessor;

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
            EventId = Guid.NewGuid(),
            Action = action,
            Target = target,
            Result = result,
            Detail = Redact(detail),
            ActorType = string.IsNullOrWhiteSpace(actorType) ? "system" : actorType,
            ActorId = actorId,
            IpAddress = ipAddress,
            UserAgent = userAgent,
            CreatedAt = DateTime.UtcNow
        };

        await PersistAsync(item, cancellationToken);
    }

    public async Task WriteLogAsync(
        string action,
        string target,
        string result,
        string? detail = null,
        CancellationToken cancellationToken = default)
    {
        await WriteEventAsync(
            action: action,
            target: target,
            result: result,
            eventContext: CreateHttpContext(),
            detail: detail,
            cancellationToken: cancellationToken);
    }

    public async Task WriteEventAsync(
        string action,
        string target,
        string result,
        AuditEventContext eventContext,
        string? detail = null,
        CancellationToken cancellationToken = default)
    {
        var httpContext = _httpContextAccessor.HttpContext;
        var inferred = CreateHttpContext();
        var actorId = httpContext?.User?.FindFirstValue(JwtRegisteredClaimNames.Sub)
                      ?? httpContext?.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        var actorType = actorId == null ? "system" : "admin";
        if (actorId == null &&
            httpContext?.Items.TryGetValue(McpContextItemKeys.AccessKeyId, out var keyIdObj) == true &&
            keyIdObj != null)
        {
            actorId = keyIdObj.ToString();
            actorType = "mcp-key";
        }

        var item = new AuditLog
        {
            EventId = Guid.NewGuid(),
            Action = action,
            Target = target,
            Result = result,
            Detail = Redact(detail),
            ActorType = actorType,
            ActorId = actorId,
            IpAddress = httpContext?.Connection.RemoteIpAddress?.ToString(),
            UserAgent = httpContext?.Request.Headers.UserAgent.ToString(),
            RequestId = eventContext.RequestId ?? inferred.RequestId,
            SessionId = eventContext.SessionId ?? inferred.SessionId,
            AccessKeyId = eventContext.AccessKeyId ?? inferred.AccessKeyId,
            DbManagementId = eventContext.DbManagementId ?? inferred.DbManagementId,
            DatabaseName = eventContext.DatabaseName ?? inferred.DatabaseName,
            ToolName = eventContext.ToolName,
            Operation = eventContext.Operation,
            DurationMs = eventContext.DurationMs,
            ReturnedRows = eventContext.ReturnedRows,
            AffectedRows = eventContext.AffectedRows,
            ApprovalStatus = eventContext.ApprovalStatus,
            ErrorCategory = eventContext.ErrorCategory,
            Definition = eventContext.Definition,
            CreatedAt = DateTime.UtcNow
        };

        await PersistAsync(item, cancellationToken);
    }

    public async Task<AuditLogQueryResult> QueryAsync(int page, int pageSize, string? action = null, string? keyword = null, CancellationToken cancellationToken = default)
        => await QueryAsync(new AuditLogFilter
        {
            Page = page,
            PageSize = pageSize,
            Action = action,
            Keyword = keyword
        }, cancellationToken);

    public async Task<AuditLogQueryResult> QueryAsync(
        AuditLogFilter filter,
        CancellationToken cancellationToken = default)
    {
        var safePage = filter.Page <= 0 ? 1 : filter.Page;
        var safePageSize = filter.PageSize <= 0 ? 20 : Math.Min(filter.PageSize, 200);

        var query = _context.AuditLogs.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(filter.Action))
        {
            query = query.Where(x => x.Action == filter.Action);
        }

        if (!string.IsNullOrWhiteSpace(filter.Keyword))
        {
            query = query.Where(x =>
                (x.Target != null && x.Target.Contains(filter.Keyword)) ||
                (x.Detail != null && x.Detail.Contains(filter.Keyword)) ||
                (x.ActorId != null && x.ActorId.Contains(filter.Keyword)));
        }
        if (filter.From.HasValue)
            query = query.Where(x => x.CreatedAt >= filter.From.Value);
        if (filter.To.HasValue)
            query = query.Where(x => x.CreatedAt <= filter.To.Value);
        if (!string.IsNullOrWhiteSpace(filter.Result))
            query = query.Where(x => x.Result == filter.Result);
        if (!string.IsNullOrWhiteSpace(filter.Actor))
            query = query.Where(x => x.ActorId == filter.Actor || x.ActorType == filter.Actor);
        if (filter.DbManagementId.HasValue)
            query = query.Where(x => x.DbManagementId == filter.DbManagementId);
        if (filter.AccessKeyId.HasValue)
            query = query.Where(x => x.AccessKeyId == filter.AccessKeyId);
        if (!string.IsNullOrWhiteSpace(filter.ToolName))
            query = query.Where(x => x.ToolName == filter.ToolName);

        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(x => x.CreatedAt)
            .Skip((safePage - 1) * safePageSize)
            .Take(safePageSize)
            .Select(x => new AuditLogItem
            {
                Id = x.Id,
                EventId = x.EventId,
                ActorType = x.ActorType,
                ActorId = x.ActorId,
                Action = x.Action,
                Target = x.Target,
                Detail = x.Detail,
                Result = x.Result,
                IpAddress = x.IpAddress,
                UserAgent = x.UserAgent,
                RequestId = x.RequestId,
                SessionId = x.SessionId,
                AccessKeyId = x.AccessKeyId,
                DbManagementId = x.DbManagementId,
                DatabaseName = x.DatabaseName,
                ToolName = x.ToolName,
                Operation = x.Operation,
                DurationMs = x.DurationMs,
                ReturnedRows = x.ReturnedRows,
                AffectedRows = x.AffectedRows,
                ApprovalStatus = x.ApprovalStatus,
                ErrorCategory = x.ErrorCategory,
                Definition = x.Definition,
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

    private AuditEventContext CreateHttpContext()
    {
        var context = _httpContextAccessor.HttpContext;
        if (context == null)
            return new AuditEventContext();

        return new AuditEventContext
        {
            RequestId = context.TraceIdentifier,
            SessionId = context.Request.Headers["Mcp-Session-Id"].FirstOrDefault(),
            AccessKeyId = GetItem<int>(context, McpContextItemKeys.AccessKeyId),
            DbManagementId = GetItem<int>(context, McpContextItemKeys.DbManagementId),
            DatabaseName = GetItem<string>(context, McpContextItemKeys.DatabaseName)
        };
    }

    private static T? GetItem<T>(HttpContext context, string key)
    {
        if (!context.Items.TryGetValue(key, out var value) || value == null)
            return default;
        return value is T typed ? typed : default;
    }

    private async Task PersistAsync(AuditLog item, CancellationToken cancellationToken)
    {
        _context.AuditLogs.Add(item);
        const int maxAttempts = 3;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await _context.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch when (attempt < maxAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt), cancellationToken);
            }
        }
    }

    private static string? Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        var redacted = Regex.Replace(
            value,
            @"(?i)\b(password|pwd|token|api[_-]?key|secret)\s*[=:]\s*[^;\s]+",
            "$1=[REDACTED]",
            RegexOptions.CultureInvariant);
        return Regex.Replace(
            redacted,
            @"(?i)\bBearer\s+[A-Za-z0-9._~+/=-]+",
            "Bearer [REDACTED]",
            RegexOptions.CultureInvariant);
    }
}
