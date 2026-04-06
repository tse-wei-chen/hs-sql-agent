using System.Threading.RateLimiting;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Modules.Data;
using Modules.Interfaces;
using Modules.Models;
using Modules.Services;
using ToolBox.Tools;
using ToolBox.Middleware;
using ToolBox.Models;
using System.Text.Json;

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

builder.Services.AddDbContext<AdminContext>(options => options.UseSqlite(appConnectionString));
builder.Services.AddScoped<IAdminContext>(sp => sp.GetRequiredService<AdminContext>());
builder.Services.AddScoped<IAdminService, AdminService>();
builder.Services.AddSingleton<IRateLimitingRuntimeState, RateLimitingRuntimeState>();
builder.Services.AddScoped<IMcpAccessKeyService, McpAccessKeyService>();
builder.Services.AddScoped<IAuditService, AuditService>();
builder.Services.Configure<JwtSettings>(builder.Configuration.GetSection("JwtSettings"));
builder.Services.Configure<McpKeySettings>(builder.Configuration.GetSection("McpKeySettings"));

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
		var rateState = context.RequestServices.GetRequiredService<IRateLimitingRuntimeState>();
		var effective = rateState.GetCurrent();

		if (context.Items.TryGetValue(McpContextItemKeys.PermitLimit, out var permitObj) && permitObj is int permit)
		{
			effective.PermitLimit = permit;
		}

		if (context.Items.TryGetValue(McpContextItemKeys.WindowSeconds, out var windowObj) && windowObj is int window)
		{
			effective.WindowSeconds = window;
		}

		if (context.Items.TryGetValue(McpContextItemKeys.QueueLimit, out var queueObj) && queueObj is int queue)
		{
			effective.QueueLimit = queue;
		}

		var keyBucket = context.Items.TryGetValue(McpContextItemKeys.AccessKeyId, out var keyIdObj) && keyIdObj is int keyId
			? $"key:{keyId}"
			: null;

		var clientIp = context.Connection.RemoteIpAddress?.ToString();
		var forwardedFor = context.Request.Headers["X-Forwarded-For"].ToString();
		var ipBucket = !string.IsNullOrWhiteSpace(forwardedFor)
			? forwardedFor.Split(',')[0].Trim()
			: string.IsNullOrWhiteSpace(clientIp)
				? "unknown"
				: clientIp;

		var bucketBase = keyBucket ?? $"ip:{ipBucket}";
		var partitionKey = $"{bucketBase}:{effective.PermitLimit}:{effective.WindowSeconds}:{effective.QueueLimit}";

		if (effective.PermitLimit <= 0 || effective.WindowSeconds <= 0)
		{
			return RateLimitPartition.GetNoLimiter(partitionKey);
		}

		return RateLimitPartition.GetFixedWindowLimiter(
			partitionKey,
			_ => new FixedWindowRateLimiterOptions
			{
				PermitLimit = effective.PermitLimit,
				Window = TimeSpan.FromSeconds(effective.WindowSeconds),
				QueueLimit = Math.Max(0, effective.QueueLimit),
				QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
				AutoReplenishment = true
			});
	});
});

builder.WebHost.UseUrls(builder.Configuration["ASPNETCORE_URLS"] ?? "http://localhost:8080");
builder.Services.AddScoped<McpContextMiddleware>();
builder.Services.AddScoped<McpAccessKeyAuthMiddleware>();
builder.Services.AddScoped<McpResponseFlattenerMiddleware>();
builder.Services.AddScoped<SqlAgent>();
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
		policy.WithOrigins("http://localhost:3000") // Nuxt 預設 Port
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

if (app.Environment.IsDevelopment())
{
	app.UseCors("DevCors");
}

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
app.UseWhen(
	context => context.Request.Path.StartsWithSegments("/api"),
	branch =>
	{
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
