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

        var entity = new DbManagement
        {
            Name = dbManagement.Name,
            SqlProvider = dbManagement.SqlProvider,
            Host = dbManagement.Host,
            Port = dbManagement.Port,
            Username = dbManagement.Username,
            PasswordHash = _cryptoService.EncryptText(dbManagement.Password, _hmacSecret),
            Database = dbManagement.Database,
            CreatedBy = dbManagement.CreatedBy,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            UpdatedBy = dbManagement.UpdatedBy
        };

        _context.DbManagement.Add(entity);
        await _context.SaveChangesAsync(cancellationToken);
        return DbManagementVM.Projection.Compile().Invoke(entity);
    }

    public async Task<DbManagementVM?> GetDbByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        return await _context.DbManagement
            .Where(db => db.Id == id)
            .Select(DbManagementVM.Projection)
            .FirstOrDefaultAsync(cancellationToken);
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
            existingDbManagement.PasswordHash = _cryptoService.EncryptText(dbManagement.Password, _hmacSecret);
            existingDbManagement.Database = dbManagement.Database;
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
            _context.DbManagement.Remove(existingDbManagement);
            await _context.SaveChangesAsync(cancellationToken);
        }
    }

}