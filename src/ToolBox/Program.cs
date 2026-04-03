using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using System.Threading.RateLimiting;
using ToolBox.Tools;
using ToolBox.Middleware;
using ToolBox.Models;
using ToolBox.Enums;

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

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

if (isRateLimitingEnabled)
{
    app.UseRateLimiter();
}
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

// SqlAgent.Initialize(app.Services, dbConn, dbType);

await app.RunAsync();
