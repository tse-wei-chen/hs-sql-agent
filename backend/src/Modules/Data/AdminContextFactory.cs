using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Modules.Data;

public class AdminContextFactory : IDesignTimeDbContextFactory<AdminContext>
{
    public AdminContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<AdminContext>();
        optionsBuilder.UseSqlite("Data Source=hsqlagent.db");

        return new AdminContext(optionsBuilder.Options);
    }
}