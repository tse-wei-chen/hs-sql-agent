using Admin.Service.Data;
using Auth.Service.Data;
using HsSqlAgent.PostgresMigrations;
using HsSqlAgent.SqliteMigrations;
using Microsoft.EntityFrameworkCore;

namespace HsSqlAgent.Server.Extensions;

internal static class AdminDatabaseServiceCollectionExtensions
{
    public static IServiceCollection AddAdminDatabase(
        this IServiceCollection services,
        string provider,
        string connectionString)
    {
        Validate(provider, connectionString);
        services.AddDbContext<AdminContext>(db => ConfigureAdminContext(db, provider, connectionString));
        services.AddScoped<IAdminContext>(sp => sp.GetRequiredService<AdminContext>());
        return services;
    }

    public static IServiceCollection AddAuthDatabase(
        this IServiceCollection services,
        string provider,
        string connectionString)
    {
        Validate(provider, connectionString);
        services.AddDbContext<AuthContext>(db => ConfigureAuthContext(db, provider, connectionString));
        services.AddScoped<IAuthContext>(sp => sp.GetRequiredService<AuthContext>());
        return services;
    }

    private static void Validate(string provider, string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("Admin database connection string is required.");
        if (!IsSqlite(provider) && !IsPostgres(provider))
            ThrowUnsupportedProvider(provider);
    }

    private static void ConfigureAdminContext(
        DbContextOptionsBuilder options,
        string provider,
        string connectionString)
    {
        if (IsSqlite(provider))
        {
            options.UseSqlite(connectionString, sqlite =>
                sqlite.MigrationsAssembly(typeof(SqliteAdminContextFactory).Assembly.FullName));
            return;
        }

        if (IsPostgres(provider))
        {
            options.UseNpgsql(connectionString, postgres =>
            {
                postgres.MigrationsAssembly(typeof(PostgresAdminContextFactory).Assembly.FullName);
                postgres.MigrationsHistoryTable("__AdminMigrationsHistory");
            });
            return;
        }

        ThrowUnsupportedProvider(provider);
    }

    private static void ConfigureAuthContext(
        DbContextOptionsBuilder options,
        string provider,
        string connectionString)
    {
        if (IsSqlite(provider))
        {
            options.UseSqlite(connectionString, sqlite =>
                sqlite.MigrationsAssembly(typeof(SqliteAuthContextFactory).Assembly.FullName));
            return;
        }

        if (IsPostgres(provider))
        {
            options.UseNpgsql(connectionString, postgres =>
            {
                postgres.MigrationsAssembly(typeof(PostgresAuthContextFactory).Assembly.FullName);
                postgres.MigrationsHistoryTable("__AuthMigrationsHistory");
            });
            return;
        }

        ThrowUnsupportedProvider(provider);
    }

    private static void ThrowUnsupportedProvider(string provider) =>
        throw new InvalidOperationException(
            $"Unsupported admin database provider '{provider}'. Supported providers: Sqlite, Postgres.");

    private static bool IsSqlite(string provider) =>
        string.Equals(provider, "Sqlite", StringComparison.OrdinalIgnoreCase);

    private static bool IsPostgres(string provider) =>
        string.Equals(provider, "Postgres", StringComparison.OrdinalIgnoreCase);
}
