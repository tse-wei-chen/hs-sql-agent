using Admin.Service.Data;
using Auth.Service.Data;
using HsSqlAgent.SqliteMigrations;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Admin.Test.Data;

public class AdminMigrationTests
{
    [Fact]
    public async Task CustomToolLifecycleMigration_ShouldDisableLegacyJsonDefinitions()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<AdminContext>()
            .UseSqlite(connection, sqlite => sqlite.MigrationsAssembly(typeof(SqliteAdminContextFactory).Assembly.FullName))
            .Options;
        var authOptions = new DbContextOptionsBuilder<AuthContext>()
            .UseSqlite(connection, sqlite => sqlite.MigrationsAssembly(typeof(SqliteAuthContextFactory).Assembly.FullName))
            .Options;
        await using (var authContext = new AuthContext(authOptions))
        {
            await authContext.Database.MigrateAsync(TestContext.Current.CancellationToken);
        }
        await using var context = new AdminContext(options);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync("20260804124242_AddOperability", TestContext.Current.CancellationToken);
        await context.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO CustomSqlTools (Name, Description, DefinitionJson, Type, CreatedAt)
            VALUES ('legacy', 'legacy JSON tool', '{{"tableName":"users"}}', 'Query', '2026-01-01T00:00:00Z');
            """,
            TestContext.Current.CancellationToken);

        await migrator.MigrateAsync(cancellationToken: TestContext.Current.CancellationToken);

        var legacy = await context.CustomSqlTools.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal("Disabled", legacy.Status);
        Assert.Null(legacy.DbManagementId);
        Assert.Contains("tableName", legacy.SqlTemplate);
    }

    [Fact]
    public async Task OperabilityPermissionMigration_ShouldGrantExistingSuperUser()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<AuthContext>()
            .UseSqlite(connection, sqlite => sqlite.MigrationsAssembly(typeof(SqliteAuthContextFactory).Assembly.FullName))
            .Options;
        await using var context = new AuthContext(options);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync("20260804121039_AddEnterpriseIdentity", TestContext.Current.CancellationToken);
        context.Roles.Add(new Auth.Service.Data.Entites.Role { Name = "SuperUser" });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await migrator.MigrateAsync(cancellationToken: TestContext.Current.CancellationToken);

        var permissions = await context.PermissionActions.AsNoTracking()
            .Where(x => x.Role.Name == "SuperUser")
            .Select(x => x.Permission.Path + "." + x.Action.Code)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Contains("/runtime/audit.export", permissions);
        Assert.Contains("/runtime/audit.edit", permissions);
        Assert.Contains("/runtime/operability.view", permissions);
        Assert.Contains("/runtime/operability.edit", permissions);
    }

    [Fact]
    public async Task StructuredAuditMigration_ShouldAssignUniqueEventIdsToExistingRows()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<AdminContext>()
            .UseSqlite(connection, sqlite => sqlite.MigrationsAssembly(typeof(SqliteAdminContextFactory).Assembly.FullName))
            .Options;
        var authOptions = new DbContextOptionsBuilder<AuthContext>()
            .UseSqlite(connection, sqlite => sqlite.MigrationsAssembly(typeof(SqliteAuthContextFactory).Assembly.FullName))
            .Options;
        await using (var authContext = new AuthContext(authOptions))
        {
            await authContext.Database.MigrateAsync(TestContext.Current.CancellationToken);
        }
        await using var context = new AdminContext(options);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(
            "20260627034645_RemoveSuperUsersFromAdmin",
            TestContext.Current.CancellationToken);

        await context.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO AuditLogs
                (ActorType, Action, Target, Result, CreatedAt)
            VALUES
                ('system', 'one', 'target', 'success', '2026-01-01T00:00:00Z'),
                ('system', 'two', 'target', 'success', '2026-01-01T00:00:00Z');
            """,
            TestContext.Current.CancellationToken);

        await migrator.MigrateAsync(cancellationToken: TestContext.Current.CancellationToken);

        var eventIds = await context.AuditLogs
            .AsNoTracking()
            .Select(x => x.EventId)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, eventIds.Count);
        Assert.All(eventIds, id => Assert.NotEqual(Guid.Empty, id));
        Assert.Equal(2, eventIds.Distinct().Count());
    }
}
