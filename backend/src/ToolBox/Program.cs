using HsSqlAgent.Server.Extensions;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

builder.Configuration
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables();

builder.Services.AddHsSqlAgent(options =>
{
    options.AdminDatabaseProvider = builder.Configuration["AdminDatabase:Provider"] ?? "Sqlite";
    options.AdminConnectionString = builder.Configuration["AdminDatabase:ConnectionString"]
        ?? builder.Configuration["AppConnectionString"]
        ?? throw new InvalidOperationException("Missing AppConnectionString in configuration.");
    options.HmacSecretKey = builder.Configuration["McpKeySettings:HmacSecretKey"] ?? string.Empty;
    options.JwtSecretKey = builder.Configuration["JwtSettings:SecretKey"] ?? string.Empty;
    options.JwtIssuer = builder.Configuration["JwtSettings:Issuer"] ?? "HS-Agent";
    options.JwtAudience = builder.Configuration["JwtSettings:Audience"] ?? "HS-Agent-Users";

    if (int.TryParse(builder.Configuration["JwtSettings:AccessTokenExpirationMinutes"], out var atExp))
        options.JwtAccessTokenExpirationMinutes = atExp;
    if (int.TryParse(builder.Configuration["JwtSettings:RefreshTokenExpirationDays"], out var rtExp))
        options.JwtRefreshTokenExpirationDays = rtExp;
    if (int.TryParse(builder.Configuration["Authentication:LockoutThreshold"], out var lockoutThreshold))
        options.SignInLockoutThreshold = lockoutThreshold;
    if (int.TryParse(builder.Configuration["Authentication:LockoutMinutes"], out var lockoutMinutes))
        options.SignInLockoutMinutes = lockoutMinutes;
    options.PasswordResetBaseUrl = builder.Configuration["PasswordReset:BaseUrl"] ?? options.PasswordResetBaseUrl;
    if (int.TryParse(builder.Configuration["PasswordReset:ExpirationMinutes"], out var resetExpiration))
        options.PasswordResetExpirationMinutes = resetExpiration;
    options.SmtpHost = builder.Configuration["PasswordReset:SmtpHost"] ?? string.Empty;
    if (int.TryParse(builder.Configuration["PasswordReset:SmtpPort"], out var smtpPort)) options.SmtpPort = smtpPort;
    if (bool.TryParse(builder.Configuration["PasswordReset:SmtpEnableSsl"], out var smtpSsl)) options.SmtpEnableSsl = smtpSsl;
    options.SmtpUsername = builder.Configuration["PasswordReset:SmtpUsername"] ?? string.Empty;
    options.SmtpPassword = builder.Configuration["PasswordReset:SmtpPassword"] ?? string.Empty;
    options.SmtpFrom = builder.Configuration["PasswordReset:SmtpFrom"] ?? string.Empty;
    builder.Configuration.GetSection("EnterpriseIdentity").Bind(options.EnterpriseIdentity);
    if (int.TryParse(builder.Configuration["RateLimiting:PermitLimit"], out var pl))
        options.RateLimitPermitLimit = pl;
    if (int.TryParse(builder.Configuration["RateLimiting:WindowSeconds"], out var ws))
        options.RateLimitWindowSeconds = ws;
    if (int.TryParse(builder.Configuration["RateLimiting:QueueLimit"], out var ql))
        options.RateLimitQueueLimit = ql;

    options.RateLimiterProvider = builder.Configuration["RateLimiter:Provider"] ?? "Memory";
    options.RateLimiterConnectionString = builder.Configuration["RateLimiter:ConnectionString"]
        ?? builder.Configuration["CacheConfig:ConnectionString"]
        ?? string.Empty;
    options.RateLimiterFailureMode = builder.Configuration["RateLimiter:FailureMode"] ?? "FailClosed";
    options.RateLimiterKeyPrefix = builder.Configuration["RateLimiter:KeyPrefix"] ?? "hsqlagent:ratelimit:";

    options.SecurityPolicySyncProvider = builder.Configuration["SecurityPolicySync:Provider"] ?? "Memory";
    options.SecurityPolicySyncConnectionString = builder.Configuration["SecurityPolicySync:ConnectionString"]
        ?? builder.Configuration["RateLimiter:ConnectionString"]
        ?? builder.Configuration["CacheConfig:ConnectionString"]
        ?? string.Empty;
    options.SecurityPolicySyncKeyPrefix = builder.Configuration["SecurityPolicySync:KeyPrefix"]
        ?? "hsqlagent:security-policy:";
    if (int.TryParse(builder.Configuration["SecurityPolicySync:RefreshIntervalSeconds"], out var refreshInterval))
        options.SecurityPolicySyncRefreshIntervalSeconds = refreshInterval;

    options.SqlConcurrencyProvider = builder.Configuration["SqlConcurrency:Provider"] ?? "Memory";
    options.SqlConcurrencyConnectionString = builder.Configuration["SqlConcurrency:ConnectionString"]
        ?? builder.Configuration["RateLimiter:ConnectionString"]
        ?? builder.Configuration["CacheConfig:ConnectionString"]
        ?? string.Empty;
    options.SqlConcurrencyFailureMode = builder.Configuration["SqlConcurrency:FailureMode"] ?? "FailClosed";
    options.SqlConcurrencyKey = builder.Configuration["SqlConcurrency:Key"]
        ?? "hsqlagent:sql-concurrency";
    if (int.TryParse(builder.Configuration["SqlConcurrency:LeaseSeconds"], out var leaseSeconds))
        options.SqlConcurrencyLeaseSeconds = leaseSeconds;

    options.CacheProvider = builder.Configuration["CacheConfig:Provider"] ?? "Memory";
    options.CacheConnectionString = builder.Configuration["CacheConfig:ConnectionString"] ?? string.Empty;
    options.CacheKeyPrefix = builder.Configuration["CacheConfig:KeyPrefix"] ?? "hsqlagent:cache:";
});

builder.WebHost.UseUrls(builder.Configuration["ASPNETCORE_URLS"] ?? "http://localhost:8080");

builder.Logging.AddConsole(consoleLogOptions =>
{
    consoleLogOptions.LogToStandardErrorThreshold = LogLevel.Trace;
});

var app = builder.Build();

app.UseExceptionHandler();
app.UseHsSqlAgent().ServeAdminUi();

await app.RunAsync();
