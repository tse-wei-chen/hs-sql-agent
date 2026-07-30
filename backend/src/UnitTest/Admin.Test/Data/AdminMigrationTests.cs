using Admin.Service.Data;
using Auth.Service.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Admin.Test.Data;

public class AdminMigrationTests
{
    [Fact]
    public async Task StructuredAuditMigration_ShouldAssignUniqueEventIdsToExistingRows()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<AdminContext>()
            .UseSqlite(connection)
            .Options;
        var authOptions = new DbContextOptionsBuilder<AuthContext>()
            .UseSqlite(connection)
            .Options;
        await using (var authContext = new AuthContext(authOptions))
        {
            await authContext.Database.MigrateAsync(TestContext.Current.CancellationToken);
        }
        await using var context = new AdminContext(options);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(
            "20260730130602_AddSecurityPolicy",
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
