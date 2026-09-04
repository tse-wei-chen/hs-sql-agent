using System.Security.Cryptography;
using System.Text;
using Admin.Service.Data.Entites;
using Admin.Service.Models;
using Common.Interfaces;
using HsSqlAgent.Server.Middleware;
using HsSqlAgent.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

namespace HsSqlAgent.Server.Extensions;

public static class HsSqlAgentApplicationExtensions
{
    private const string InitializedKey = "HsSqlAgent.Server.Initialized";
    private const string McpMappedKey = "HsSqlAgent.Server.McpMapped";
    private const string AdminApiMappedKey = "HsSqlAgent.Server.AdminApiMapped";
    private const string AdminUiMappedKey = "HsSqlAgent.Server.AdminUiMapped";

    /// <summary>
    /// Compatibility preset: initialize the HsSqlAgent store, then mount the current /mcp and /api surfaces.
    /// Admin UI remains opt-in through ServeAdminUi().
    /// </summary>
    public static HsSqlAgentBuilder UseHsSqlAgent(this IApplicationBuilder app)
    {
        app.UseHsSqlAgentMcp();
        app.UseHsSqlAgentAdminApi();
        return new HsSqlAgentBuilder(app);
    }

    /// <summary>
    /// Applies packaged migrations, loads the runtime security policy, and provisions configured bootstrap data.
    /// Initialization is idempotent per application pipeline.
    /// </summary>
    public static IApplicationBuilder InitializeHsSqlAgent(this IApplicationBuilder app)
    {
        if (app.Properties.ContainsKey(InitializedKey)) return app;

        using (var scope = app.ApplicationServices.CreateScope())
        {
            var authDb = scope.ServiceProvider.GetRequiredService<Auth.Service.Data.AuthContext>();
            authDb.Database.Migrate();

            var adminDb = scope.ServiceProvider.GetRequiredService<Admin.Service.Data.AdminContext>();
            adminDb.Database.Migrate();

            var securityPolicy = adminDb.SecurityPolicySettings
                .AsNoTracking()
                .Single(x => x.Id == Admin.Service.Data.Entites.SecurityPolicySettings.SingletonId);
            scope.ServiceProvider
                .GetRequiredService<Admin.Service.Interfaces.ISecurityPolicyRuntimeState>()
                .SetCurrent(Admin.Service.Models.SecurityPolicyModel.FromEntity(securityPolicy));

            var bootstrapOptions = scope.ServiceProvider.GetService<IOptions<BootstrapOptions>>()?.Value;
            if (bootstrapOptions is { Enabled: true })
                SeedBootstrapData(adminDb, scope.ServiceProvider, bootstrapOptions);
        }

        app.Properties[InitializedKey] = true;
        return app;
    }

    /// <summary>
    /// Mounts only the MCP surface at /mcp and its request middleware.
    /// </summary>
    public static IApplicationBuilder UseHsSqlAgentMcp(this IApplicationBuilder app)
    {
        app.InitializeHsSqlAgent();
        if (app.Properties.ContainsKey(McpMappedKey)) return app;

        app.UseWhen(
            context => context.Request.Path.StartsWithSegments(HsSqlAgentHttpPaths.Mcp),
            branch =>
            {
                branch.UseMiddleware<McpRequestMetricsMiddleware>();
                branch.UseMiddleware<McpIpRateLimitMiddleware>();
                branch.UseMiddleware<McpAccessKeyAuthMiddleware>();
                branch.UseMiddleware<McpKeyRateLimitMiddleware>();
                branch.UseMiddleware<McpStringifiedArrayMiddleware>();
            });

        if (app is not IEndpointRouteBuilder endpoints)
            throw new InvalidOperationException("HsSqlAgent MCP mapping requires an endpoint route builder.");

        endpoints.MapMcp(HsSqlAgentHttpPaths.Mcp).AllowAnonymous();
        app.Properties[McpMappedKey] = true;
        return app;
    }

    /// <summary>
    /// Mounts only the administration API surface. Built-in token revocation middleware is installed
    /// only when AddHsSqlAgentBuiltInAuth() was selected; host authentication can otherwise own auth.
    /// </summary>
    public static IApplicationBuilder UseHsSqlAgentAdminApi(this IApplicationBuilder app)
    {
        app.InitializeHsSqlAgent();
        if (app.Properties.ContainsKey(AdminApiMappedKey)) return app;

        var useBuiltInAuth = app.ApplicationServices
            .GetServices<HsSqlAgentRegisteredFeature>()
            .Any(x => string.Equals(x.Name, "built-in-auth", StringComparison.Ordinal));

        app.UseWhen(
            context => context.Request.Path.StartsWithSegments(HsSqlAgentHttpPaths.AdminApi),
            branch =>
            {
                branch.UseAuthentication();
                if (useBuiltInAuth)
                    branch.UseMiddleware<TokenRevocationMiddleware>();
                branch.UseAuthorization();
            });

        if (app is not IEndpointRouteBuilder endpoints)
            throw new InvalidOperationException("HsSqlAgent administration API mapping requires an endpoint route builder.");

        endpoints.MapControllers();
        app.Properties[AdminApiMappedKey] = true;
        return app;
    }

    /// <summary>
    /// Serves the packaged administration SPA at the root path. Arbitrary sub-path mounting is intentionally
    /// rejected until the frontend asset/router/API base contract is relocatable end-to-end.
    /// </summary>
    public static IApplicationBuilder UseHsSqlAgentAdminUi(this IApplicationBuilder app, string rootPath = "wwwroot")
    {
        if (app.Properties.ContainsKey(AdminUiMappedKey)) return app;

        var options = new HsSqlAgentPipelineOptions
        {
            ServeAdminUi = true,
            AdminUiRequestPath = HsSqlAgentHttpPaths.AdminUi,
            AdminUiRootPath = rootPath
        };
        TryServeAdminUi(app, options);
        RegisterAdminUiFallback(app, options);
        app.Properties[AdminUiMappedKey] = true;
        return app;
    }

    [Obsolete("MCP is currently mounted at the fixed /mcp contract. Configure a relocatable PathBase only after the frontend/API/MCP mount contract supports it end-to-end.")]
    public static HsSqlAgentBuilder MapMcpEndpoint(this HsSqlAgentBuilder builder, string endpoint)
    {
        RequireFixedEndpoint(endpoint, HsSqlAgentHttpPaths.Mcp, "MCP");
        return builder;
    }

    [Obsolete("The administration API is currently mounted at the fixed /api contract. Custom prefixes are not supported by controller routes yet.")]
    public static HsSqlAgentBuilder MapAdminEndpoint(this HsSqlAgentBuilder builder, string prefix)
    {
        RequireFixedEndpoint(prefix, HsSqlAgentHttpPaths.AdminApi, "administration API");
        return builder;
    }

    public static HsSqlAgentBuilder ServeAdminUi(
        this HsSqlAgentBuilder builder,
        string requestPath = HsSqlAgentHttpPaths.AdminUi,
        string rootPath = "wwwroot")
    {
        RequireFixedEndpoint(requestPath, HsSqlAgentHttpPaths.AdminUi, "administration UI");
        builder.Options.ServeAdminUi = true;
        builder.Options.AdminUiRequestPath = HsSqlAgentHttpPaths.AdminUi;
        builder.Options.AdminUiRootPath = rootPath;
        builder.App.UseHsSqlAgentAdminUi(rootPath);
        return builder;
    }

    private static void RegisterAdminUiFallback(IApplicationBuilder app, HsSqlAgentPipelineOptions options)
    {
        if (app is not IEndpointRouteBuilder endpoints)
            throw new InvalidOperationException("HsSqlAgent administration UI mapping requires an endpoint route builder.");

        var fileProvider = ResolveUiFileProvider(options);
        if (fileProvider == null) return;

        endpoints.MapFallbackToFile("index.html", new StaticFileOptions
        {
            FileProvider = fileProvider,
            RequestPath = string.Empty
        }).AllowAnonymous();
    }

    private static void TryServeAdminUi(IApplicationBuilder app, HsSqlAgentPipelineOptions options)
    {
        var fileProvider = ResolveUiFileProvider(options);
        if (fileProvider == null) return;

        app.UseDefaultFiles(new DefaultFilesOptions
        {
            FileProvider = fileProvider,
            RequestPath = string.Empty
        });

        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = fileProvider,
            RequestPath = string.Empty
        });
    }

    private static IFileProvider? ResolveUiFileProvider(HsSqlAgentPipelineOptions options)
    {
        var assembly = typeof(HsSqlAgentBuilder).Assembly;
        var baseNamespace = $"{assembly.GetName().Name}.wwwroot";

        if (assembly.GetManifestResourceInfo($"{baseNamespace}.index.html") != null)
            return new EmbeddedFileProvider(assembly, baseNamespace);

        var uiRoot = Path.Combine(AppContext.BaseDirectory, options.AdminUiRootPath);
        if (Directory.Exists(uiRoot))
            return new PhysicalFileProvider(uiRoot);

        return null;
    }

    private static void RequireFixedEndpoint(string value, string expected, string surface)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = NormalizeEndpoint(value);
        if (!string.Equals(normalized, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"HsSqlAgent {surface} is currently fixed at '{expected}'. '{value}' would create a split routing contract and is not supported.");
        }
    }

    private static string NormalizeEndpoint(string value)
    {
        var normalized = value.Trim().Replace('\\', '/');
        if (!normalized.StartsWith('/')) normalized = "/" + normalized;
        while (normalized.Contains("//", StringComparison.Ordinal))
            normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
        if (normalized.Length > 1) normalized = normalized.TrimEnd('/');
        return normalized;
    }

    private static void SeedBootstrapData(
        Admin.Service.Data.AdminContext adminDb,
        IServiceProvider services,
        BootstrapOptions bootstrapOptions)
    {
        if (bootstrapOptions.Databases.Count == 0) return;

        var cryptoService = services.GetRequiredService<ICryptoService>();
        var mcpKeySettings = services.GetRequiredService<IOptions<McpKeySettings>>().Value;
        var hmacSecret = Encoding.UTF8.GetBytes(mcpKeySettings.HmacSecretKey);

        var databaseIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keyIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dbConfig in bootstrapOptions.Databases)
        {
            if (string.IsNullOrWhiteSpace(dbConfig.BootstrapId) || string.IsNullOrWhiteSpace(dbConfig.Provider))
                throw new InvalidOperationException("Each Bootstrap database requires BootstrapId and Provider.");
            var bootstrapId = dbConfig.BootstrapId.Trim();
            if (!databaseIds.Add(bootstrapId))
                throw new InvalidOperationException($"Duplicate Bootstrap database id '{bootstrapId}'.");
            var dbName = string.IsNullOrWhiteSpace(dbConfig.Name) ? bootstrapId : dbConfig.Name.Trim();
            var dbEntity = adminDb.DbManagement.FirstOrDefault(x => x.BootstrapId == bootstrapId);
            var now = DateTime.UtcNow;
            if (dbEntity == null)
            {
                dbEntity = new DbManagement { BootstrapId = bootstrapId, CreatedAt = now, CreatedBy = "Bootstrap" };
                adminDb.DbManagement.Add(dbEntity);
            }
            dbEntity.Name = dbName;
            dbEntity.SqlProvider = dbConfig.Provider.Trim();
            dbEntity.Host = Normalize(dbConfig.Host);
            dbEntity.Port = Normalize(dbConfig.Port);
            dbEntity.Database = Normalize(dbConfig.Database);
            dbEntity.Username = Normalize(dbConfig.Username);
            dbEntity.ExtraSettings = Normalize(dbConfig.ExtraSettings);
            if (!string.IsNullOrWhiteSpace(dbConfig.Password))
                dbEntity.PasswordHash = cryptoService.EncryptText(dbConfig.Password, hmacSecret);
            dbEntity.UpdatedAt = now;
            dbEntity.UpdatedBy = "Bootstrap";
            adminDb.SaveChanges();

            foreach (var keyConfig in dbConfig.McpKeys)
            {
                if (string.IsNullOrWhiteSpace(keyConfig.BootstrapId))
                    throw new InvalidOperationException($"Each MCP key for Bootstrap database '{bootstrapId}' requires BootstrapId.");
                var keyBootstrapId = keyConfig.BootstrapId.Trim();
                if (!keyIds.Add(keyBootstrapId))
                    throw new InvalidOperationException($"Duplicate Bootstrap MCP key id '{keyBootstrapId}'.");
                var keyEntity = adminDb.McpAccessKeys.FirstOrDefault(x => x.BootstrapId == keyBootstrapId);
                var isNew = keyEntity == null;
                var rawKey = Normalize(keyConfig.Key);
                if (isNew)
                {
                    rawKey ??= GenerateBootstrapRawKey();
                    keyEntity = new McpAccessKey { BootstrapId = keyBootstrapId, CreatedAt = now, CreatedBy = "Bootstrap" };
                    adminDb.McpAccessKeys.Add(keyEntity);
                    if (string.IsNullOrWhiteSpace(keyConfig.Key))
                        Console.WriteLine($"[Bootstrap] Generated initial MCP Key for '{keyConfig.Name ?? keyBootstrapId}': {rawKey}");
                }
                var managedKey = keyEntity!;
                if (rawKey != null)
                {
                    managedKey.KeyPrefix = rawKey[..Math.Min(8, rawKey.Length)];
                    managedKey.KeyHash = McpAccessKeyCacheKeys.ComputeKeyHash(rawKey, hmacSecret);
                }
                managedKey.Name = string.IsNullOrWhiteSpace(keyConfig.Name) ? keyBootstrapId : keyConfig.Name.Trim();
                managedKey.DbManagementId = dbEntity.Id;
                managedKey.AllowedTools = Normalize(keyConfig.AllowedTools);
                managedKey.IsActive = true;
                managedKey.RevokedAt = null;
                managedKey.RevokedBy = null;
            }
            adminDb.SaveChanges();
        }
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string GenerateBootstrapRawKey()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
