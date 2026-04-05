namespace Modules.Models;

public class McpAccessKeyValidationResult
{
    public bool IsValid { get; set; }
    public int? KeyId { get; set; }
    public string? Name { get; set; }
    public string? AllowedTools { get; set; }
    public string? SqlProvider { get; set; }
    public string? SqlConnectionString { get; set; }
    public int? PermitLimitOverride { get; set; }
    public int? WindowSecondsOverride { get; set; }
    public int? QueueLimitOverride { get; set; }
    public string? Reason { get; set; }
}
