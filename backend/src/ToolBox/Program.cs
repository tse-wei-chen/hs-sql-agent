using System.Threading.RateLimiting;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Admin.Service.Data;
using Admin.Service.Interfaces;
using Admin.Service.Models;
using Admin.Service.Services;
using ToolBox.Background;
using ToolBox.Tools;
using ToolBox.Middleware;
using System.Text.Json;
using Common.Interfaces;
using Common.Services;
using SqlAgent.Service.Factories;
using SqlAgent.Service.Interfaces;
using SqlAgent.Service.Services;
using SqlAgent.Service.Strategies;
using ModelContextProtocol.Server;
using System.Reflection;
using Common.Models;
using System.Text.Json.Serialization;
using System.Diagnostics.CodeAnalysis;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args
});

builder.Configuration
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables();

var appConnectionString = builder.Configuration["AppConnectionString"];
if (string.IsNullOrWhiteSpace(appConnectionString))
{
    throw new InvalidOperationException("Missing AppConnectionString in configuration.");
}

builder.Services.AddMemoryCache();
builder.Services.AddDbContext<AdminContext>(options => options.UseSqlite(appConnectionString));

builder.Services.AddScoped<IAdminContext>(sp => sp.GetRequiredService<AdminContext>());
builder.Services.AddScoped<IAdminService, AdminService>();
builder.Services.AddSingleton<IRateLimitingRuntimeState, RateLimitingRuntimeState>();
builder.Services.AddScoped<IMcpAccessKeyService, McpAccessKeyService>();
builder.Services.AddScoped<IAuditService, AuditService>();
builder.Services.AddScoped<ICustomSqlToolService, CustomSqlToolService>();
builder.Services.AddScoped<IDbManagementService, DbManagementService>();
builder.Services.AddSingleton<ICryptoService, CryptoService>();
builder.Services.AddSingleton<IQueryValueParserService, QueryValueParserService>();
builder.Services.AddScoped<ISqlStrategy, MySqlStrategy>();
builder.Services.AddScoped<ISqlStrategy, PostgresStrategy>();
builder.Services.AddScoped<ISqlStrategy, SqliteStrategy>();
builder.Services.AddScoped<ISqlStrategy, MsSqlServerStrategy>();
builder.Services.AddScoped<ISqlStrategy, OracleStrategy>();
builder.Services.AddScoped<ISqlStrategy, FirebirdStrategy>();
builder.Services.AddScoped<ISqlStrategyFactory, SqlStrategyFactory>();
builder.Services.AddScoped<IDbSetterService, DbSetterService>();
builder.Services.Configure<JwtSettings>(builder.Configuration.GetSection("JwtSettings"));
builder.Services.Configure<McpKeySettings>(builder.Configuration.GetSection("McpKeySettings"));
builder.Services.Configure<RateLimitingSettings>(builder.Configuration.GetSection("RateLimiting"));

var jwtSettings = builder.Configuration.GetSection("JwtSettings").Get<JwtSettings>()
    ?? throw new InvalidOperationException("Missing JwtSettings in configuration.");

if (string.IsNullOrWhiteSpace(jwtSettings.SecretKey))
{
    throw new InvalidOperationException("Missing JwtSettings:SecretKey in configuration.");
}

if (Encoding.UTF8.GetByteCount(jwtSettings.SecretKey) < 32)
{
    throw new InvalidOperationException("JwtSettings:SecretKey must be at least 32 bytes for HS256.");
}

var mcpKeySettings = builder.Configuration.GetSection("McpKeySettings").Get<McpKeySettings>()
    ?? throw new InvalidOperationException("Missing McpKeySettings in configuration.");

if (string.IsNullOrWhiteSpace(mcpKeySettings.HmacSecretKey) || Encoding.UTF8.GetByteCount(mcpKeySettings.HmacSecretKey) < 32)
{
    throw new InvalidOperationException("McpKeySettings:HmacSecretKey must be at least 32 bytes.");
}

var rateLimitingSettings = builder.Configuration.GetSection("RateLimiting").Get<RateLimitingSettings>()
    ?? new RateLimitingSettings { PermitLimit = 0, WindowSeconds = 0, QueueLimit = 0 };

var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSettings.SecretKey));

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            ValidIssuer = jwtSettings.Issuer,
            ValidAudience = jwtSettings.Audience,
            IssuerSigningKey = signingKey,
            ClockSkew = TimeSpan.Zero
        };
    });

builder.Services.AddAuthorizationBuilder()
    .SetDefaultPolicy(new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .RequireClaim("typ", "access")
        .Build())
    .AddPolicy("RefreshTokenPolicy", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim("typ", "refresh");
    });

builder.Logging.AddConsole(consoleLogOptions =>
{
    consoleLogOptions.LogToStandardErrorThreshold = LogLevel.Trace;
});

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("mcp-policy", context =>
    {
        var clientIp = context.Connection.RemoteIpAddress?.ToString();
        var forwardedFor = context.Request.Headers["X-Forwarded-For"].ToString();
        var ip = !string.IsNullOrWhiteSpace(forwardedFor)
            ? forwardedFor.Split(',')[0].Trim()
            : string.IsNullOrWhiteSpace(clientIp)
                ? "unknown"
                : clientIp;

        var partitionKey = $"ip:{ip}";

        if (rateLimitingSettings.PermitLimit <= 0 || rateLimitingSettings.WindowSeconds <= 0)
        {
            return RateLimitPartition.GetNoLimiter(partitionKey);
        }

        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = rateLimitingSettings.PermitLimit,
                Window = TimeSpan.FromSeconds(rateLimitingSettings.WindowSeconds),
                QueueLimit = Math.Max(0, rateLimitingSettings.QueueLimit),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                AutoReplenishment = true
            });
    });

});

builder.WebHost.UseUrls(builder.Configuration["ASPNETCORE_URLS"] ?? "http://localhost:8080");
builder.Services.AddScoped<McpAccessKeyAuthMiddleware>();
builder.Services.AddSingleton<IMcpAccessKeyLastUsedQueue, McpAccessKeyLastUsedQueue>();
builder.Services.AddHostedService<McpAccessKeyLastUsedBackgroundService>();
builder.Services.AddScoped<SqlAgentTool>();
builder.Services.AddHttpContextAccessor();
var tools = GetToolsForType<SqlAgentTool>();
builder.Services.AddSingleton(tools);
builder.Services.AddMcpServer()
    .WithHttpTransport(options =>
    {
        // Allow Tools Logic to access HttpContext and DI services, so we can do dynamic tool injection based on the request
        options.Stateless = false;

        options.ConfigureSessionOptions = async (httpContext, mcpOptions, cancellationToken) =>
        {
            var allTools = httpContext.RequestServices.GetRequiredService<McpServerTool[]>();
            var allowedCsv = httpContext.Items[McpContextItemKeys.AllowedTools]?.ToString();

            var toolCollection = mcpOptions.ToolCollection = [];
            mcpOptions.Capabilities = new() { Tools = new() };

            if (!string.IsNullOrEmpty(allowedCsv))
            {
                var allowedNames = allowedCsv
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                foreach (var tool in allTools)
                {
                    if (allowedNames.Contains(tool.ProtocolTool.Name))
                    {
                        toolCollection.Add(tool);
                    }
                }
            }
            else
            {
                foreach (var tool in allTools)
                {
                    toolCollection.Add(tool);
                }
            }

            // 3. Add Custom Dynamic Tools
            var customToolService = httpContext.RequestServices.GetRequiredService<ICustomSqlToolService>();
            var customTools = await customToolService.GetAllToolsAsync();
            var executeMethod = typeof(CustomToolProxy).GetMethod(nameof(CustomToolProxy.Execute));

            if (executeMethod != null)
            {
                foreach (var ct in customTools)
                {
                    var toolName = ct.Name;
                    var toolDescription = ct.Description;
                    
                    var dynamicTool = McpServerTool.Create(executeMethod, (request) => 
                    {
                        var svc = request.Services!.GetRequiredService<ICustomSqlToolService>();
                        var acc = request.Services!.GetRequiredService<IHttpContextAccessor>();
                        var cfg = request.Services!.GetRequiredService<IConfiguration>();
                        var ssf = request.Services!.GetRequiredService<ISqlStrategyFactory>();
                        return new CustomToolProxy(toolName, svc, acc, cfg, ssf);
                    }, new McpServerToolCreateOptions 
                    { 
                        Name = toolName, 
                        Description = toolDescription
                    });
                    toolCollection.Add(dynamicTool);
                }
            }
        };
    });
builder.Services.AddControllers().AddJsonOptions(options =>
{
    options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.JsonSerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.CamelCase;
    options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    options.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
});
builder.Services.AddCors(options =>
{
    options.AddPolicy("DevCors", policy =>
    {
        policy.WithOrigins("http://localhost:3000") // Nuxt default Port
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

var app = builder.Build();

// 1. database migration
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AdminContext>();
    db.Database.Migrate();
}

// 2. static files and basic middleware
app.UseDefaultFiles();
app.UseStaticFiles();

// 3. MCP pipeline
app.UseWhen(
    context => context.Request.Path.StartsWithSegments("/mcp"),
    branch =>
    {
        branch.UseRateLimiter();
        branch.UseMiddleware<McpAccessKeyAuthMiddleware>();
    });

// 4. API pipeline (authentication/authorization)
var isDev = app.Environment.IsDevelopment();
app.UseWhen(
    context => context.Request.Path.StartsWithSegments("/api"),
    branch =>
    {
        if (isDev)
        {
            branch.UseCors("DevCors");
        }
        branch.UseAuthentication();
        branch.UseAuthorization();
    });

// 5. endpoints
app.MapMcp("/mcp")
   .AllowAnonymous()
   .RequireRateLimiting("mcp-policy");

app.MapControllers();
app.MapFallbackToFile("index.html");

await app.RunAsync();



static McpServerTool[] GetToolsForType<[DynamicallyAccessedMembers(
    DynamicallyAccessedMemberTypes.PublicMethods)] T>() where T : class
{
    var tools = new List<McpServerTool>();
    var toolType = typeof(T);
    var methods = toolType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
        .Where(m => m.GetCustomAttributes(typeof(McpServerToolAttribute), false).Length != 0);

    foreach (var method in methods)
    {
        var tool = McpServerTool.Create(method, (request) =>
        {
            return request.Services!.GetRequiredService<T>()!;
        }, new McpServerToolCreateOptions());
        tools.Add(tool);
    }

    return [.. tools];
}