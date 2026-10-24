using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Admin.Service.Services;

/// <summary>
/// ExtraSettings is persisted and returned as plaintext. Do not allow
/// credential-bearing JSON into this field. Never echo input on validation errors.
/// </summary>
public static partial class DbExtraSettingsSecurityPolicy
{
    private const int MaxUtf8Bytes = 16 * 1024;

    private static readonly string[] SecretNames =
    [
        "password", "passwd", "passphrase", "pwd", "secret", "token",
        "credential", "privatekey", "apikey", "accesskey",
        "clientkey", "authkey", "connectionstring", "sharedkey", "signingkey"
    ];

    [GeneratedRegex(@"(?:^|[;\s])(?:password|passwd|pwd|passphrase|secret|token|apikey|accesskey|connection\s*string)\s*[:=]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex SecretAssignment();

    public static void Validate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        if (Encoding.UTF8.GetByteCount(value) > MaxUtf8Bytes)
            throw new ArgumentException("ExtraSettings is too large.");
        try
        {
            using var json = JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 16 });
            if (json.RootElement.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("ExtraSettings must be a JSON object.");
            Inspect(json.RootElement);
        }
        catch (JsonException)
        {
            throw new ArgumentException("ExtraSettings must be a valid JSON object.");
        }
        catch (RegexMatchTimeoutException)
        {
            throw new ArgumentException("ExtraSettings contains unsupported secret-like content.");
        }
    }

    private static void Inspect(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                {
                    var key = new string(property.Name
                        .Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
                    if (SecretNames.Any(s => key.Contains(s, StringComparison.Ordinal)))
                        throw new ArgumentException("ExtraSettings cannot contain credentials or secrets.");
                    Inspect(property.Value);
                }
                break;
            case JsonValueKind.Array:
                foreach (var child in value.EnumerateArray()) Inspect(child);
                break;
            case JsonValueKind.String:
                if (SecretAssignment().IsMatch(value.GetString() ?? string.Empty))
                    throw new ArgumentException("ExtraSettings cannot contain credential assignments.");
                break;
        }
    }
}
