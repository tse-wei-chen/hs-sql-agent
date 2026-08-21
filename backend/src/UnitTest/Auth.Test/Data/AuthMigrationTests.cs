using Auth.Service.Data;
using HsSqlAgent.SqliteMigrations;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Auth.Test.Data;

public class AuthMigrationTests
{
    [Fact]
    public async Task AccountSecurityMigration_ShouldUpgradeDatabaseWithExistingMember()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<AuthContext>()
            .UseSqlite(connection, sqlite =>
                sqlite.MigrationsAssembly(typeof(SqliteAuthContextFactory).Assembly.FullName))
            .Options;
        await using var context = new AuthContext(options);
        var migrator = context.GetService<IMigrator>();

        await migrator.MigrateAsync(
            "20260804113626_AddAuthSessions",
            TestContext.Current.CancellationToken);
        await context.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO Members (Username, Mail, PasswordHash)
            VALUES ('legacy-user', ' Legacy@Example.COM ', 'legacy-hash');
            """,
            TestContext.Current.CancellationToken);
        var migrationStartedAt = DateTime.UtcNow.AddSeconds(-1);

        await migrator.MigrateAsync(cancellationToken: TestContext.Current.CancellationToken);

        var member = await context.Members.AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal("LEGACY@EXAMPLE.COM", member.NormalizedMail);
        Assert.True(member.CreatedAt >= migrationStartedAt);
        Assert.NotEqual(new DateTime(1970, 1, 1), member.CreatedAt);
    }
}
