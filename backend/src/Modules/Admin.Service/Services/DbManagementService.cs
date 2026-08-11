using Admin.Service.Data.Entites;
using Admin.Service.Data;
using Admin.Service.Models;
using Common.Interfaces;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using System.Text;
using Admin.Service.Interfaces;
namespace Admin.Service.Services;

public class DbManagementService(IAdminContext context, ICryptoService cryptoService, IOptions<McpKeySettings> mcpKeySettings) : IDbManagementService
{
    private readonly IAdminContext _context = context;
    private readonly ICryptoService _cryptoService = cryptoService;
    private readonly byte[] _hmacSecret = Encoding.UTF8.GetBytes(mcpKeySettings.Value.HmacSecretKey);

    public async Task<DbManagementVM> CreateDbAsync(DbManagementRequest dbManagement, CancellationToken cancellationToken = default)
    {
        if (DbManagementPasswordPolicy.RequiresPassword(dbManagement.SqlProvider)
            && string.IsNullOrWhiteSpace(dbManagement.Password))
        {
            throw new ArgumentException("Password is required for this SQL provider.", nameof(dbManagement));
        }

        var entity = new DbManagement
        {
            Name = dbManagement.Name,
            SqlProvider = dbManagement.SqlProvider,
            Host = dbManagement.Host,
            Port = dbManagement.Port,
            Username = dbManagement.Username,
            PasswordHash = _cryptoService.EncryptText(dbManagement.Password, _hmacSecret),
            Database = dbManagement.Database,
            ExtraSettings = dbManagement.ExtraSettings,
            CreatedBy = dbManagement.CreatedBy,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            UpdatedBy = dbManagement.UpdatedBy
        };

        _context.DbManagement.Add(entity);
        await _context.SaveChangesAsync(cancellationToken);
        return DbManagementVM.Projection.Compile().Invoke(entity);
    }

    public async Task<DbManagementBase?> GetDbByIdAsync(int id, bool isPwd, CancellationToken cancellationToken = default)
    {
        var query = _context.DbManagement.Where(db => db.Id == id);

        if (isPwd)
        {
            return await query
                .Select(DbManagementPwdVM.Projection)
                .FirstOrDefaultAsync(cancellationToken);
        }
        else
        {
            return await query
                .Select(DbManagementVM.Projection)
                .FirstOrDefaultAsync(cancellationToken);
        }
    }

    public async Task<List<DbManagementVM>> GetAllDbsAsync(CancellationToken cancellationToken = default)
    {
        return await _context.DbManagement
            .Select(DbManagementVM.Projection)
            .ToListAsync(cancellationToken: cancellationToken);
    }

    public async Task UpdateDbAsync(int id, DbManagementRequest dbManagement, CancellationToken cancellationToken = default)
    {
        var existingDbManagement = await _context.DbManagement.FirstOrDefaultAsync(db => db.Id == id, cancellationToken);
        if (existingDbManagement != null)
        {
            existingDbManagement.Name = dbManagement.Name;
            existingDbManagement.SqlProvider = dbManagement.SqlProvider;
            existingDbManagement.Host = dbManagement.Host;
            existingDbManagement.Port = dbManagement.Port;
            existingDbManagement.Username = dbManagement.Username;
            if (!string.IsNullOrWhiteSpace(dbManagement.Password))
            {
                existingDbManagement.PasswordHash = _cryptoService.EncryptText(dbManagement.Password, _hmacSecret);
            }
            existingDbManagement.Database = dbManagement.Database;
            existingDbManagement.ExtraSettings = dbManagement.ExtraSettings;
            existingDbManagement.UpdatedAt = DateTime.UtcNow;
            existingDbManagement.UpdatedBy = dbManagement.UpdatedBy;

            await _context.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task DeleteDbAsync(int id, CancellationToken cancellationToken = default)
    {
        var existingDbManagement = await _context.DbManagement.FirstOrDefaultAsync(db => db.Id == id, cancellationToken);
        if (existingDbManagement != null)
        {
            var now = DateTime.UtcNow;
            var hasUsableKeys = await _context.McpAccessKeys.AnyAsync(
                key => key.DbManagementId == id &&
                       key.IsActive &&
                       (!key.ExpiresAt.HasValue || key.ExpiresAt > now),
                cancellationToken);

            if (hasUsableKeys)
            {
                throw new InvalidOperationException(
                    "This database connection is still used by an active MCP access key. Revoke or let the key expire before deleting the connection.");
            }

            var dependentDraftTools = await _context.CustomSqlTools.CountAsync(
                tool => tool.DbManagementId == id,
                cancellationToken);
            var dependentRevisions = await _context.CustomSqlToolRevisions.CountAsync(
                revision => revision.DbManagementId == id,
                cancellationToken);
            if (dependentDraftTools > 0 || dependentRevisions > 0)
            {
                throw new InvalidOperationException(
                    $"This database connection is referenced by {dependentDraftTools} custom tool(s) and " +
                    $"{dependentRevisions} published revision(s). Delete or rebind those custom tools before deleting the connection.");
            }

            _context.DbManagement.Remove(existingDbManagement);
            await _context.SaveChangesAsync(cancellationToken);
        }
    }

}
