namespace Common.Models;

public class CacheConfig
{
    public string Provider { get; set; } = "IMemoryCache";
    public string ConnectionString { get; set; } = string.Empty;
}
