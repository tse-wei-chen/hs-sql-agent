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
    public static HsSqlAgentBuilder UseHsSqlAgent(this IApplicationBuilder app)
    {
        var builder = new HsSqlAgentBuilder(app);
        var options = builder.Options;
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
            {
                SeedBootstrapData(adminDb, scope.ServiceProvider, bootstrapOptions);
            }
        }

        if (options.ServeAdminUi)
        {
            TryServeAdminUi(app, options);
        }

        app.UseWhen(
            context => context.Request.Path.StartsWithSegments(options.McpEndpoint),
            branch =>
            {
                branch.UseMiddleware<McpRequestMetricsMiddleware>();
                branch.UseMiddleware<McpIpRateLimitMiddleware>();
                branch.UseMiddleware<McpAccessKeyAuthMiddleware>();
                branch.UseMiddleware<McpKeyRateLimitMiddleware>();
                branch.UseMiddleware<McpStringifiedArrayMiddleware>();
            });

        app.UseWhen(
            context => context.Request.Path.StartsWithSegments(options.AdminApiPrefix),
            branch =>
            {
                branch.UseAuthentication();
                branch.UseMiddleware<TokenRevocationMiddleware>();
                branch.UseAuthorization();
            });

        RegisterEndpoints(builder);

        return builder;
    }

    public static HsSqlAgentBuilder MapAdminEndpoint(this HsSqlAgentBuilder builder, string prefix)
    {
        builder.Options.AdminApiPrefix = prefix;
        return builder;
    }

    public static HsSqlAgentBuilder MapMcpEndpoint(this HsSqlAgentBuilder builder, string endpoint)
    {
        builder.Options.McpEndpoint = endpoint;
        return builder;
    }

    public static HsSqlAgentBuilder ServeAdminUi(this HsSqlAgentBuilder builder, string requestPath = "/", string rootPath = "wwwroot")
    {
        builder.Options.ServeAdminUi = true;
        builder.Options.AdminUiRequestPath = requestPath;
        builder.Options.AdminUiRootPath = rootPath;
        TryServeAdminUi(builder.App, builder.Options);
        RegisterAdminUiFallback(builder);
        return builder;
    }

    private static void RegisterEndpoints(HsSqlAgentBuilder builder)
    {
        if (builder.App is IEndpointRouteBuilder endpoints)
        {
            var options = builder.Options;
            endpoints.MapGet("/metrics", () => Results.NotFound())
                .AllowAnonymous();
            endpoints.MapMcp(options.McpEndpoint)
               .AllowAnonymous();

            endpoints.MapControllers();
        }
    }

    private static void RegisterAdminUiFallback(HsSqlAgentBuilder builder)
    {
        if (builder.App is IEndpointRouteBuilder endpoints)
        {
            var fileProvider = ResolveUiFileProvider(builder.Options);
            if (fileProvider == null) return;

            var requestPath = GetFormatRequestPath(builder.Options.AdminUiRequestPath);

            endpoints.MapFallbackToFile("index.html", new StaticFileOptions
            {
                FileProvider = fileProvider,
                RequestPath = requestPath
            }).AllowAnonymous();
        }
    }

    private static void TryServeAdminUi(IApplicationBuilder app, HsSqlAgentPipelineOptions options)
    {
        var fileProvider = ResolveUiFileProvider(options);
        if (fileProvider == null) return;

        var requestPath = GetFormatRequestPath(options.AdminUiRequestPath);

        app.UseDefaultFiles(new DefaultFilesOptions
        {
            FileProvider = fileProvider,
            RequestPath = requestPath
        });

        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = fileProvider,
            RequestPath = requestPath
        });
    }

    private static IFileProvider? ResolveUiFileProvider(HsSqlAgentPipelineOptions options)
    {
        var assembly = typeof(HsSqlAgentBuilder).Assembly;
        var baseNamespace = $"{assembly.GetName().Name}.wwwroot";

        // 優先使用 EmbeddedFileProvider（Release build / NuGet 套件情境）
        if (assembly.GetManifestResourceInfo($"{baseNamespace}.index.html") != null)
        {
            return new EmbeddedFileProvider(assembly, baseNamespace);
        }

        // 倒退：直接實體檔案（開發階段手動產生 wwwroot）
        var uiRoot = Path.Combine(AppContext.BaseDirectory, options.AdminUiRootPath);
        if (Directory.Exists(uiRoot))
        {
            return new PhysicalFileProvider(uiRoot);
        }

        return null;
    }

    private static string GetFormatRequestPath(string path)
    {
        return (path == "/" || string.IsNullOrEmpty(path))
            ? string.Empty
            : (path.StartsWith('/') ? path : "/" + path);
    }

    private static void SeedBootstrapData(
        Admin.Service.Data.AdminContext adminDb,
        IServiceProvider services,
        BootstrapOptions bootstrapOptions)
    {
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
