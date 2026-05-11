using SqlAgent.Service.Enums;

namespace SqlAgent.Service.Models;
public class BuildDbConnectionModelBase
{
    public string? Host { get; set; }
    public string? Port { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? Database { get; set; }
    public string? ExtraSettings { get; set; }
}

public class BuildDbConnectionModel : BuildDbConnectionModelBase
{
    public required string Provider { get; set; }
}