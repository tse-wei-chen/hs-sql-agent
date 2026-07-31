using Admin.Service.Data;
using Auth.Service.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace HsSqlAgent.PostgresMigrations;

public sealed class PostgresAdminContextFactory : IDesignTimeDbContextFactory<AdminContext>
{
    public AdminContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AdminContext>()
            .UseNpgsql(
                "Host=localhost;Database=hsqlagent;Username=postgres;Password=postgres",
                postgres =>
                {
                    postgres.MigrationsAssembly(typeof(PostgresAdminContextFactory).Assembly.FullName);
                    postgres.MigrationsHistoryTable("__AdminMigrationsHistory");
                })
            .Options;

        return new AdminContext(options);
    }
}

public sealed class PostgresAuthContextFactory : IDesignTimeDbContextFactory<AuthContext>
{
    public AuthContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AuthContext>()
            .UseNpgsql(
                "Host=localhost;Database=hsqlagent;Username=postgres;Password=postgres",
                postgres =>
                {
                    postgres.MigrationsAssembly(typeof(PostgresAuthContextFactory).Assembly.FullName);
                    postgres.MigrationsHistoryTable("__AuthMigrationsHistory");
                })
            .Options;

        return new AuthContext(options);
    }
}
