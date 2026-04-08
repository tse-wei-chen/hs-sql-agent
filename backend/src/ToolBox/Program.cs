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
builder.Services.AddSingleton<ICryptoService, CryptoService>();
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
builder.Services.AddScoped<McpContextMiddleware>();
builder.Services.AddScoped<McpAccessKeyAuthMiddleware>();
builder.Services.AddScoped<McpResponseFlattenerMiddleware>();
builder.Services.AddSingleton<IMcpAccessKeyLastUsedQueue, McpAccessKeyLastUsedQueue>();
builder.Services.AddHostedService<McpAccessKeyLastUsedBackgroundService>();
builder.Services.AddScoped<SqlAgentTool>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddMcpServer(_ => { }).WithToolsFromAssembly().WithHttpTransport();
builder.Services.AddControllers().AddJsonOptions(options =>
{
	options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
	options.JsonSerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.CamelCase;
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
		branch.UseMiddleware<McpContextMiddleware>();
		branch.UseMiddleware<McpResponseFlattenerMiddleware>();
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
