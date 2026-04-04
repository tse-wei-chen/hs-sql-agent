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
	Args = args,
	WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot")
});

builder.Configuration
	.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
	.AddEnvironmentVariables();

builder.Services.AddSingleton(_ =>
{
	var provider = builder.Configuration["SqlConfig:Provider"];
	var connectionString = builder.Configuration["SqlConfig:ConnectionString"];

	if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(connectionString))
	{
		throw new InvalidOperationException("Missing SqlConfig. Set SqlConfig:Provider and SqlConfig:ConnectionString via appsettings or environment variables.");
	}

	return new SqlConfig
	{
		Provider = provider,
		ConnectionString = connectionString
	};
});

var appConnectionString = builder.Configuration["AppConnectionString"];
if (string.IsNullOrWhiteSpace(appConnectionString))
{
	throw new InvalidOperationException("Missing AppConnectionString in configuration.");
}

builder.Services.AddDbContext<AdminContext>(options => options.UseSqlite(appConnectionString));
builder.Services.AddScoped<IAdminContext>(sp => sp.GetRequiredService<AdminContext>());
builder.Services.AddScoped<IAdminService, AdminService>();
builder.Services.Configure<JwtSettings>(builder.Configuration.GetSection("JwtSettings"));

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

var permitLimit = builder.Configuration.GetValue<int?>("RateLimiting:PermitLimit") ?? 60;
var windowSeconds = builder.Configuration.GetValue<int?>("RateLimiting:WindowSeconds") ?? 60;
var queueLimit = builder.Configuration.GetValue<int?>("RateLimiting:QueueLimit") ?? 0;
// Enable rate limiting only if both PermitLimit and WindowSeconds are greater than 0
var isRateLimitingEnabled = permitLimit > 0 && windowSeconds > 0;

if (isRateLimitingEnabled)
{
	builder.Services.AddRateLimiter(options =>
	{
		options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
		options.AddPolicy("mcp-policy", context =>
		{
			var clientIp = context.Connection.RemoteIpAddress?.ToString();
			var forwardedFor = context.Request.Headers["X-Forwarded-For"].ToString();
			var partitionKey = !string.IsNullOrWhiteSpace(forwardedFor)
				? forwardedFor.Split(',')[0].Trim()
				: string.IsNullOrWhiteSpace(clientIp)
					? "unknown"
					: clientIp;

			return RateLimitPartition.GetFixedWindowLimiter(
				partitionKey,
				_ => new FixedWindowRateLimiterOptions
				{
					PermitLimit = permitLimit,
					Window = TimeSpan.FromSeconds(windowSeconds),
					QueueLimit = queueLimit,
					QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
					AutoReplenishment = true
				});
		});
	});
}

builder.WebHost.UseUrls(builder.Configuration["ASPNETCORE_URLS"] ?? "http://localhost:8080");
builder.Services.AddScoped<McpContextMiddleware>();
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
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AdminContext>();
    db.Database.Migrate();
}
app.UseDefaultFiles();
app.UseStaticFiles();
if (app.Environment.IsDevelopment())
{
	app.UseCors("DevCors");
}
if (isRateLimitingEnabled)
{
	app.UseRateLimiter();
}

app.UseAuthentication();
app.UseAuthorization();

app.UseWhen(
	context => context.Request.Path.StartsWithSegments("/mcp"),
	branch =>
	{
		branch.UseMiddleware<McpContextMiddleware>();
		branch.UseMiddleware<McpResponseFlattenerMiddleware>();
	});

if (isRateLimitingEnabled)
{
	app.MapMcp("/mcp").RequireRateLimiting("mcp-policy");
}
else
{
	app.MapMcp("/mcp");
}
app.MapControllers();
app.MapFallbackToFile("index.html");
// SqlAgent.Initialize(app.Services, dbConn, dbType);

await app.RunAsync();
