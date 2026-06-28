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
    options.AdminConnectionString = builder.Configuration["AppConnectionString"]
        ?? throw new InvalidOperationException("Missing AppConnectionString in configuration.");
    options.HmacSecretKey = builder.Configuration["McpKeySettings:HmacSecretKey"] ?? string.Empty;
    options.JwtSecretKey = builder.Configuration["JwtSettings:SecretKey"] ?? string.Empty;
    options.JwtIssuer = builder.Configuration["JwtSettings:Issuer"] ?? "HS-Agent";
    options.JwtAudience = builder.Configuration["JwtSettings:Audience"] ?? "HS-Agent-Users";

    if (int.TryParse(builder.Configuration["JwtSettings:AccessTokenExpirationMinutes"], out var atExp))
        options.JwtAccessTokenExpirationMinutes = atExp;
    if (int.TryParse(builder.Configuration["JwtSettings:RefreshTokenExpirationDays"], out var rtExp))
        options.JwtRefreshTokenExpirationDays = rtExp;
    if (int.TryParse(builder.Configuration["RateLimiting:PermitLimit"], out var pl))
        options.RateLimitPermitLimit = pl;
    if (int.TryParse(builder.Configuration["RateLimiting:WindowSeconds"], out var ws))
        options.RateLimitWindowSeconds = ws;
    if (int.TryParse(builder.Configuration["RateLimiting:QueueLimit"], out var ql))
        options.RateLimitQueueLimit = ql;

    options.CacheProvider = builder.Configuration["CacheConfig:Provider"] ?? "IMemoryCache";
    options.CacheConnectionString = builder.Configuration["CacheConfig:ConnectionString"] ?? string.Empty;
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
