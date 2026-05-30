using Admin.Service.Data;
using Admin.Service.Data.Entites;
using Admin.Service.Interfaces;
using Admin.Service.Models;
using Microsoft.EntityFrameworkCore;

namespace Admin.Service.Services;

public class DbSemanticService(IAdminContext context) : IDbSemanticService
{
    private readonly IAdminContext _context = context;

    public async Task<List<DbSemanticVM>> GetSemanticsByDbIdAsync(int dbManagementId, CancellationToken cancellationToken = default)
    {
        return await _context.DbSemantics
            .Where(s => s.DbManagementId == dbManagementId)
            .Select(DbSemanticVM.Projection)
            .ToListAsync(cancellationToken);
    }

    public async Task<DbSemanticVM> UpsertSemanticAsync(DbSemanticRequest request, CancellationToken cancellationToken = default)
    {
        var entity = await _context.DbSemantics
            .FirstOrDefaultAsync(s =>
                s.DbManagementId == request.DbManagementId &&
                s.SchemaName == request.SchemaName &&
                s.TableName == request.TableName &&
                s.ColumnName == request.ColumnName,
                cancellationToken);

        if (entity == null)
        {
            entity = new DbSemantic
            {
                DbManagementId = request.DbManagementId,
                SchemaName = request.SchemaName,
                TableName = request.TableName,
                ColumnName = request.ColumnName,
                CreatedAt = DateTime.UtcNow
            };
            _context.DbSemantics.Add(entity);
        }

        entity.Description = request.Description;
        entity.DisplayName = request.DisplayName;
        entity.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);

        return DbSemanticVM.Projection.Compile().Invoke(entity);
    }

    public async Task DeleteSemanticAsync(int id, CancellationToken cancellationToken = default)
    {
        var entity = await _context.DbSemantics.FindAsync([id], cancellationToken);
        if (entity != null)
        {
            _context.DbSemantics.Remove(entity);
            await _context.SaveChangesAsync(cancellationToken);
        }
    }
}
