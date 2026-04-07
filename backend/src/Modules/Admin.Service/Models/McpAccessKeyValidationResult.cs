namespace Admin.Service.Models;

public class McpAccessKeyValidationResult
{
    public bool IsValid { get; set; }
    public int? KeyId { get; set; }
    public string? Name { get; set; }
    public string? AllowedTools { get; set; }
    public string? CorsAllowedOrigins { get; set; }
    public IReadOnlySet<string>? CorsAllowedOriginsSet { get; set; }
    public string? SqlProvider { get; set; }
    public string? SqlConnectionString { get; set; }
    public string? Reason { get; set; }
}
