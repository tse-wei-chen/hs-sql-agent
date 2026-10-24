using Admin.Service.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace HsSqlAgent.Server.Test.Data;

public sealed class SecretProtectionMigrationTests
{
    [Fact]
    public void PostgresSecretMigrationGeneratesWithMatchingModelWithoutConnecting()
    {
        var options = new DbContextOptionsBuilder<AdminContext>()
            .UseNpgsql("Host=unused;Database=unused", postgres => postgres.MigrationsAssembly(
                typeof(HsSqlAgent.PostgresMigrations.Admin.VersionedSecretProtectionPostgres).Assembly.FullName))
            .Options;
        using var context = new AdminContext(options);
        Assert.False(context.Database.HasPendingModelChanges());
        var sql = context.GetService<IMigrator>().GenerateScript(
            "20261022190000_V3ApprovalDrainGatePostgres",
            "20261024120000_VersionedSecretProtectionPostgres");
        Assert.Contains("CHECK (\"Count\" = 0)", sql);
        Assert.Contains("'Pending', 'Executing'", sql);
        Assert.Contains("IX_DbManagement_SecretBindingId", sql);
        Assert.Contains("ALTER COLUMN \"PasswordHash\" TYPE text", sql);
        Assert.Throws<NotSupportedException>(() =>
            _ = new HsSqlAgent.PostgresMigrations.Admin.VersionedSecretProtectionPostgres().DownOperations);
    }
}
