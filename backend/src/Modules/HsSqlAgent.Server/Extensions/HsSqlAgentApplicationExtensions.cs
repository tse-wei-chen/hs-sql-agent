using HsSqlAgent.Server.Middleware;
using HsSqlAgent.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;

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
        }

        if (options.ServeAdminUi)
        {
            TryServeAdminUi(app, options);
        }

        app.UseWhen(
            context => context.Request.Path.StartsWithSegments(options.McpEndpoint),
            branch =>
            {
                branch.UseRateLimiter();
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
            endpoints.MapMcp(options.McpEndpoint)
               .AllowAnonymous()
               .RequireRateLimiting("mcp-policy");

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
            });
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
}
