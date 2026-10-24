using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Admin.Service.Data;
using Admin.Service.Models;
using Admin.Service.Services;
using Common.Interfaces;
using Common.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Admin.Test.Services;

public sealed class VersionedDatabasePasswordWriteTests
{
    private const string Hmac = "migration-test-legacy-hmac-secret-at-least-32-bytes";

    private static ISecretProtector CreateProtector() =>
        new VersionedSecretProtector(new SecretKeyRing(
            new Dictionary<SecretPurpose, SecretKeySet>
            {
                [SecretPurpose.DatabasePassword] = new SecretKeySet
                {
                    ActiveKeyId = "db_k1",
                    Keys = new Dictionary<string, byte[]>
                    {
                        ["db_k1"] = Enumerable.Repeat((byte)0x81, 32).ToArray()
                    }
                }
            }), Encoding.UTF8.GetBytes(Hmac));

    [Fact]
    public async Task NewPasswordIsNeverCommittedInLegacyFormatWhenV2Enabled()
    {
        await using var sqlite = new SqliteConnection("Data Source=:memory:");
        await sqlite.OpenAsync(TestContext.Current.CancellationToken);
        var opts = new DbContextOptionsBuilder<AdminContext>().UseSqlite(sqlite).Options;
        await using var context = new AdminContext(opts);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        var crypto = new CryptoService();
        var protector = CreateProtector();
        var service = new DbManagementService(context, crypto,
            Options.Create(new McpKeySettings { HmacSecretKey = Hmac }),
            new Mock<ICacheService>().Object, protector, new WritePolicy());

        var created = await service.CreateDbAsync(new DbManagementRequest
        {
            Name = "primary",
            SqlProvider = "Postgres",
            Password = "new-password",
            Database = "app"
        }, TestContext.Current.CancellationToken);

        context.ChangeTracker.Clear();
        var record = await context.DbManagement.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(created.Id, record.Id);
        Assert.StartsWith("v2:db_k1:", record.PasswordHash);
        Assert.Equal("new-password", DatabasePasswordCiphertextReader.Decrypt(
            record.PasswordHash, record.Id, crypto, Encoding.UTF8.GetBytes(Hmac), protector));
        Assert.ThrowsAny<CryptographicException>(() =>
            DatabasePasswordCiphertextReader.Decrypt(
                record.PasswordHash, record.Id, crypto, Encoding.UTF8.GetBytes(Hmac)));

        await service.UpdateDbAsync(record.Id, new DbManagementRequest
        {
            Name = "primary",
            SqlProvider = "Postgres",
            Password = "rotated-password",
            Database = "app"
        }, TestContext.Current.CancellationToken);
        context.ChangeTracker.Clear();
        record = await context.DbManagement.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal("rotated-password", DatabasePasswordCiphertextReader.Decrypt(
            record.PasswordHash, record.Id, crypto, Encoding.UTF8.GetBytes(Hmac), protector));
    }

    [Fact]
    public async Task FailureToEncryptAfterIdAllocationRollsBackEntireCreation()
    {
        await using var sqlite = new SqliteConnection("Data Source=:memory:");
        await sqlite.OpenAsync(TestContext.Current.CancellationToken);
        var opts = new DbContextOptionsBuilder<AdminContext>().UseSqlite(sqlite).Options;
        await using var context = new AdminContext(opts);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        var service = new DbManagementService(context, new CryptoService(),
            Options.Create(new McpKeySettings { HmacSecretKey = Hmac }),
            new Mock<ICacheService>().Object, new ThrowingProtector(), new WritePolicy());

        await Assert.ThrowsAsync<CryptographicException>(() =>
            service.CreateDbAsync(new DbManagementRequest
            {
                Name = "rollback",
                SqlProvider = "Postgres",
                Password = "password",
                Database = "app"
            }, TestContext.Current.CancellationToken));

        context.ChangeTracker.Clear();
        Assert.Empty(await context.DbManagement.ToListAsync(TestContext.Current.CancellationToken));
    }

    private sealed class WritePolicy : ISecretWriteRolloutPolicy
    {
        public bool EnableV2DatabasePasswordWrites => true;
        public bool EnableV2DurableDmlWrites => false;
    }

    private sealed class ThrowingProtector : ISecretProtector
    {
        public string? Protect(string? plaintext, SecretPurpose purpose, string recordId)
            => throw new CryptographicException("Injected encryption failure.");
        public string? Unprotect(string? ciphertext, SecretPurpose purpose, string recordId)
            => throw new NotSupportedException();
        public string? Reprotect(string? ciphertext, SecretPurpose purpose, string recordId)
            => throw new NotSupportedException();
    }
}
