using System.Security.Cryptography;
using System.Text;
using Auth.Service.Data;
using Auth.Service.Data.Entites;
using Auth.Service.Models;
using Auth.Service.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Auth.Test.Services;

public class PasswordResetServiceTests
{
    [Fact]
    public async Task ResetAsync_UsesTokenOnlyOnce_AndRevokesSessions()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<AuthContext>().UseSqlite(connection).Options;
        await using var context = new AuthContext(options);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        const string rawToken = "one-time-reset-token";
        var member = new Member
        {
            Mail = "user@test.com",
            NormalizedMail = "USER@TEST.COM",
            Username = "user",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("old-password")
        };
        context.Members.Add(member);
        context.PasswordResetTokens.Add(new PasswordResetToken
        {
            Member = member,
            TokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken))),
            ExpiresAt = DateTime.UtcNow.AddMinutes(5)
        });
        var session = new AuthSession
        {
            Member = member,
            CurrentRefreshTokenHash = new string('A', 64),
            ExpiresAt = DateTime.UtcNow.AddDays(1)
        };
        context.AuthSessions.Add(session);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var service = new PasswordResetService(context, Options.Create(new PasswordResetSettings()));
        var request = new ResetPasswordRequest { Token = rawToken, NewPassword = "new-password" };
        await service.ResetAsync(request, TestContext.Current.CancellationToken);

        Assert.True(BCrypt.Net.BCrypt.Verify("new-password", member.PasswordHash));
        Assert.NotNull(session.RevokedAt);
        Assert.Equal(2, member.SecurityVersion);
        await Assert.ThrowsAsync<ArgumentException>(() => service.ResetAsync(request, TestContext.Current.CancellationToken));
    }
}
