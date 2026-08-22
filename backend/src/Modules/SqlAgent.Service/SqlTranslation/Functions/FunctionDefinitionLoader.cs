using System.Text.Json;
using System.Text.Json.Serialization;

namespace SqlAgent.Service.SqlTranslation.Functions;

public static class FunctionDefinitionLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static IReadOnlyList<FunctionDefinition> LoadEmbedded()
    {
        var assembly = typeof(FunctionDefinitionLoader).Assembly;
        var resources = assembly.GetManifestResourceNames()
            .Where(name => name.Contains(".SqlTranslation.Functions.Definitions.", StringComparison.Ordinal)
                && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        if (resources.Length == 0)
            throw new InvalidOperationException("No embedded function definitions were found.");

        var definitions = new List<FunctionDefinition>();
        foreach (var resource in resources)
        {
            using var stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"Unable to open function definition resource '{resource}'.");
            var loaded = JsonSerializer.Deserialize<List<FunctionDefinition>>(stream, JsonOptions)
                ?? throw new InvalidOperationException($"Function definition resource '{resource}' is empty.");
            definitions.AddRange(loaded);
        }
        return definitions;
    }
}
