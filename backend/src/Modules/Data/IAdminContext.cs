using Microsoft.EntityFrameworkCore;
using Modules.Data.Entites;

namespace Modules.Data;

public interface IAdminContext
{
    DbSet<SuperUser> SuperUsers { get; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}