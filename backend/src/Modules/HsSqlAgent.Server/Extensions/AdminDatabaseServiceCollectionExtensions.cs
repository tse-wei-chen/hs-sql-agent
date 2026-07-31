using Admin.Service.Data;
using Auth.Service.Data;
using Microsoft.EntityFrameworkCore;

namespace HsSqlAgent.Server.Extensions;

internal static class AdminDatabaseServiceCollectionExtensions
{
    public static IServiceCollection AddAdminDatabase(
        this IServiceCollection services,
        string provider,
        string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("Admin database connection string is required.");

        if (!string.Equals(provider, "Sqlite", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Unsupported admin database provider '{provider}'. Supported providers: Sqlite.");

        services.AddDbContext<AdminContext>(db => db.UseSqlite(connectionString));
        services.AddScoped<IAdminContext>(sp => sp.GetRequiredService<AdminContext>());

        services.AddDbContext<AuthContext>(db => db.UseSqlite(connectionString));
        services.AddScoped<IAuthContext>(sp => sp.GetRequiredService<AuthContext>());

        return services;
    }
}
