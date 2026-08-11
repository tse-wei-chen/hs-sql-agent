using Admin.Service.Data;
using Admin.Service.Data.Entites;
using Admin.Service.Interfaces;
using Admin.Service.Models;
using Microsoft.EntityFrameworkCore;

namespace Admin.Service.Services;

public class DbSemanticService(IAdminContext context) : IDbSemanticService
{
    private static readonly HashSet<string> Cardinalities = new(StringComparer.OrdinalIgnoreCase)
        { "one-to-one", "one-to-many", "many-to-one", "many-to-many" };
    private static readonly HashSet<string> Directions = new(StringComparer.OrdinalIgnoreCase)
        { "source-to-target", "target-to-source", "bidirectional" };
    private static readonly HashSet<string> Aggregations = new(StringComparer.OrdinalIgnoreCase)
        { "sum", "count", "count-distinct", "avg", "min", "max", "custom" };

    private readonly IAdminContext _context = context;

    public async Task<List<DbSemanticVM>> GetSemanticsByDbIdAsync(int dbManagementId, CancellationToken cancellationToken = default)
    {
        var entities = await _context.DbSemantics.AsNoTracking()
            .Where(s => s.DbManagementId == dbManagementId)
            .OrderBy(s => s.SchemaName).ThenBy(s => s.TableName).ThenBy(s => s.ColumnName)
            .ToListAsync(cancellationToken);
        return [.. entities.Select(DbSemanticVM.FromEntity)];
    }

    public async Task<DbSemanticModel> GetSemanticModelAsync(int dbManagementId, CancellationToken cancellationToken = default)
    {
        var entities = await GetSemanticsByDbIdAsync(dbManagementId, cancellationToken);
        var relationshipEntities = await _context.DbSemanticRelationships.AsNoTracking()
            .Where(x => x.DbManagementId == dbManagementId)
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);
        var relationships = relationshipEntities.Select(ToRelationshipModel).ToList();
        var metricEntities = await _context.DbSemanticMetrics.AsNoTracking()
            .Where(x => x.DbManagementId == dbManagementId)
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);
        var metrics = metricEntities.Select(ToMetricModel).ToList();
        return new DbSemanticModel(dbManagementId, entities, relationships, metrics);
    }

    public async Task<DbSemanticVM> UpsertSemanticAsync(DbSemanticRequest request, CancellationToken cancellationToken = default)
    {
        RequireName(request.TableName, nameof(request.TableName));
        var entity = await _context.DbSemantics.FirstOrDefaultAsync(s =>
            s.DbManagementId == request.DbManagementId &&
            s.SchemaName == request.SchemaName &&
            s.TableName == request.TableName &&
            s.ColumnName == request.ColumnName, cancellationToken);

        if (entity == null)
        {
            entity = new DbSemantic
            {
                DbManagementId = request.DbManagementId,
                SchemaName = NormalizeOptional(request.SchemaName),
                TableName = request.TableName.Trim(),
                ColumnName = NormalizeOptional(request.ColumnName),
                CreatedAt = DateTime.UtcNow
            };
            _context.DbSemantics.Add(entity);
        }

        entity.Description = NormalizeOptional(request.Description);
        entity.DisplayName = NormalizeOptional(request.DisplayName);
        entity.SynonymsJson = SemanticSynonymNormalizer.Serialize(request.Synonyms);
        entity.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
        return DbSemanticVM.FromEntity(entity);
    }

    public async Task<DbSemanticRelationshipModel> UpsertRelationshipAsync(
        DbSemanticRelationshipModel model,
        CancellationToken cancellationToken = default)
    {
        RequireName(model.Name, nameof(model.Name));
        RequireName(model.SourceTable, nameof(model.SourceTable));
        RequireName(model.SourceColumn, nameof(model.SourceColumn));
        RequireName(model.TargetTable, nameof(model.TargetTable));
        RequireName(model.TargetColumn, nameof(model.TargetColumn));
        if (!Cardinalities.Contains(model.Cardinality))
            throw new ArgumentException($"Unsupported cardinality '{model.Cardinality}'.");
        if (!Directions.Contains(model.Direction))
            throw new ArgumentException($"Unsupported relationship direction '{model.Direction}'.");

        var entity = model.Id > 0
            ? await _context.DbSemanticRelationships.FirstOrDefaultAsync(
                x => x.Id == model.Id && x.DbManagementId == model.DbManagementId, cancellationToken)
            : await _context.DbSemanticRelationships.FirstOrDefaultAsync(
                x => x.DbManagementId == model.DbManagementId && x.Name == model.Name, cancellationToken);
        if (model.Id > 0 && entity == null)
            throw new KeyNotFoundException($"Relationship {model.Id} was not found for database {model.DbManagementId}.");
        if (entity == null)
        {
            entity = new DbSemanticRelationship { DbManagementId = model.DbManagementId, CreatedAt = DateTime.UtcNow };
            _context.DbSemanticRelationships.Add(entity);
        }

        entity.Name = model.Name.Trim();
        entity.SourceSchema = NormalizeOptional(model.SourceSchema);
        entity.SourceTable = model.SourceTable.Trim();
        entity.SourceColumn = model.SourceColumn.Trim();
        entity.TargetSchema = NormalizeOptional(model.TargetSchema);
        entity.TargetTable = model.TargetTable.Trim();
        entity.TargetColumn = model.TargetColumn.Trim();
        entity.Cardinality = model.Cardinality.ToLowerInvariant();
        entity.Direction = model.Direction.ToLowerInvariant();
        entity.Description = NormalizeOptional(model.Description);
        entity.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
        return ToRelationshipModel(entity);
    }

    public async Task<DbSemanticMetricModel> UpsertMetricAsync(
        DbSemanticMetricModel model,
        CancellationToken cancellationToken = default)
    {
        RequireName(model.Name, nameof(model.Name));
        RequireName(model.TableName, nameof(model.TableName));
        RequireName(model.Formula, nameof(model.Formula));
        if (!Aggregations.Contains(model.Aggregation))
            throw new ArgumentException($"Unsupported metric aggregation '{model.Aggregation}'.");

        var entity = model.Id > 0
            ? await _context.DbSemanticMetrics.FirstOrDefaultAsync(
                x => x.Id == model.Id && x.DbManagementId == model.DbManagementId, cancellationToken)
            : await _context.DbSemanticMetrics.FirstOrDefaultAsync(
                x => x.DbManagementId == model.DbManagementId && x.SchemaName == model.SchemaName &&
                     x.TableName == model.TableName && x.Name == model.Name, cancellationToken);
        if (model.Id > 0 && entity == null)
            throw new KeyNotFoundException($"Metric {model.Id} was not found for database {model.DbManagementId}.");
        if (entity == null)
        {
            entity = new DbSemanticMetric { DbManagementId = model.DbManagementId, CreatedAt = DateTime.UtcNow };
            _context.DbSemanticMetrics.Add(entity);
        }

        entity.Name = model.Name.Trim();
        entity.SchemaName = NormalizeOptional(model.SchemaName);
        entity.TableName = model.TableName.Trim();
        entity.DisplayName = NormalizeOptional(model.DisplayName);
        entity.Description = NormalizeOptional(model.Description);
        entity.Formula = model.Formula.Trim();
        entity.Aggregation = model.Aggregation.ToLowerInvariant();
        entity.Grain = NormalizeOptional(model.Grain);
        entity.Filter = NormalizeOptional(model.Filter);
        entity.SynonymsJson = SemanticSynonymNormalizer.Serialize(model.Synonyms);
        entity.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
        return ToMetricModel(entity);
    }

    public async Task DeleteSemanticAsync(int id, CancellationToken cancellationToken = default)
        => await DeleteAsync(_context.DbSemantics, id, cancellationToken);

    public async Task DeleteRelationshipAsync(int id, CancellationToken cancellationToken = default)
        => await DeleteAsync(_context.DbSemanticRelationships, id, cancellationToken);

    public async Task DeleteMetricAsync(int id, CancellationToken cancellationToken = default)
        => await DeleteAsync(_context.DbSemanticMetrics, id, cancellationToken);

    private async Task DeleteAsync<TEntity>(DbSet<TEntity> set, int id, CancellationToken cancellationToken)
        where TEntity : class
    {
        var entity = await set.FindAsync([id], cancellationToken);
        if (entity == null) return;
        set.Remove(entity);
        await _context.SaveChangesAsync(cancellationToken);
    }

    private static DbSemanticRelationshipModel ToRelationshipModel(DbSemanticRelationship x) => new()
    {
        Id = x.Id, DbManagementId = x.DbManagementId, Name = x.Name,
        SourceSchema = x.SourceSchema, SourceTable = x.SourceTable, SourceColumn = x.SourceColumn,
        TargetSchema = x.TargetSchema, TargetTable = x.TargetTable, TargetColumn = x.TargetColumn,
        Cardinality = x.Cardinality, Direction = x.Direction, Description = x.Description
    };

    private static DbSemanticMetricModel ToMetricModel(DbSemanticMetric x) => new()
    {
        Id = x.Id, DbManagementId = x.DbManagementId, Name = x.Name,
        SchemaName = x.SchemaName, TableName = x.TableName,
        DisplayName = x.DisplayName, Description = x.Description, Formula = x.Formula,
        Aggregation = x.Aggregation, Grain = x.Grain, Filter = x.Filter,
        Synonyms = SemanticSynonymNormalizer.Deserialize(x.SynonymsJson)
    };

    private static void RequireName(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{name} is required.");
    }

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
