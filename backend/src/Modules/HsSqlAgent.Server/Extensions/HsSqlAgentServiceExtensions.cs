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
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
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
        if (!Uri.TryCreate(options.Mcp.PublicEndpoint, UriKind.Absolute, out var mcpPublicEndpoint)
            || mcpPublicEndpoint.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("Mcp:PublicEndpoint must be an absolute HTTP or HTTPS URL.");
        ValidateWebhook("Operability Alert", options.Operability.AlertWebhookUrl, options.Operability.AlertWebhookSecret);
        ValidateWebhook("Operability SIEM", options.Operability.SiemWebhookUrl, options.Operability.SiemWebhookSecret);
        if (string.IsNullOrWhiteSpace(options.Operability.AuditFallbackPath))
            throw new InvalidOperationException("Operability AuditFallbackPath is required.");
        if (options.Telemetry.PrometheusEnabled && options.Telemetry.PrometheusPort is < 1 or > 65535)
            throw new InvalidOperationException("Telemetry PrometheusPort must be between 1 and 65535.");
        if (options.Telemetry.PrometheusEnabled && string.IsNullOrWhiteSpace(options.Telemetry.PrometheusHost))
            throw new InvalidOperationException("Telemetry PrometheusHost is required when Prometheus is enabled.");
        if (string.IsNullOrWhiteSpace(options.Telemetry.ServiceName))
            throw new InvalidOperationException("Telemetry ServiceName is required.");
        if (!string.IsNullOrWhiteSpace(options.Telemetry.OtlpEndpoint) &&
            (!Uri.TryCreate(options.Telemetry.OtlpEndpoint, UriKind.Absolute, out var otlpUri) ||
             otlpUri.Scheme is not ("http" or "https")))
            throw new InvalidOperationException("Telemetry OtlpEndpoint must be an absolute HTTP or HTTPS URL.");

        // --- Cache ---
        services.AddCacheProvider(
            options.CacheProvider,
            options.CacheConnectionString,
            options.CacheKeyPrefix);
        var dataProtection = services.AddDataProtection().SetApplicationName("HsSqlAgent");
        if (!string.IsNullOrWhiteSpace(options.EnterpriseIdentity.DataProtectionKeyPath))
        {
            var keyPath = Path.GetFullPath(options.EnterpriseIdentity.DataProtectionKeyPath, AppContext.BaseDirectory);
            Directory.CreateDirectory(keyPath);
            dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keyPath));
        }
        services.AddAdminDatabase(options.AdminDatabaseProvider, options.AdminConnectionString);

        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IEnterpriseIdentityService, EnterpriseIdentityService>();
        services.AddScoped<IMfaService, MfaService>();
        services.AddScoped<IPasswordResetService, PasswordResetService>();
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
        services.AddScoped<IOperabilityService, OperabilityService>();
        services.AddScoped<IAuditRetentionService, AuditRetentionService>();
        services.AddScoped<ICustomSqlToolService, CustomSqlToolService>();
        services.AddScoped<IDbManagementService, DbManagementService>();
        services.AddScoped<IDbSemanticService, DbSemanticService>();
        services.AddSingleton<ICryptoService, CryptoService>();
        services.AddSingleton<IQueryValueParserService, QueryValueParserService>();
        services.AddSingleton<HsSqlAgentMetrics>();
        services.AddSingleton<IHsSqlAgentMetrics>(provider => provider.GetRequiredService<HsSqlAgentMetrics>());
        services.AddSingleton<IAuditMetricSink>(provider => provider.GetRequiredService<HsSqlAgentMetrics>());

        if (options.Telemetry.PrometheusEnabled || !string.IsNullOrWhiteSpace(options.Telemetry.OtlpEndpoint))
        {
            services.AddOpenTelemetry()
                .ConfigureResource(resource => resource.AddService(options.Telemetry.ServiceName))
                .WithMetrics(metrics =>
                {
                    metrics
                        .AddMeter(HsSqlAgentMetrics.MeterName)
                        .AddMeter("Microsoft.AspNetCore.Hosting")
                        .AddMeter("Microsoft.AspNetCore.Server.Kestrel")
                        .AddMeter("System.Net.Http")
                        .AddMeter("System.Net.NameResolution")
                        .AddView(
                            "hsqlagent.mcp.request.duration",
                            new ExplicitBucketHistogramConfiguration
                            {
                                Boundaries = [0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30, 60]
                            })
                        .AddView(
                            "hsqlagent.sql.execution.duration",
                            new ExplicitBucketHistogramConfiguration
                            {
                                Boundaries = [0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30, 60]
                            })
                        .AddView(
                            "hsqlagent.sql.rows.returned",
                            new ExplicitBucketHistogramConfiguration
                            {
                                Boundaries = [0, 1, 10, 100, 1_000, 10_000, 100_000]
                            })
                        .AddView(
                            "hsqlagent.sql.rows.affected",
                            new ExplicitBucketHistogramConfiguration
                            {
                                Boundaries = [0, 1, 10, 100, 1_000, 10_000, 100_000]
                            });
                    if (options.Telemetry.PrometheusEnabled)
                        metrics.AddPrometheusHttpListener(exporter =>
                        {
                            exporter.Host = options.Telemetry.PrometheusHost;
                            exporter.Port = options.Telemetry.PrometheusPort;
                            exporter.ScrapeEndpointPath = "/metrics";
                            exporter.ScopeInfoEnabled = false;
                        });
                    if (!string.IsNullOrWhiteSpace(options.Telemetry.OtlpEndpoint))
                        metrics.AddOtlpExporter(exporter => exporter.Endpoint = new Uri(options.Telemetry.OtlpEndpoint));
                });
        }

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
            jwt.SignInLockoutThreshold = options.SignInLockoutThreshold;
            jwt.SignInLockoutMinutes = options.SignInLockoutMinutes;
        });
        services.Configure<McpKeySettings>(mcp => mcp.HmacSecretKey = options.HmacSecretKey);
        services.Configure<McpOptions>(mcp => mcp.PublicEndpoint = options.Mcp.PublicEndpoint);
        services.Configure<OperabilitySettings>(operability =>
        {
            var source = options.Operability;
            operability.HealthProbeEnabled = source.HealthProbeEnabled;
            operability.HealthProbeIntervalSeconds = source.HealthProbeIntervalSeconds;
            operability.HealthProbeTimeoutSeconds = source.HealthProbeTimeoutSeconds;
            operability.SlowQueryThresholdMs = source.SlowQueryThresholdMs;
            operability.AlertWebhookUrl = source.AlertWebhookUrl;
            operability.AlertWebhookSecret = source.AlertWebhookSecret;
            operability.SiemWebhookUrl = source.SiemWebhookUrl;
            operability.SiemWebhookSecret = source.SiemWebhookSecret;
            operability.DeliveryMaxAttempts = source.DeliveryMaxAttempts;
            operability.AuditRetentionDays = source.AuditRetentionDays;
            operability.AuditRetentionMode = source.AuditRetentionMode;
            operability.AuditArchivePath = source.AuditArchivePath;
            operability.AuditFallbackPath = source.AuditFallbackPath;
            operability.AuditRetentionRunHourUtc = source.AuditRetentionRunHourUtc;
        });
        services.Configure<TelemetryOptions>(telemetry =>
        {
            telemetry.PrometheusEnabled = options.Telemetry.PrometheusEnabled;
            telemetry.PrometheusHost = options.Telemetry.PrometheusHost;
            telemetry.PrometheusPort = options.Telemetry.PrometheusPort;
            telemetry.OtlpEndpoint = options.Telemetry.OtlpEndpoint;
            telemetry.ServiceName = options.Telemetry.ServiceName;
        });
        services.Configure<PasswordResetSettings>(reset =>
        {
            reset.BaseUrl = options.PasswordResetBaseUrl;
            reset.ExpirationMinutes = options.PasswordResetExpirationMinutes;
            reset.SmtpHost = options.SmtpHost;
            reset.SmtpPort = options.SmtpPort;
            reset.SmtpEnableSsl = options.SmtpEnableSsl;
            reset.SmtpUsername = options.SmtpUsername;
            reset.SmtpPassword = options.SmtpPassword;
            reset.SmtpFrom = options.SmtpFrom;
        });
        services.Configure<EnterpriseIdentitySettings>(identity =>
        {
            var source = options.EnterpriseIdentity;
            identity.OidcEnabled = source.OidcEnabled;
            identity.Authority = source.Authority;
            identity.ClientId = source.ClientId;
            identity.ClientSecret = source.ClientSecret;
            identity.RequireHttpsMetadata = source.RequireHttpsMetadata;
            identity.EmailClaim = source.EmailClaim;
            identity.NameClaim = source.NameClaim;
            identity.RoleClaim = source.RoleClaim;
            identity.EmailVerifiedClaim = source.EmailVerifiedClaim;
            identity.RequireVerifiedEmail = source.RequireVerifiedEmail;
            identity.Scopes = [.. source.Scopes];
            identity.RoleMappings = new(source.RoleMappings, StringComparer.OrdinalIgnoreCase);
            identity.DefaultRoleNames = [.. source.DefaultRoleNames];
            identity.AutoProvision = source.AutoProvision;
            identity.FrontendCallbackUrl = source.FrontendCallbackUrl;
            identity.LoginCodeExpirationMinutes = source.LoginCodeExpirationMinutes;
            identity.RequireMfaForRoles = [.. source.RequireMfaForRoles];
            identity.TotpIssuer = source.TotpIssuer;
        });
        services.Configure<RateLimitingSettings>(rl =>
        {
            rl.PermitLimit = options.RateLimitPermitLimit;
            rl.WindowSeconds = options.RateLimitWindowSeconds;
        });

        // --- JWT Auth ---
        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.JwtSecretKey));
        var authentication = services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
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
            })
            .AddCookie("ExternalCookie", cookie =>
            {
                cookie.Cookie.Name = "hs-sql-agent.external";
                cookie.Cookie.HttpOnly = true;
                cookie.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                cookie.ExpireTimeSpan = TimeSpan.FromMinutes(10);
            });

        if (options.EnterpriseIdentity.OidcEnabled)
        {
            if (string.IsNullOrWhiteSpace(options.EnterpriseIdentity.Authority) ||
                string.IsNullOrWhiteSpace(options.EnterpriseIdentity.ClientId))
                throw new InvalidOperationException("OIDC Authority and ClientId are required when OIDC is enabled.");
            authentication.AddOpenIdConnect("oidc", oidc =>
            {
                var source = options.EnterpriseIdentity;
                oidc.SignInScheme = "ExternalCookie";
                oidc.Authority = source.Authority;
                oidc.ClientId = source.ClientId;
                oidc.ClientSecret = source.ClientSecret;
                oidc.RequireHttpsMetadata = source.RequireHttpsMetadata;
                oidc.ResponseType = "code";
                oidc.UsePkce = true;
                oidc.SaveTokens = false;
                oidc.CallbackPath = "/api/auth/oidc/signin";
                oidc.Scope.Clear();
                foreach (var scope in source.Scopes) oidc.Scope.Add(scope);
            });
        }

        services.AddAuthorizationBuilder()
            .SetDefaultPolicy(new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .RequireClaim("typ", "access")
                .Build())
            .SetFallbackPolicy(new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .RequireClaim("typ", "access")
                .Build())
            .AddPolicy("RefreshTokenPolicy", policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.RequireClaim("typ", "refresh");
            })
            .AddPolicy("MfaChallengePolicy", policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.RequireClaim("typ", "mfa");
            })
            .AddPolicy("ExternalLoginPolicy", policy =>
            {
                policy.AddAuthenticationSchemes("ExternalCookie");
                policy.RequireAuthenticatedUser();
            });

        services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
        services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();

        // --- MCP Server ---
        services.AddTransient<McpIpRateLimitMiddleware>();
        services.AddScoped<McpAccessKeyAuthMiddleware>();
        services.AddTransient<McpKeyRateLimitMiddleware>();
        services.AddTransient<McpRequestMetricsMiddleware>();
        services.AddSingleton<IOperationalMetricRecorder, OperationalMetricRecorder>();
        services.AddSingleton<IMcpAccessKeyLastUsedQueue, McpAccessKeyLastUsedQueue>();
        services.AddHostedService<McpAccessKeyLastUsedBackgroundService>();
        services.AddHostedService<TokenBlacklistCleanupService>();
        services.AddHostedService<OperationalMetricFlushService>();
        services.AddHostedService<DbHealthMonitorService>();
        services.AddHostedService<OutboundDeliveryService>();
        services.AddHostedService<AuditRetentionBackgroundService>();
        services.AddHttpClient("operability-webhook", client => client.Timeout = TimeSpan.FromSeconds(15))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
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
                    var dbManagementId = httpContext.Items[Common.Models.McpContextItemKeys.DbManagementId] is int id
                        ? id
                        : (int?)null;
                    var customTools = dbManagementId.HasValue
                        ? await customToolService.GetPublishedToolsForDbAsync(dbManagementId.Value, cancellationToken)
                        : [];

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

                        var schemaObj = new { type = "object", properties, required = properties.Keys.ToArray() };
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

    private static void ValidateWebhook(string name, string url, string secret)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw new InvalidOperationException($"{name} webhook URL must be an absolute HTTP(S) URL.");
        if (Encoding.UTF8.GetByteCount(secret) < 32)
            throw new InvalidOperationException($"{name} webhook secret must be at least 32 bytes when enabled.");
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


