using System.Text.Json;
using Admin.Service.Data;
using Admin.Service.Interfaces;
using Admin.Service.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Admin.Service.Services;

public class AuditRetentionService(
    IAdminContext context,
    IAuditService auditService,
    IOptions<OperabilitySettings> settings) : IAuditRetentionService
{
    public async Task<AuditRetentionResult> ExecuteAsync(bool dryRun, CancellationToken cancellationToken = default)
    {
        var options = settings.Value;
        if (options.AuditRetentionDays <= 0)
            throw new InvalidOperationException("Audit retention is disabled. Configure a positive retention period first.");
        var cutoff = DateTime.UtcNow.AddDays(-options.AuditRetentionDays);
        var count = await context.AuditLogs.LongCountAsync(x => x.CreatedAt < cutoff, cancellationToken);
        var result = new AuditRetentionResult
        {
            Cutoff = cutoff, MatchingCount = count, DryRun = dryRun, Mode = options.AuditRetentionMode
        };
        if (dryRun || count == 0) return result;

        string? archiveFile = null;
        if (string.Equals(options.AuditRetentionMode, "Archive", StringComparison.OrdinalIgnoreCase))
        {
            var directory = Path.GetFullPath(options.AuditArchivePath, AppContext.BaseDirectory);
            Directory.CreateDirectory(directory);
            archiveFile = Path.Combine(directory, $"audit-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}.jsonl");
            var temporaryFile = archiveFile + ".tmp";
            try
            {
                await using (var writer = new StreamWriter(temporaryFile, append: false))
                {
                    await foreach (var item in context.AuditLogs.AsNoTracking().Where(x => x.CreatedAt < cutoff)
                        .OrderBy(x => x.Id).AsAsyncEnumerable().WithCancellation(cancellationToken))
                        await writer.WriteLineAsync(JsonSerializer.Serialize(item).AsMemory(), cancellationToken);
                }
                File.Move(temporaryFile, archiveFile);
            }
            catch
            {
                if (File.Exists(temporaryFile)) File.Delete(temporaryFile);
                throw;
            }
            result.ArchiveFile = archiveFile;
        }
        else if (!string.Equals(options.AuditRetentionMode, "Purge", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("AuditRetentionMode must be Purge or Archive.");
        }

        result.DeletedCount = await context.AuditLogs.Where(x => x.CreatedAt < cutoff).ExecuteDeleteAsync(cancellationToken);
        await auditService.WriteAsync(
            "audit.retention.executed", cutoff.ToString("O"), "success",
            $"Mode: {result.Mode}; Deleted: {result.DeletedCount}; Archive: {archiveFile ?? "none"}",
            actorType: "system", cancellationToken: cancellationToken);
        return result;
    }
}
