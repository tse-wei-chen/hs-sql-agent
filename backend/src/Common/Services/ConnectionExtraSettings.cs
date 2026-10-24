using System.Text.Json;
namespace Common.Services;

public static class ConnectionExtraSettings
{
    public static void Validate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        try
        {
            using var document = JsonDocument.Parse(value);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("ExtraSettings must be an object.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!seen.Add(property.Name) || property.Name is not ("Encrypt" or "TrustServerCertificate")
                    || property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new ArgumentException("ExtraSettings accepts only Encrypt and TrustServerCertificate boolean values.");
            }
        }
        catch (JsonException) { throw new ArgumentException("ExtraSettings must be valid JSON containing only supported non-secret options."); }
    }
}
