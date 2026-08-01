using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Admin.Service.Interfaces;
using Admin.Service.Models;
using Admin.Service.Services;
using Admin.Service.Validators;
using Auth.Service.Interfaces;
using Auth.Service.Services;
using Common.Interfaces;
using Common.Services;
using FluentValidation;
using FluentValidation.AspNetCore;
using HsSqlAgent.Server.Authorization;
using HsSqlAgent.Server.Background;
using HsSqlAgent.Server.Middleware;
using HsSqlAgent.Server.Models;
using HsSqlAgent.Server.Services;
using HsSqlAgent.Server.Tools;
using Infrastructure.Caching;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using SqlAgent.Service.Factories;
using SqlAgent.Service.Interfaces;
using SqlAgent.Service.Services;
using SqlAgent.Service.Strategies;
using Auth.Service.Models;
using Auth.Service.Validators;

namespace HsSqlAgent.Server.Extensions;

public static class HsSqlAgentServiceExtensions
{
    public static IServiceCollection AddHsSqlAgent(this IServiceCollection services, Action<HsSqlAgentServiceOptions> configure)
    {
        var options = new HsSqlAgentServiceOptions();
        configure(options);
        return services.AddHsSqlAgent(options);
    }

    public static IServiceCollection AddHsSqlAgent(this IServiceCollection services, HsSqlAgentServiceOptions options)
    {
        // Validate
        if (string.IsNullOrWhiteSpace(options.AdminDatabaseProvider))
            throw new InvalidOperationException("AdminDatabaseProvider is required.");
        if (string.IsNullOrWhiteSpace(options.AdminConnectionString))
            throw new InvalidOperationException("AdminConnectionString is required.");
        if (string.IsNullOrWhiteSpace(options.HmacSecretKey) || Encoding.UTF8.GetByteCount(options.HmacSecretKey) < 32)
            throw new InvalidOperationException("HmacSecretKey must be at least 32 bytes.");
        if (string.IsNullOrWhiteSpace(options.JwtSecretKey) || Encoding.UTF8.GetByteCount(options.JwtSecretKey) < 32)
            throw new InvalidOperationException("JwtSecretKey must be at least 32 bytes.");

        // --- Cache ---
        services.AddCacheProvider(
            options.CacheProvider,
            options.CacheConnectionString,
            options.CacheKeyPrefix);
        services.AddAdminDatabase(options.AdminDatabaseProvider, options.AdminConnectionString);

        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<ITokenRevocationService, TokenRevocationService>();
        services.AddSingleton<IRateLimitingRuntimeState, RateLimitingRuntimeState>();
        services.AddSingleton<ISecurityPolicyRuntimeState, SecurityPolicyRuntimeState>();
        services.AddSecurityPolicySync(
            options.SecurityPolicySyncProvider,
            options.SecurityPolicySyncConnectionString,
            options.SecurityPolicySyncKeyPrefix,
            options.SecurityPolicySyncRefreshIntervalSeconds);
        services.AddRequestRateLimiter(
            options.RateLimiterProvider,
            options.RateLimiterConnectionString,
            options.RateLimiterFailureMode,
            options.RateLimiterKeyPrefix);
        services.AddSingleton<ILayeredRateLimitService, LayeredRateLimitService>();
        services.AddSqlConcurrencyLimiter(
            options.SqlConcurrencyProvider,
            options.SqlConcurrencyConnectionString,
            options.SqlConcurrencyFailureMode,
            options.SqlConcurrencyKey,
            options.SqlConcurrencyLeaseSeconds);
        services.AddScoped<ISecurityPolicyService, SecurityPolicyService>();
        services.AddScoped<IMcpAccessKeyService, McpAccessKeyService>();
        services.AddScoped<IMemberService, MemberService>();
        services.AddScoped<IRoleService, RoleService>();
        services.AddScoped<IAuditService, AuditService>();
        services.AddScoped<ICustomSqlToolService, CustomSqlToolService>();
        services.AddScoped<IDbManagementService, DbManagementService>();
        services.AddScoped<IDbSemanticService, DbSemanticService>();
        services.AddSingleton<ICryptoService, CryptoService>();
        services.AddSingleton<IQueryValueParserService, QueryValueParserService>();

        // --- SQL strategies ---
        services.AddScoped<ISqlStrategy, MySqlStrategy>();
        services.AddScoped<ISqlStrategy, PostgresStrategy>();
        services.AddScoped<ISqlStrategy, SqliteStrategy>();
        services.AddScoped<ISqlStrategy, MsSqlServerStrategy>();
        services.AddScoped<ISqlStrategy, OracleStrategy>();
        services.AddScoped<ISqlStrategy, FirebirdStrategy>();
        services.AddScoped<ISqlStrategyFactory, SqlStrategyFactory>();
        services.AddScoped<IDbSetterService, DbSetterService>();

        // --- Options ---
        services.Configure<JwtSettings>(jwt =>
        {
            jwt.SecretKey = options.JwtSecretKey;
            jwt.Issuer = options.JwtIssuer;
            jwt.Audience = options.JwtAudience;
            jwt.AccessTokenExpirationMinutes = options.JwtAccessTokenExpirationMinutes;
            jwt.RefreshTokenExpirationDays = options.JwtRefreshTokenExpirationDays;
        });
        services.Configure<McpKeySettings>(mcp => mcp.HmacSecretKey = options.HmacSecretKey);
        services.Configure<RateLimitingSettings>(rl =>
        {
            rl.PermitLimit = options.RateLimitPermitLimit;
            rl.WindowSeconds = options.RateLimitWindowSeconds;
            rl.QueueLimit = options.RateLimitQueueLimit;
        });

        // --- JWT Auth ---
        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.JwtSecretKey));
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(jwt =>
            {
                jwt.MapInboundClaims = false;
                jwt.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateIssuerSigningKey = true,
                    ValidateLifetime = true,
                    ValidIssuer = options.JwtIssuer,
                    ValidAudience = options.JwtAudience,
                    IssuerSigningKey = signingKey,
                    ClockSkew = TimeSpan.Zero
                };
            });

        services.AddAuthorizationBuilder()
            .SetDefaultPolicy(new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .RequireClaim("typ", "access")
                .Build())
            .AddPolicy("RefreshTokenPolicy", policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.RequireClaim("typ", "refresh");
            });

        services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
        services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();

        // --- MCP Server ---
        services.AddTransient<McpIpRateLimitMiddleware>();
        services.AddScoped<McpAccessKeyAuthMiddleware>();
        services.AddTransient<McpKeyRateLimitMiddleware>();
        services.AddSingleton<IMcpAccessKeyLastUsedQueue, McpAccessKeyLastUsedQueue>();
        services.AddHostedService<McpAccessKeyLastUsedBackgroundService>();
        services.AddHostedService<TokenBlacklistCleanupService>();
        services.AddScoped<SqlAgentTool>();
        services.TryAddSingleton<IHttpContextAccessor, HttpContextAccessor>();

        var tools = GetToolsForType<SqlAgentTool>(options);
        services.AddSingleton(tools);

        services.AddMcpServer()
            .WithHttpTransport(mcp =>
            {
                mcp.Stateless = false;
                mcp.ConfigureSessionOptions = async (httpContext, mcpOptions, cancellationToken) =>
                {
                    var allTools = httpContext.RequestServices.GetRequiredService<McpServerTool[]>();
                    var allowedCsv = httpContext.Items[Common.Models.McpContextItemKeys.AllowedTools]?.ToString();

                    mcpOptions.ToolCollection = [];
                    mcpOptions.Capabilities = new() { Tools = new() };

                    var allowedNames = !string.IsNullOrEmpty(allowedCsv)
                        ? allowedCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .ToHashSet(StringComparer.OrdinalIgnoreCase)
                        : null;

                    foreach (var tool in allTools)
                    {
                        if (allowedNames == null || allowedNames.Contains(tool.ProtocolTool.Name))
                            mcpOptions.ToolCollection.Add(tool);
                    }

                    var customToolService = httpContext.RequestServices.GetRequiredService<ICustomSqlToolService>();
                    var customTools = await customToolService.GetAllToolsAsync();

                    foreach (var ct in customTools)
                    {
                        if (allowedNames != null && !allowedNames.Contains(ct.Name)) continue;

                        var properties = new Dictionary<string, object>();
                        if (!string.IsNullOrWhiteSpace(ct.ParametersJson))
                        {
                            try
                            {
                                using var doc = JsonDocument.Parse(ct.ParametersJson);
                                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                                {
                                    foreach (var item in doc.RootElement.EnumerateArray())
                                    {
                                        var n = item.TryGetProperty("name", out var pName) ? pName.GetString() : null;
                                        if (!string.IsNullOrEmpty(n))
                                        {
                                            var t = item.TryGetProperty("type", out var pType) ? pType.GetString() : "string";
                                            var d = item.TryGetProperty("description", out var pDesc) ? pDesc.GetString() : null;
                                            var propObj = new Dictionary<string, object> { ["type"] = t ?? "string" };
                                            if (d != null) propObj["description"] = d;
                                            properties[n] = propObj;
                                        }
                                    }
                                }
                            }
                            catch { }
                        }

                        var schemaObj = new { type = "object", properties };
                        var jsonSchema = JsonSerializer.SerializeToElement(schemaObj);
                        var scopeFactory = httpContext.RequestServices.GetRequiredService<IServiceScopeFactory>();

                        var aiFunc = new CustomAIFunction(
                            ct.Name, ct.Description ?? string.Empty, jsonSchema,
                            async (args, ct2) =>
                            {
                                using var scope = scopeFactory.CreateScope();
                                var sp = scope.ServiceProvider;
                                var server = args.Services?.GetService<McpServer>();
                                var proxy = new CustomToolProxy(
                                    ct.Name,
                                    sp.GetRequiredService<ICustomSqlToolService>(),
                                    sp.GetRequiredService<IHttpContextAccessor>(),
                                    sp.GetRequiredService<IConfiguration>(),
                                    sp.GetRequiredService<ISqlStrategyFactory>(),
                                    sp.GetRequiredService<IAuditService>(),
                                    sp.GetRequiredService<IQueryValueParserService>(),
                                    sp.GetRequiredService<ISecurityPolicyRuntimeState>(),
                                    sp.GetRequiredService<ISqlExecutionConcurrencyLimiter>());
                                var json = JsonSerializer.SerializeToElement((IDictionary<string, object?>)args, AIJsonUtilities.DefaultOptions);
                                return await proxy.Execute(json, server, ct2);
                            });

                        mcpOptions.ToolCollection.Add(McpServerTool.Create(aiFunc, new McpServerToolCreateOptions
                        {
                            Name = ct.Name,
                            Description = ct.Description
                        }));
                    }
                };
            });

        // --- Controllers & Validation ---
        services.AddControllers().AddJsonOptions(json =>
        {
            json.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            json.JsonSerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.CamelCase;
            json.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
            json.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
        });
        services.AddFluentValidationAutoValidation();
        services.AddValidatorsFromAssemblyContaining<IssueMcpAccessKeyRequestValidator>();
        services.AddValidatorsFromAssemblyContaining<SignInRequestValidator>();

        // --- Exception handling ---
        services.AddExceptionHandler<GlobalExceptionHandler>();
        services.AddProblemDetails();

        return services;
    }

    private static McpServerTool[] GetToolsForType<[System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicMethods)] T>(HsSqlAgentServiceOptions options) where T : class
    {
        var tools = new List<McpServerTool>();
        var toolType = typeof(T);
        var methods = toolType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.GetCustomAttributes(typeof(McpServerToolAttribute), false).Length != 0);

        var serializerOptions = new JsonSerializerOptions(McpJsonUtilities.DefaultOptions)
        {
            AllowOutOfOrderMetadataProperties = true
        };

        foreach (var method in methods)
        {
            var tool = McpServerTool.Create(method, request =>
                request.Services!.GetRequiredService<T>(), new McpServerToolCreateOptions
                {
                    SerializerOptions = serializerOptions
                });
            tools.Add(tool);
        }

        return [.. tools];
    }
}

internal class CustomAIFunction : AIFunction
{
    private readonly Func<AIFunctionArguments, CancellationToken, Task<object?>> _handler;

    public CustomAIFunction(string name, string description, JsonElement jsonSchema, Func<AIFunctionArguments, CancellationToken, Task<object?>> handler)
    {
        Name = name;
        Description = description;
        JsonSchema = jsonSchema;
        _handler = handler;
    }

    public override string Name { get; }
    public override string Description { get; }
    public override JsonElement JsonSchema { get; }

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        => await _handler(arguments, cancellationToken);
}


