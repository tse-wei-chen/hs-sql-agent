using System.Text.Json.Serialization;

namespace Admin.Service.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum McpKeyRateLimitMode
{
    Inherit = 0,
    Custom = 1,
    Unlimited = 2
}
