using System.Text.Json;
using Admin.Service.Data;
using Admin.Service.Data.Entites;
using Admin.Service.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Admin.Service.Models;

namespace Admin.Service.Services;

public class CustomSqlToolService(
    IAdminContext context,
    IConfiguration configuration) : ICustomSqlToolService
{
    private readonly IAdminContext _context = context;
    private readonly IConfiguration _configuration = configuration;

    public async Task<List<CustomSqlTool>> GetAllToolsAsync()
    {
        return await _context.CustomSqlTools.ToListAsync();
    }

    public async Task<CustomSqlTool?> GetToolByIdAsync(int id)
    {
        return await _context.CustomSqlTools.FindAsync(id);
    }

    public async Task<CustomSqlTool?> GetToolByNameAsync(string name)
    {
        return await _context.CustomSqlTools.FirstOrDefaultAsync(t => t.Name == name);
    }

    public async Task<List<CustomSqlTool>> GetPublishedToolsForDbAsync(
        int dbManagementId,
        CancellationToken cancellationToken = default)
    {
        var tools = await _context.CustomSqlTools
            .AsNoTracking()
            .Include(x => x.PublishedRevision)
            .Where(x => x.Status == CustomSqlToolStatuses.Published
                && x.PublishedRevisionId != null
                && x.PublishedRevision!.DbManagementId == dbManagementId)
            .ToListAsync(cancellationToken);
        return tools.Select(ToPublishedSnapshot).ToList();
    }

    public async Task<CustomSqlTool?> GetPublishedToolByNameAsync(
        string name,
        int dbManagementId,
        CancellationToken cancellationToken = default)
    {
        var tool = await _context.CustomSqlTools
            .AsNoTracking()
            .Include(x => x.PublishedRevision)
            .FirstOrDefaultAsync(x => x.Status == CustomSqlToolStatuses.Published
                && x.PublishedRevisionId != null
                && x.PublishedRevision!.DbManagementId == dbManagementId
                && x.PublishedRevision.Name == name,
                cancellationToken);
        return tool == null ? null : ToPublishedSnapshot(tool);
    }

    public Task<List<CustomSqlToolRevision>> GetRevisionsAsync(
        int toolId,
        CancellationToken cancellationToken = default)
        => _context.CustomSqlToolRevisions.AsNoTracking()
            .Where(x => x.CustomSqlToolId == toolId)
            .OrderByDescending(x => x.RevisionNumber)
            .ToListAsync(cancellationToken);

    public async Task<CustomSqlToolImpact?> GetImpactAsync(
        int toolId,
        CancellationToken cancellationToken = default)
    {
        var tool = await _context.CustomSqlTools.AsNoTracking()
            .Include(x => x.PublishedRevision)
            .FirstOrDefaultAsync(x => x.Id == toolId, cancellationToken);
        if (tool == null) return null;

        var dbIds = new[] { tool.DbManagementId, tool.PublishedRevision == null ? null : (int?)tool.PublishedRevision.DbManagementId }
            .Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToArray();
        var dbNames = await _context.DbManagement.AsNoTracking()
            .Where(x => dbIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);
        var keys = await _context.McpAccessKeys.AsNoTracking()
            .Where(x => x.IsActive && x.RevokedAt == null
                && (x.ExpiresAt == null || x.ExpiresAt > DateTime.UtcNow)
                && x.DbManagementId != null && dbIds.Contains(x.DbManagementId.Value))
            .Select(x => new { x.Id, x.Name, x.KeyPrefix, x.DbManagementId, x.AllowedTools })
            .ToListAsync(cancellationToken);

        IReadOnlyList<CustomSqlToolImpactKey> ResolveKeys(int? dbId, string? name)
            => dbId is null || string.IsNullOrWhiteSpace(name)
                ? []
                : keys.Where(x => x.DbManagementId == dbId && AllowsTool(x.AllowedTools, name))
                    .Select(x => new CustomSqlToolImpactKey(x.Id, x.Name, x.KeyPrefix))
                    .ToList();

        var published = tool.PublishedRevision;
        return new CustomSqlToolImpact
        {
            ToolId = tool.Id,
            DraftDbManagementId = tool.DbManagementId,
            DraftDatabaseName = tool.DbManagementId.HasValue && dbNames.TryGetValue(tool.DbManagementId.Value, out var draftDb)
                ? draftDb : null,
            PublishedDbManagementId = published?.DbManagementId,
            PublishedDatabaseName = published != null && dbNames.TryGetValue(published.DbManagementId, out var publishedDb)
                ? publishedDb : null,
            CurrentlyExposedToKeys = ResolveKeys(published?.DbManagementId, published?.Name),
            WouldExposeToKeys = ResolveKeys(tool.DbManagementId, tool.Name),
            BreakingChanges = DescribeBreakingChanges(tool, published),
            SqlChanged = published != null && !string.Equals(tool.SqlTemplate, published.SqlTemplate, StringComparison.Ordinal)
        };
    }

    public async Task<CustomSqlTool> CreateToolAsync(CustomSqlTool tool)
    {
        tool.Id = 0;
        tool.Status = CustomSqlToolStatuses.Draft;
        tool.PublishedRevisionId = null;
        tool.PublishedIdentity = null;
        tool.CreatedAt = DateTime.UtcNow;
        tool.LastModifiedAt = null;
        _context.CustomSqlTools.Add(tool);
        await _context.SaveChangesAsync();
        return tool;
    }

    public async Task<CustomSqlTool> UpdateToolAsync(CustomSqlTool tool)
    {
        var existing = await _context.CustomSqlTools.FindAsync(tool.Id)
            ?? throw new KeyNotFoundException($"Custom tool {tool.Id} was not found.");
        existing.Name = tool.Name;
        existing.Description = tool.Description;
        existing.SqlTemplate = tool.SqlTemplate;
        existing.Type = tool.Type;
        existing.ParametersJson = tool.ParametersJson;
        existing.DbManagementId = tool.DbManagementId;
        existing.LastModifiedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        return existing;
    }

    public async Task<CustomSqlTool?> PublishAsync(
        int id,
        string? actor,
        CancellationToken cancellationToken = default)
    {
        var tool = await _context.CustomSqlTools
            .Include(x => x.PublishedRevision)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (tool == null) return null;
        if (tool.DbManagementId is null)
            throw new InvalidOperationException("A database binding is required before publishing.");
        await EnsurePublishedIdentityAvailableAsync(tool.Id, tool.DbManagementId.Value, tool.Name, cancellationToken);

        var revision = await CreateRevisionAsync(tool, actor, tool.PublishedRevision, cancellationToken);
        tool.PublishedRevision = revision;
        tool.PublishedIdentity = BuildPublishedIdentity(tool.DbManagementId.Value, tool.Name);
        tool.Status = CustomSqlToolStatuses.Published;
        tool.LastModifiedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
        return tool;
    }

    public async Task<CustomSqlTool?> DisableAsync(int id, CancellationToken cancellationToken = default)
    {
        var tool = await _context.CustomSqlTools.FindAsync([id], cancellationToken);
        if (tool == null) return null;
        tool.Status = CustomSqlToolStatuses.Disabled;
        tool.PublishedIdentity = null;
        tool.LastModifiedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
        return tool;
    }

    public async Task<CustomSqlTool?> RollbackAsync(
        int id,
        int revisionId,
        string? actor,
        CancellationToken cancellationToken = default)
    {
        var tool = await _context.CustomSqlTools
            .Include(x => x.PublishedRevision)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (tool == null) return null;
        var target = await _context.CustomSqlToolRevisions.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == revisionId && x.CustomSqlToolId == id, cancellationToken)
            ?? throw new KeyNotFoundException("The requested revision does not belong to this tool.");

        tool.Name = target.Name;
        tool.Description = target.Description;
        tool.SqlTemplate = target.SqlTemplate;
        tool.Type = target.Type;
        tool.ParametersJson = target.ParametersJson;
        tool.DbManagementId = target.DbManagementId;
        await EnsurePublishedIdentityAvailableAsync(tool.Id, target.DbManagementId, target.Name, cancellationToken);
        var revision = await CreateRevisionAsync(tool, actor, tool.PublishedRevision, cancellationToken);
        tool.PublishedRevision = revision;
        tool.PublishedIdentity = BuildPublishedIdentity(target.DbManagementId, target.Name);
        tool.Status = CustomSqlToolStatuses.Published;
        tool.LastModifiedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
        return tool;
    }

    public async Task<bool> DeleteToolAsync(int id)
    {
        var tool = await _context.CustomSqlTools.FindAsync(id);
        if (tool == null) return false;

        // Break the pointer to the current revision before the tool-to-revisions
        // cascade; otherwise EF cannot order the circular pair of deletes.
        if (tool.PublishedRevisionId != null)
        {
            tool.PublishedRevision = null;
            tool.PublishedRevisionId = null;
            tool.PublishedIdentity = null;
            await _context.SaveChangesAsync();
        }
        _context.CustomSqlTools.Remove(tool);
        await _context.SaveChangesAsync();
        return true;
    }

    private async Task<CustomSqlToolRevision> CreateRevisionAsync(
        CustomSqlTool tool,
        string? actor,
        CustomSqlToolRevision? previous,
        CancellationToken cancellationToken)
    {
        var revisionNumber = (await _context.CustomSqlToolRevisions
            .Where(x => x.CustomSqlToolId == tool.Id)
            .MaxAsync(x => (int?)x.RevisionNumber, cancellationToken) ?? 0) + 1;
        var current = Snapshot(tool);
        var revision = new CustomSqlToolRevision
        {
            CustomSqlToolId = tool.Id,
            RevisionNumber = revisionNumber,
            DbManagementId = tool.DbManagementId!.Value,
            Name = tool.Name,
            Description = tool.Description,
            SqlTemplate = tool.SqlTemplate,
            Type = tool.Type,
            ParametersJson = tool.ParametersJson,
            DiffJson = JsonSerializer.Serialize(new
            {
                before = previous == null ? null : Snapshot(previous),
                after = current
            }),
            PublishedBy = actor,
            PublishedAt = DateTime.UtcNow
        };
        _context.CustomSqlToolRevisions.Add(revision);
        return revision;
    }

    private static CustomSqlTool ToPublishedSnapshot(CustomSqlTool tool)
    {
        var revision = tool.PublishedRevision
            ?? throw new InvalidOperationException("Published tool has no revision snapshot.");
        return new CustomSqlTool
        {
            Id = tool.Id,
            Name = revision.Name,
            Description = revision.Description,
            SqlTemplate = revision.SqlTemplate,
            Type = revision.Type,
            ParametersJson = revision.ParametersJson,
            DbManagementId = revision.DbManagementId,
            Status = tool.Status,
            PublishedRevisionId = revision.Id,
            PublishedIdentity = tool.PublishedIdentity,
            CreatedAt = tool.CreatedAt,
            LastModifiedAt = tool.LastModifiedAt
        };
    }

    private static object Snapshot(CustomSqlTool tool) => new
    {
        tool.DbManagementId,
        tool.Name,
        tool.Description,
        tool.Type,
        tool.SqlTemplate,
        tool.ParametersJson
    };

    private static object Snapshot(CustomSqlToolRevision revision) => new
    {
        DbManagementId = (int?)revision.DbManagementId,
        revision.Name,
        revision.Description,
        revision.Type,
        revision.SqlTemplate,
        revision.ParametersJson
    };

    private async Task EnsurePublishedIdentityAvailableAsync(
        int toolId,
        int dbManagementId,
        string name,
        CancellationToken cancellationToken)
    {
        var identity = BuildPublishedIdentity(dbManagementId, name);
        if (await _context.CustomSqlTools.AnyAsync(
            x => x.Id != toolId && x.PublishedIdentity == identity,
            cancellationToken))
            throw new InvalidOperationException($"A published tool named '{name}' already exists for this database.");
    }

    private static string BuildPublishedIdentity(int dbManagementId, string name)
        => $"{dbManagementId}:{name.Trim().ToLowerInvariant()}";

    private static bool AllowsTool(string? allowedTools, string name)
        => string.IsNullOrEmpty(allowedTools)
           || allowedTools.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
               .Contains(name, StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyList<string> DescribeBreakingChanges(
        CustomSqlTool tool,
        CustomSqlToolRevision? published)
    {
        if (published == null) return [];
        var changes = new List<string>();
        if (tool.DbManagementId != published.DbManagementId) changes.Add("Bound database changed.");
        if (!string.Equals(tool.Name, published.Name, StringComparison.Ordinal)) changes.Add("MCP tool name changed.");
        if (!string.Equals(tool.Type, published.Type, StringComparison.OrdinalIgnoreCase)) changes.Add("Operation type changed.");
        var draftParameters = ParseParameterTypes(tool.ParametersJson);
        var publishedParameters = ParseParameterTypes(published.ParametersJson);
        foreach (var parameter in publishedParameters)
        {
            if (!draftParameters.TryGetValue(parameter.Key, out var draftType))
                changes.Add($"Parameter '{parameter.Key}' was removed.");
            else if (!string.Equals(draftType, parameter.Value, StringComparison.OrdinalIgnoreCase))
                changes.Add($"Parameter '{parameter.Key}' changed type from {parameter.Value} to {draftType}.");
        }
        foreach (var parameter in draftParameters.Keys.Where(x => !publishedParameters.ContainsKey(x)))
            changes.Add($"Required parameter '{parameter}' was added.");
        return changes;
    }

    private static Dictionary<string, string> ParseParameterTypes(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new(StringComparer.OrdinalIgnoreCase);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateArray()
            .Where(x => x.TryGetProperty("name", out _))
            .ToDictionary(
                x => x.GetProperty("name").GetString() ?? string.Empty,
                x => x.TryGetProperty("type", out var type) ? type.GetString() ?? "string" : "string",
                StringComparer.OrdinalIgnoreCase);
    }
}
