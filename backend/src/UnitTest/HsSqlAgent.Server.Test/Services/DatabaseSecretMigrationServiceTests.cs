using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
    private const string Hmac = "historical-migration-legacy-hmac-secret-at-least-32-bytes";
    private static byte[] OldSecret => Encoding.UTF8.GetBytes(Hmac);

    private static (ISecretProtector Protector, ISecretKeyRing Ring) CreateKeys()
    {
        var ring = new SecretKeyRing(new Dictionary<SecretPurpose, SecretKeySet>
        {
            [SecretPurpose.DatabasePassword] = new SecretKeySet
            {
                ActiveKeyId = "db_new_1",
                Keys = new Dictionary<string, byte[]>
                {
                    ["db_new_1"] = Enumerable.Repeat((byte)0x52, 32).ToArray(),
                    ["db_old_1"] = Enumerable.Repeat((byte)0x53, 32).ToArray()
                }
            }
        });
        return (new VersionedSecretProtector(ring, OldSecret), ring);
    }

    private sealed class Policy : ISecretWriteRolloutPolicy
    {
        public bool EnableV2DatabasePasswordWrites => true;
        public bool EnableV2DurableDmlWrites => false;
    }

    [Fact]
    public async Task ResumableBatchesVerifyAndPreserveDatabaseIdentities()
    {
        await using var dbConnection = new SqliteConnection("Data Source=:memory:");
        await dbConnection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<AdminContext>().UseSqlite(dbConnection).Options;
        await using var context = new AdminContext(options);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        var crypto = new CryptoService();
        context.DbManagement.AddRange(
            new DbManagement { Name = "one", PasswordHash = crypto.EncryptText("old-one", OldSecret), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
            new DbManagement { Name = "two", PasswordHash = crypto.EncryptText("old-two", OldSecret), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
            new DbManagement { Name = "empty", PasswordHash = null, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        var (protector, ring) = CreateKeys();
        var service = new DatabaseSecretMigrationService(context, crypto,
            Options.Create(new McpKeySettings { HmacSecretKey = Hmac }), protector, ring, new Policy());

        var before = await service.InspectAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, before.LegacyDatabasePasswords);
        Assert.Equal(1, before.EmptyDatabasePasswords);
        var first = await service.MigrateBatchAsync(0, 1, TestContext.Current.CancellationToken);
        Assert.Equal(1, first.Migrated);
        Assert.Equal(1, first.Scanned);
        var after = await service.MigrateBatchAsync(first.NextAfterId, 100, TestContext.Current.CancellationToken);
        Assert.Equal(1, after.Migrated);
        Assert.Equal(1, after.AlreadyCurrent);

        context.ChangeTracker.Clear();
        var rows = await context.DbManagement.AsNoTracking().OrderBy(x => x.Id).ToListAsync(TestContext.Current.CancellationToken);
        Assert.StartsWith("v2:db_new_1:", rows[0].PasswordHash);
        Assert.StartsWith("v2:db_new_1:", rows[1].PasswordHash);
        Assert.Equal("old-one", protector.Unprotect(rows[0].PasswordHash, SecretPurpose.DatabasePassword,
            rows[0].Id.ToString(CultureInfo.InvariantCulture)));
        Assert.Equal("old-two", protector.Unprotect(rows[1].PasswordHash, SecretPurpose.DatabasePassword,
            rows[1].Id.ToString(CultureInfo.InvariantCulture)));
        Assert.ThrowsAny<CryptographicException>(() =>
            protector.Unprotect(rows[0].PasswordHash, SecretPurpose.DatabasePassword,
                rows[1].Id.ToString(CultureInfo.InvariantCulture)));
        var inventory = await service.InspectAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, inventory.LegacyDatabasePasswords);
        Assert.Single(inventory.DatabaseKeyUsage);
        Assert.Equal(2, inventory.DatabaseKeyUsage[0].Records);
        Assert.Equal(2, await context.AuditLogs.CountAsync(TestContext.Current.CancellationToken));
        var currentReview = await service.ReviewKeyRetirementAsync(
            SecretPurpose.DatabasePassword, "db_new_1", TestContext.Current.CancellationToken);
        Assert.False(currentReview.EligibleForOfflineReview);
        Assert.Equal(2, currentReview.CurrentReferences);
        var oldReview = await service.ReviewKeyRetirementAsync(
            SecretPurpose.DatabasePassword, "db_old_1", TestContext.Current.CancellationToken);
        Assert.True(oldReview.EligibleForOfflineReview);
        Assert.Equal(0, oldReview.CurrentReferences);
        Assert.Contains("backups", oldReview.RequiredOperatorVerification, StringComparison.OrdinalIgnoreCase);

        var again = await service.MigrateBatchAsync(0, 100, TestContext.Current.CancellationToken);
        Assert.Equal(0, again.Migrated);
        Assert.Equal(3, again.AlreadyCurrent); // Two current rows and one empty row.
        Assert.Equal(2, await context.AuditLogs.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ActiveDurableApprovalBlocksMigrationWithoutMutatingEncryptedRows()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<AdminContext>().UseSqlite(conn).Options;
        await using var context = new AdminContext(options);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        var crypto = new CryptoService();
        var original = crypto.EncryptText("password", OldSecret);
        context.DbManagement.Add(new DbManagement
        {
            Name = "primary", PasswordHash = original, CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        context.DmlApprovalRequests.Add(new DmlApprovalRequestState
        {
            RequestId = "pending-1", Status = "Pending",
            ApprovalFingerprint = new string('a',64), EvidenceFingerprint = new string('b',64),
            ProtectedExecutionPayload = "encrypted-placeholder",
            RequesterIdentity = "requester", TargetIdentity = "target",
            DatabaseProvider = "Postgres", DatabaseIdentity = "target",
            RequiredToolName = "execute_dml_sql",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(1)
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var (protector, ring) = CreateKeys();
        var service = new DatabaseSecretMigrationService(context, crypto,
            Options.Create(new McpKeySettings { HmacSecretKey = Hmac }), protector, ring, new Policy());
        var inventory = await service.InspectAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, inventory.PendingOrExecutingApprovals);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.MigrateBatchAsync(0, 50, TestContext.Current.CancellationToken));
        context.ChangeTracker.Clear();
        Assert.Equal(original, (await context.DbManagement.SingleAsync(TestContext.Current.CancellationToken)).PasswordHash);
        Assert.Empty(await context.AuditLogs.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PostWriteVerificationFailureRollsBackCiphertextAndAuditTogether()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<AdminContext>().UseSqlite(conn).Options;
        await using var context = new AdminContext(options);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        var crypto = new CryptoService();
        var old = crypto.EncryptText("critical-password", OldSecret);
        context.DbManagement.Add(new DbManagement
        {
            Name = "protected", PasswordHash = old,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        var (normalProtector, ring) = CreateKeys();
        var intentionallyBrokenReader = new VerificationFailureProtector(normalProtector);
        var service = new DatabaseSecretMigrationService(context, crypto,
            Options.Create(new McpKeySettings { HmacSecretKey = Hmac }),
            intentionallyBrokenReader, ring, new Policy());
        await Assert.ThrowsAsync<CryptographicException>(() =>
            service.MigrateBatchAsync(0, 25, TestContext.Current.CancellationToken));

        context.ChangeTracker.Clear();
        Assert.Equal(old, (await context.DbManagement.SingleAsync(TestContext.Current.CancellationToken)).PasswordHash);
        Assert.Empty(await context.AuditLogs.ToListAsync(TestContext.Current.CancellationToken));
    }

    private sealed class VerificationFailureProtector(ISecretProtector inner) : ISecretProtector
    {
        public string? Protect(string? plaintext, SecretPurpose purpose, string recordId)
            => inner.Protect(plaintext, purpose, recordId);
        public string? Unprotect(string? ciphertext, SecretPurpose purpose, string recordId)
            => "mismatched-plaintext";
        public string? Reprotect(string? ciphertext, SecretPurpose purpose, string recordId)
            => throw new NotSupportedException();
    }

    [Fact]
    public async Task MigrationWithWrongLegacyKeyFailsWithoutAnyRowChanges()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<AdminContext>().UseSqlite(conn).Options;
        await using var context = new AdminContext(options);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        var crypto = new CryptoService();
        var original = crypto.EncryptText("protected", OldSecret);
        context.DbManagement.Add(new DbManagement
        {
            Name = "protected", PasswordHash = original,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        var (protector, ring) = CreateKeys();
        var service = new DatabaseSecretMigrationService(context, crypto,
            Options.Create(new McpKeySettings { HmacSecretKey = "wrong-key" }),
            protector, ring, new Policy());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.MigrateBatchAsync(0, 25, TestContext.Current.CancellationToken));
        context.ChangeTracker.Clear();
        Assert.Equal(original, (await context.DbManagement.SingleAsync(TestContext.Current.CancellationToken)).PasswordHash);
    }
}
