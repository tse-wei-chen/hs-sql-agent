using HsSqlAgent.Server.Extensions;
using HsSqlAgent.Server.Middleware;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

builder.Configuration
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables();

if (!builder.Environment.IsDevelopment()
    && string.IsNullOrWhiteSpace(builder.Configuration["Mcp:PublicEndpoint"]))
{
    throw new InvalidOperationException(
        "Mcp:PublicEndpoint is required outside Development so generated client configuration uses the externally reachable MCP URL.");
}

var hs = builder.Services.AddHsSqlAgentCore();

hs.AddHsSqlAgentRuntime(options =>
{
    builder.Configuration.GetSection("Bootstrap").Bind(options.Bootstrap);
    builder.Configuration.GetSection("Operability").Bind(options.Operability);

    if (int.TryParse(builder.Configuration["RateLimiting:PermitLimit"], out var permitLimit))
        options.RateLimiter.PermitLimit = permitLimit;
    if (int.TryParse(builder.Configuration["RateLimiting:WindowSeconds"], out var windowSeconds))
        options.RateLimiter.WindowSeconds = windowSeconds;

    options.RateLimiter.Provider = builder.Configuration["RateLimiter:Provider"] ?? options.RateLimiter.Provider;
    options.RateLimiter.ConnectionString = builder.Configuration["RateLimiter:ConnectionString"]
        ?? builder.Configuration["CacheConfig:ConnectionString"]
        ?? options.RateLimiter.ConnectionString;
    options.RateLimiter.FailureMode = builder.Configuration["RateLimiter:FailureMode"] ?? options.RateLimiter.FailureMode;
    options.RateLimiter.KeyPrefix = builder.Configuration["RateLimiter:KeyPrefix"] ?? options.RateLimiter.KeyPrefix;

    options.SecurityPolicySync.Provider = builder.Configuration["SecurityPolicySync:Provider"] ?? options.SecurityPolicySync.Provider;
    options.SecurityPolicySync.ConnectionString = builder.Configuration["SecurityPolicySync:ConnectionString"]
        ?? builder.Configuration["RateLimiter:ConnectionString"]
        ?? builder.Configuration["CacheConfig:ConnectionString"]
        ?? options.SecurityPolicySync.ConnectionString;
    options.SecurityPolicySync.KeyPrefix = builder.Configuration["SecurityPolicySync:KeyPrefix"]
        ?? options.SecurityPolicySync.KeyPrefix;
    if (int.TryParse(builder.Configuration["SecurityPolicySync:RefreshIntervalSeconds"], out var refreshInterval))
        options.SecurityPolicySync.RefreshIntervalSeconds = refreshInterval;

    options.OutboundDeliverySync.Provider = builder.Configuration["OutboundDeliverySync:Provider"] ?? options.OutboundDeliverySync.Provider;
    options.OutboundDeliverySync.ConnectionString = builder.Configuration["OutboundDeliverySync:ConnectionString"]
        ?? builder.Configuration["RateLimiter:ConnectionString"]
        ?? builder.Configuration["CacheConfig:ConnectionString"]
        ?? options.OutboundDeliverySync.ConnectionString;
    options.OutboundDeliverySync.KeyPrefix = builder.Configuration["OutboundDeliverySync:KeyPrefix"]
        ?? options.OutboundDeliverySync.KeyPrefix;

    options.SqlConcurrency.Provider = builder.Configuration["SqlConcurrency:Provider"] ?? options.SqlConcurrency.Provider;
    options.SqlConcurrency.ConnectionString = builder.Configuration["SqlConcurrency:ConnectionString"]
        ?? builder.Configuration["RateLimiter:ConnectionString"]
        ?? builder.Configuration["CacheConfig:ConnectionString"]
        ?? options.SqlConcurrency.ConnectionString;
    options.SqlConcurrency.FailureMode = builder.Configuration["SqlConcurrency:FailureMode"] ?? options.SqlConcurrency.FailureMode;
    options.SqlConcurrency.Key = builder.Configuration["SqlConcurrency:Key"] ?? options.SqlConcurrency.Key;
    if (int.TryParse(builder.Configuration["SqlConcurrency:LeaseSeconds"], out var leaseSeconds))
        options.SqlConcurrency.LeaseSeconds = leaseSeconds;

    options.Cache.Provider = builder.Configuration["CacheConfig:Provider"] ?? options.Cache.Provider;
    options.Cache.ConnectionString = builder.Configuration["CacheConfig:ConnectionString"] ?? options.Cache.ConnectionString;
    options.Cache.KeyPrefix = builder.Configuration["CacheConfig:KeyPrefix"] ?? options.Cache.KeyPrefix;
});

hs.AddHsSqlAgentAdminStore(options =>
{
    options.Provider = builder.Configuration["AdminDatabase:Provider"] ?? options.Provider;
    options.ConnectionString = builder.Configuration["AdminDatabase:ConnectionString"]
        ?? builder.Configuration["AppConnectionString"]
        ?? throw new InvalidOperationException("Missing AppConnectionString in configuration.");
});

hs.AddHsSqlAgentBuiltInAuth(options =>
{
    options.Jwt.SecretKey = builder.Configuration["JwtSettings:SecretKey"] ?? string.Empty;
    options.Jwt.Issuer = builder.Configuration["JwtSettings:Issuer"] ?? options.Jwt.Issuer;
    options.Jwt.Audience = builder.Configuration["JwtSettings:Audience"] ?? options.Jwt.Audience;

    if (int.TryParse(builder.Configuration["JwtSettings:AccessTokenExpirationMinutes"], out var accessTokenExpiration))
        options.Jwt.AccessTokenExpirationMinutes = accessTokenExpiration;
    if (int.TryParse(builder.Configuration["JwtSettings:RefreshTokenExpirationDays"], out var refreshTokenExpiration))
        options.Jwt.RefreshTokenExpirationDays = refreshTokenExpiration;
    if (int.TryParse(builder.Configuration["Authentication:LockoutThreshold"], out var lockoutThreshold))
        options.Jwt.SignInLockoutThreshold = lockoutThreshold;
    if (int.TryParse(builder.Configuration["Authentication:LockoutMinutes"], out var lockoutMinutes))
        options.Jwt.SignInLockoutMinutes = lockoutMinutes;

    options.PasswordReset.BaseUrl = builder.Configuration["PasswordReset:BaseUrl"] ?? options.PasswordReset.BaseUrl;
    if (int.TryParse(builder.Configuration["PasswordReset:ExpirationMinutes"], out var resetExpiration))
        options.PasswordReset.ExpirationMinutes = resetExpiration;
    options.PasswordReset.SmtpHost = builder.Configuration["PasswordReset:SmtpHost"] ?? string.Empty;
    if (int.TryParse(builder.Configuration["PasswordReset:SmtpPort"], out var smtpPort))
        options.PasswordReset.SmtpPort = smtpPort;
    if (bool.TryParse(builder.Configuration["PasswordReset:SmtpEnableSsl"], out var smtpSsl))
        options.PasswordReset.SmtpEnableSsl = smtpSsl;
    options.PasswordReset.SmtpUsername = builder.Configuration["PasswordReset:SmtpUsername"] ?? string.Empty;
    options.PasswordReset.SmtpPassword = builder.Configuration["PasswordReset:SmtpPassword"] ?? string.Empty;
    options.PasswordReset.SmtpFrom = builder.Configuration["PasswordReset:SmtpFrom"] ?? string.Empty;

    builder.Configuration.GetSection("EnterpriseIdentity").Bind(options.EnterpriseIdentity);
});

hs.AddHsSqlAgentMcp(options =>
{
    builder.Configuration.GetSection("Mcp").Bind(options);
    options.HmacSecretKey = builder.Configuration["McpKeySettings:HmacSecretKey"] ?? string.Empty;
});

hs.AddHsSqlAgentAdminApi();

hs.AddHsSqlAgentTelemetry(options =>
{
    builder.Configuration.GetSection("Telemetry").Bind(options);
});

// ToolBox is the first-party standalone host, so host-wide exception handling is explicit here.
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

builder.WebHost.UseUrls(builder.Configuration["ASPNETCORE_URLS"] ?? "http://localhost:8080");

builder.Logging.AddConsole(consoleLogOptions =>
{
    consoleLogOptions.LogToStandardErrorThreshold = LogLevel.Trace;
});

var app = builder.Build();

app.UseExceptionHandler();
app.UseHsSqlAgentMcp();
app.UseHsSqlAgentAdminApi();
app.MapControllers();
app.UseHsSqlAgentAdminUi();

await app.RunAsync();
