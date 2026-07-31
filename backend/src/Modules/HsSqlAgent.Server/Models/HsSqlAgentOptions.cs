namespace HsSqlAgent.Server.Models;

/// <summary>
/// for AddHsSqlAgent service registration and validation, used in Program.cs
/// </summary>
public class HsSqlAgentServiceOptions
{
    public string AdminDatabaseProvider { get; set; } = "Sqlite";
    public string AdminConnectionString { get; set; } = "Data Source=hsagent.db";
    public string HmacSecretKey { get; set; } = string.Empty;
    public string JwtSecretKey { get; set; } = string.Empty;
    public string JwtIssuer { get; set; } = "HS-Agent";
    public string JwtAudience { get; set; } = "HS-Agent-Users";
    public int JwtAccessTokenExpirationMinutes { get; set; } = 1;
    public int JwtRefreshTokenExpirationDays { get; set; } = 30;

    public int RateLimitPermitLimit { get; set; }
    public int RateLimitWindowSeconds { get; set; }
    public int RateLimitQueueLimit { get; set; }
    public string RateLimiterProvider { get; set; } = "Memory";
    public string RateLimiterConnectionString { get; set; } = string.Empty;
    public string RateLimiterFailureMode { get; set; } = "FailClosed";
    public string RateLimiterKeyPrefix { get; set; } = "hsqlagent:ratelimit:";

    public string CacheProvider { get; set; } = "IMemoryCache";
    public string CacheConnectionString { get; set; } = string.Empty;
}

/// <summary>
/// for HsSqlAgentBuilder and pipeline configuration, used in UseHsSqlAgent and MapAdminEndpoint
/// </summary>
public class HsSqlAgentPipelineOptions
{
    public string McpEndpoint { get; set; } = "/mcp";
    public string AdminApiPrefix { get; set; } = "/api";
    public string AdminUiRequestPath { get; set; } = "/";
    public string AdminUiRootPath { get; set; } = "wwwroot";
    public bool ServeAdminUi { get; set; }
}
