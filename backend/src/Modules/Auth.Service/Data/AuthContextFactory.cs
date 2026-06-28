using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Auth.Service.Data;

public class AuthContextFactory : IDesignTimeDbContextFactory<AuthContext>
{
    public AuthContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<AuthContext>();
        optionsBuilder.UseSqlite("Data Source=hsqlagent.db");

        return new AuthContext(optionsBuilder.Options);
    }
}
