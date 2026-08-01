namespace Common.Models;

public class CacheConfig
{
    public string Provider { get; set; } = "Memory";
    public string ConnectionString { get; set; } = string.Empty;
    public string KeyPrefix { get; set; } = "hsqlagent:cache:";
}
