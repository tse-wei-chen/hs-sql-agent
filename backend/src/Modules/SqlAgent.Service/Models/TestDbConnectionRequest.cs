using System.Text.Json.Serialization;
using SqlAgent.Service.Enums;

namespace SqlAgent.Service.Models;
public class TestDbConnectionRequest
{
    public string? ConnectionString { get; set; }
    public SqlAgentToolType? SqlProvider { get; set; }

}