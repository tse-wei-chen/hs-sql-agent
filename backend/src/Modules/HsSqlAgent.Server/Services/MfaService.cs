using System.Security.Cryptography;
using System.Text;
using Auth.Service.Data;
using Auth.Service.Data.Entites;
using Auth.Service.Interfaces;
using Auth.Service.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HsSqlAgent.Server.Services;

public class MfaService(
    IAuthContext context,
    IDataProtectionProvider dataProtectionProvider,
    IOptions<EnterpriseIdentitySettings> settings) : IMfaService
{
    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector("HsSqlAgent.MfaSecret.v1");

    public async Task<MfaSetupVM> BeginSetupAsync(int memberId, CancellationToken cancellationToken = default)
    {
        var member = await context.Members.FirstOrDefaultAsync(x => x.Id == memberId, cancellationToken)
            ?? throw new InvalidOperationException("Member not found.");
        if (member.MfaEnabled) throw new InvalidOperationException("MFA is already enabled.");
        var secret = RandomNumberGenerator.GetBytes(20);
        var encoded = Base32Encode(secret);
        member.MfaSecretProtected = _protector.Protect(encoded);
        await context.SaveChangesAsync(cancellationToken);
        var issuer = settings.Value.TotpIssuer;
        return new MfaSetupVM
        {
            Secret = encoded,
            OtpAuthUri = $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(member.Mail)}?secret={encoded}&issuer={Uri.EscapeDataString(issuer)}&digits=6&period=30"
        };
    }

    public async Task<IReadOnlyCollection<string>> ConfirmSetupAsync(int memberId, string code, CancellationToken cancellationToken = default)
    {
        var member = await context.Members.FirstOrDefaultAsync(x => x.Id == memberId, cancellationToken)
            ?? throw new InvalidOperationException("Member not found.");
        if (string.IsNullOrWhiteSpace(member.MfaSecretProtected) || !VerifyTotp(member.MfaSecretProtected, code))
            throw new ArgumentException("Invalid authenticator code.");

        var existing = await context.MfaRecoveryCodes.Where(x => x.MemberId == memberId).ToListAsync(cancellationToken);
        context.MfaRecoveryCodes.RemoveRange(existing);
        var rawCodes = Enumerable.Range(0, 10).Select(_ => CreateRecoveryCode()).ToArray();
        foreach (var raw in rawCodes)
            context.MfaRecoveryCodes.Add(new MfaRecoveryCode { MemberId = memberId, CodeHash = HashRecoveryCode(raw) });
        member.MfaEnabled = true;
        member.SecurityVersion++;
        await context.SaveChangesAsync(cancellationToken);
        return rawCodes;
    }

    public async Task<bool> VerifyAsync(int memberId, string code, CancellationToken cancellationToken = default)
    {
        var member = await context.Members.FirstOrDefaultAsync(x => x.Id == memberId, cancellationToken);
        if (member is null || !member.MfaEnabled || string.IsNullOrWhiteSpace(member.MfaSecretProtected)) return false;
        if (VerifyTotp(member.MfaSecretProtected, code)) return true;

        var hash = HashRecoveryCode(code);
        var recovery = await context.MfaRecoveryCodes
            .FirstOrDefaultAsync(x => x.MemberId == memberId && x.CodeHash == hash && x.UsedAt == null, cancellationToken);
        if (recovery is null) return false;
        recovery.UsedAt = DateTime.UtcNow;
        try { await context.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { return false; }
        return true;
    }

    public async Task DisableAsync(int memberId, string code, CancellationToken cancellationToken = default)
    {
        if (!await VerifyAsync(memberId, code, cancellationToken)) throw new ArgumentException("Invalid authenticator or recovery code.");
        var member = await context.Members.FirstAsync(x => x.Id == memberId, cancellationToken);
        member.MfaEnabled = false;
        member.MfaSecretProtected = null;
        member.SecurityVersion++;
        var recovery = await context.MfaRecoveryCodes.Where(x => x.MemberId == memberId).ToListAsync(cancellationToken);
        context.MfaRecoveryCodes.RemoveRange(recovery);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<MfaStatusVM> GetStatusAsync(int memberId, CancellationToken cancellationToken = default)
        => new()
        {
            Enabled = await context.Members.AsNoTracking().Where(x => x.Id == memberId).Select(x => x.MfaEnabled).FirstAsync(cancellationToken),
            RecoveryCodesRemaining = await context.MfaRecoveryCodes.CountAsync(x => x.MemberId == memberId && x.UsedAt == null, cancellationToken)
        };

    private bool VerifyTotp(string protectedSecret, string code)
    {
        var normalizedCode = code.Replace(" ", "");
        if (normalizedCode.Length != 6 || !normalizedCode.All(char.IsDigit) || !int.TryParse(normalizedCode, out var supplied)) return false;
        var secret = Base32Decode(_protector.Unprotect(protectedSecret));
        var counter = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        return Enumerable.Range(-1, 3).Any(offset => ComputeTotp(secret, counter + offset) == supplied);
    }

    private static int ComputeTotp(byte[] secret, long counter)
    {
        Span<byte> counterBytes = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counterBytes, counter);
        var hash = HMACSHA1.HashData(secret, counterBytes);
        var offset = hash[^1] & 0x0f;
        var binary = ((hash[offset] & 0x7f) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return binary % 1_000_000;
    }

    private static string CreateRecoveryCode() => $"{Convert.ToHexString(RandomNumberGenerator.GetBytes(4))}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(4))}";
    private static string HashRecoveryCode(string code) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code.Replace("-", "").Trim().ToUpperInvariant())));

    private static string Base32Encode(byte[] data)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var output = new StringBuilder(); var buffer = 0; var bits = 0;
        foreach (var value in data) { buffer = (buffer << 8) | value; bits += 8; while (bits >= 5) { output.Append(alphabet[(buffer >> (bits - 5)) & 31]); bits -= 5; } }
        if (bits > 0) output.Append(alphabet[(buffer << (5 - bits)) & 31]);
        return output.ToString();
    }

    private static byte[] Base32Decode(string value)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new List<byte>(); var buffer = 0; var bits = 0;
        foreach (var character in value.TrimEnd('=').ToUpperInvariant()) { var index = alphabet.IndexOf(character); if (index < 0) throw new FormatException("Invalid base32 secret."); buffer = (buffer << 5) | index; bits += 5; if (bits >= 8) { bytes.Add((byte)((buffer >> (bits - 8)) & 255)); bits -= 8; } }
        return [.. bytes];
    }
}
