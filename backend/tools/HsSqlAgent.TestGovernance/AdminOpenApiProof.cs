using System.Text.Json;

namespace HsSqlAgent.TestGovernance;

/// <summary>
/// Require the Admin API explorer contract to expose real endpoints, typed bodies,
/// success schemas and both API-key and bearer auth metadata.
/// </summary>
public static class AdminOpenApiProof
{
    public static int Verify(string file)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        var root = doc.RootElement;
        var paths = RequireObject(root, "paths");
        var operations = new Dictionary<(string Method, string Path), JsonElement>();
        var methods = new HashSet<string>(["get", "put", "post", "delete", "patch"], StringComparer.OrdinalIgnoreCase);
        foreach (var route in paths.EnumerateObject())
        {
            if (route.Value.ValueKind != JsonValueKind.Object)
                throw new GovernanceException($"Invalid API path {route.Name}");
            foreach (var method in route.Value.EnumerateObject())
                if (methods.Contains(method.Name))
                {
                    Require(method.Value.ValueKind == JsonValueKind.Object,
                        $"Invalid {method.Name} {route.Name} operation");
                    operations.Add((method.Name.ToUpperInvariant(), route.Name), method.Value);
                }
        }
        Require(operations.Count >= 60, $"Expected all Admin controller actions, found only {operations.Count}");

        (string Method, string Path)[] required =
        [
            ("GET", "/api/runtime/management-api-keys"),
            ("GET", "/api/runtime/management-api-keys/permissions"),
            ("POST", "/api/runtime/management-api-keys"),
            ("POST", "/api/runtime/management-api-keys/{id}/rotate"),
            ("POST", "/api/runtime/management-api-keys/{id}/revoke"),
            ("GET", "/api/CustomSqlTool"),
            ("POST", "/api/CustomSqlTool"),
            ("POST", "/api/CustomSqlTool/{id}/publish"),
            ("POST", "/api/runtime/mcp-keys"),
            ("POST", "/api/runtime/security/sql-explain"),
            ("GET", "/api/runtime/operability/doctor"),
        ];
        foreach (var key in required)
            Require(operations.ContainsKey(key), $"Missing {key.Method} {key.Path}");

        (string Method, string Path)[] typed =
        [
            ("POST", "/api/runtime/management-api-keys"),
            ("POST", "/api/runtime/mcp-keys"),
            ("POST", "/api/CustomSqlTool"),
        ];
        foreach (var key in typed)
        {
            var operation = operations[key];
            Require(operation.TryGetProperty("requestBody", out _),
                $"Missing request schema for {key.Method} {key.Path}");
            Require(operation.TryGetProperty("responses", out var responses)
                    && responses.ValueKind == JsonValueKind.Object
                    && responses.EnumerateObject().Any(),
                $"Missing responses for {key.Method} {key.Path}");
            Require(responses.EnumerateObject().Any(response =>
                response.Name.StartsWith("2", StringComparison.Ordinal)
                && response.Value.ValueKind == JsonValueKind.Object
                && response.Value.TryGetProperty("content", out _)),
                $"Missing typed success schema for {key.Method} {key.Path}");
        }

        var components = RequireObject(root, "components");
        var schemes = RequireObject(components, "securitySchemes");
        Require(schemes.TryGetProperty("ManagementApiKey", out var apiKey)
                && Text(apiKey, "name") == "X-API-Key", "Management API key contract is missing");
        Require(schemes.TryGetProperty("AdminBearer", out var bearer)
                && Text(bearer, "scheme") == "bearer", "AdminBearer scheme is missing");

        Require(Requires(operations[("POST", "/api/CustomSqlTool")], "ManagementApiKey"),
            "Custom SQL tool ManagementApiKey protection is missing");
        Require(Requires(operations[("POST", "/api/CustomSqlTool")], "AdminBearer"),
            "Custom SQL tool AdminBearer protection is missing");
        Require(Requires(operations[("POST", "/api/runtime/management-api-keys")], "AdminBearer"),
            "Management key issuing endpoint requires bearer authorization");
        Require(!Requires(operations[("POST", "/api/runtime/management-api-keys")], "ManagementApiKey"),
            "Management key issuing endpoint must not require a preexisting API key");
        Require(operations.TryGetValue(("GET", "/api/Auth/first-run"), out var firstRun),
            "Missing GET /api/Auth/first-run");
        Require(!Requires(firstRun, "ManagementApiKey"), "First-run cannot require a ManagementApiKey");
        return operations.Count;
    }

    private static JsonElement RequireObject(JsonElement owner, string property) =>
        owner.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Object
            ? value : throw new GovernanceException($"Missing OpenAPI object: {property}");

    private static string? Text(JsonElement owner, string property) =>
        owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Requires(JsonElement operation, string scheme) =>
        operation.TryGetProperty("security", out var value)
        && value.ValueKind == JsonValueKind.Array
        && value.EnumerateArray().Any(requirement =>
            requirement.ValueKind == JsonValueKind.Object && requirement.TryGetProperty(scheme, out _));

    private static void Require(bool valid, string message)
    {
        if (!valid) throw new GovernanceException(message);
    }
}
