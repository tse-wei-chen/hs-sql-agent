using System.Security.Cryptography;
using Admin.Service.Data;
using Admin.Service.Data.Entites;
using Admin.Service.Models;
using Common.Interfaces;
using Common.Services;
using HsSqlAgent.Server.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

public sealed class DatabaseSecretMigrationServiceTests
{
    [Fact]
    public async Task InventoryIsReadOnlyAndRetirementNeedsZeroReferences()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<AdminContext>().UseSqlite(conn).Options;
        await using var context = new AdminContext(options);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        var ring = new SecretKeyRing(new Dictionary<SecretPurpose, SecretKeySet>
        {
            [SecretPurpose.DatabasePassword] = new SecretKeySet
            {
                ActiveKeyId = "db_new",
                Keys = new Dictionary<string, byte[]>
                {
                    ["db_new"] = Enumerable.Repeat((byte)0x42,32).ToArray(),
                    ["db_old"] = Enumerable.Repeat((byte)0x43,32).ToArray()
                }
            }
        });
        var protector = new VersionedSecretProtector(ring);
        context.DbManagement.Add(new DbManagement
        {
            Name = "test", PasswordHash = protector.Protect("password", SecretPurpose.DatabasePassword, "1"),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        var service = new DatabaseSecretMigrationService(context, new CryptoService(),
            Options.Create(new McpKeySettings { HmacSecretKey = "unused-in-v3" }),
            protector, ring);
        var inventory = await service.InspectAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, inventory.LegacyDatabasePasswords);
        Assert.Single(inventory.DatabaseKeyUsage);
        Assert.Equal("db_new", inventory.DatabaseKeyUsage[0].KeyId);
        var active = await service.ReviewKeyRetirementAsync(
            SecretPurpose.DatabasePassword, "db_new", TestContext.Current.CancellationToken);
        Assert.False(active.EligibleForOfflineReview);
        var old = await service.ReviewKeyRetirementAsync(
            SecretPurpose.DatabasePassword, "db_old", TestContext.Current.CancellationToken);
        Assert.True(old.EligibleForOfflineReview);
        Assert.Empty(await context.AuditLogs.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Null(typeof(DatabaseSecretMigrationService).GetMethod("MigrateBatchAsync"));
    }
}
