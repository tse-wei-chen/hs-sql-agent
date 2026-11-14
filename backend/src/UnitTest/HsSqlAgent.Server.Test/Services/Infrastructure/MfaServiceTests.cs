using System.Buffers.Binary;
using System.Security.Cryptography;
using Auth.Service.Data;
using Auth.Service.Data.Entites;
using Auth.Service.Models;
using HsSqlAgent.Server.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

public class MfaServiceTests
{
    [Fact]
    public async Task Setup_AndRecoveryCode_AreOneTime()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var context = new AuthContext(new DbContextOptionsBuilder<AuthContext>().UseSqlite(connection).Options);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        var member = new Member { Mail = "admin@test.com", NormalizedMail = "ADMIN@TEST.COM", Username = "admin", PasswordHash = "hash" };
        context.Members.Add(member);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        var service = new MfaService(context, new EphemeralDataProtectionProvider(), Options.Create(new EnterpriseIdentitySettings()));

        var setup = await service.BeginSetupAsync(member.Id, TestContext.Current.CancellationToken);
        var recoveryCodes = await service.ConfirmSetupAsync(member.Id, CurrentCode(setup.Secret), TestContext.Current.CancellationToken);

        Assert.Equal(10, recoveryCodes.Count);
        Assert.True(await service.VerifyAsync(member.Id, recoveryCodes.First(), TestContext.Current.CancellationToken));
        Assert.False(await service.VerifyAsync(member.Id, recoveryCodes.First(), TestContext.Current.CancellationToken));
        var status = await service.GetStatusAsync(member.Id, TestContext.Current.CancellationToken);
        Assert.True(status.Enabled);
        Assert.Equal(9, status.RecoveryCodesRemaining);
    }

    private static string CurrentCode(string base32)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new List<byte>(); var buffer = 0; var bits = 0;
        foreach (var character in base32) { buffer = (buffer << 5) | alphabet.IndexOf(character); bits += 5; if (bits >= 8) { bytes.Add((byte)((buffer >> (bits - 8)) & 255)); bits -= 8; } }
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
        var hash = HMACSHA1.HashData([.. bytes], counter);
        var offset = hash[^1] & 15;
        var value = (((hash[offset] & 127) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3]) % 1_000_000;
        return value.ToString("D6");
    }
}
