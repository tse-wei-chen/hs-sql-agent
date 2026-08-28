using System.Data;
using System.Data.Common;

namespace SqlAgent.Service.Core.Execution;

public sealed record VerifiedRuntimeServerProfile(
    SqlProviderCapabilityProfile TargetProfile,
    string ServerVersionIdentity);

public static class RuntimeServerProfileVerifier
{
    public static VerifiedRuntimeServerProfile Capture(
        SqlAgentToolType provider,
        DbConnection openConnection)
    {
        ArgumentNullException.ThrowIfNull(openConnection);
        if (openConnection.State != ConnectionState.Open)
        {
            throw new InvalidOperationException(
                "Verified runtime capability profile requires an open database connection.");
        }

        var identity = NormalizeIdentity(openConnection.ServerVersion);
        return new VerifiedRuntimeServerProfile(
            new SqlProviderCapabilityProfile(
                provider,
                ServerVersion: ParseServerVersion(identity)),
            identity);
    }

    public static void EnsureMatches(
        DbConnection openConnection,
        string expectedServerVersionIdentity)
    {
        ArgumentNullException.ThrowIfNull(openConnection);
        if (openConnection.State != ConnectionState.Open)
        {
            throw new InvalidOperationException(
                "Runtime server-version verification requires an open database connection.");
        }

        ArgumentNullException.ThrowIfNull(expectedServerVersionIdentity);
        var actual = NormalizeIdentity(openConnection.ServerVersion);
        if (!string.Equals(actual, expectedServerVersionIdentity, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Database server version changed after compilation/approval " +
                $"(expected='{expectedServerVersionIdentity}', actual='{actual}'). Request a new plan or preview.");
        }
    }

    internal static Version? ParseServerVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        if (Version.TryParse(trimmed, out var exact)) return exact;

        var tokenLength = 0;
        while (tokenLength < trimmed.Length)
        {
            var ch = trimmed[tokenLength];
            if (!(char.IsDigit(ch) || ch == '.')) break;
            tokenLength++;
        }

        return tokenLength > 0
               && Version.TryParse(trimmed[..tokenLength].TrimEnd('.'), out var prefix)
            ? prefix
            : null;
    }

    private static string NormalizeIdentity(string? value) => value?.Trim() ?? string.Empty;
}
