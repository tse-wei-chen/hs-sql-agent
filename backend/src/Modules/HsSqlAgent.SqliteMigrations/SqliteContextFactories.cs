using Admin.Service.Data;
using Auth.Service.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace HsSqlAgent.SqliteMigrations;

public sealed class SqliteAdminContextFactory : IDesignTimeDbContextFactory<AdminContext>
{
    public AdminContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AdminContext>()
            .UseSqlite(
                "Data Source=hsqlagent.db",
                sqlite =>
                {
                    sqlite.MigrationsAssembly(typeof(SqliteAdminContextFactory).Assembly.FullName);
                })
            .Options;

        return new AdminContext(options);
    }
}

public sealed class SqliteAuthContextFactory : IDesignTimeDbContextFactory<AuthContext>
{
    public AuthContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AuthContext>()
            .UseSqlite(
                "Data Source=hsqlagent.db",
                sqlite =>
                {
                    sqlite.MigrationsAssembly(typeof(SqliteAuthContextFactory).Assembly.FullName);
                })
            .Options;

        return new AuthContext(options);
    }
}
