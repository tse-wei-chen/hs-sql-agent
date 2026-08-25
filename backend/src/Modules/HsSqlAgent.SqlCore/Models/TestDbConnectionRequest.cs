using System.Text.Json.Serialization;
using SqlAgent.Service.Enums;

namespace HsSqlAgent.SqlCore.Models;

public class TestDbConnectionBase
{
    public SqlAgentToolType? SqlProvider { get; set; }
    public string? Host { get; set; }
    public string? Port { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? Database { get; set; }
    public string? ExtraSettings { get; set; }
}

public class TestDbConnectionRequest : TestDbConnectionBase
{
    public int DbSettingMode { get; set; }
    public int? DbManagementId { get; set; }
}
