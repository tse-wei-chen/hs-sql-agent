using System.Reflection;
using System.Text.Json;
using Admin.Service.Interfaces;
using Admin.Service.Models;
using HsSqlAgent.Server.Background;
using HsSqlAgent.Server.Middleware;
using HsSqlAgent.Server.Services;
using HsSqlAgent.Server.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using SqlAgent.Service.Interfaces;
using SqlAgent.Service.Services;

namespace HsSqlAgent.Server.Extensions;

public static class HsSqlAgentMcpServiceExtensions
{
    public static HsSqlAgentRegistrationBuilder AddHsSqlAgentMcp(this HsSqlAgentRegistrationBuilder builder)
    {
        builder.AddHsSqlAgentAdminStore();
        if (!builder.TryRegister("mcp")) return builder;

        var services = builder.Services;
        var options = builder.Options;
        if (string.IsNullOrWhiteSpace(options.HmacSecretKey) || System.Text.Encoding.UTF8.GetByteCount(options.HmacSecretKey) < 32)
            throw new InvalidOperationException("HmacSecretKey must be at least 32 bytes.");
        if (!Uri.TryCreate(options.Mcp.PublicEndpoint, UriKind.Absolute, out var mcpPublicEndpoint)
            || mcpPublicEndpoint.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("Mcp:PublicEndpoint must be an absolute HTTP or HTTPS URL.");

        services.Configure<McpKeySettings>(mcp => mcp.HmacSecretKey = options.HmacSecretKey);
        services.Configure<McpOptions>(mcp => mcp.PublicEndpoint = options.Mcp.PublicEndpoint);
        services.AddTransient<McpIpRateLimitMiddleware>();
        services.AddScoped<McpAccessKeyAuthMiddleware>();
        services.AddTransient<McpKeyRateLimitMiddleware>();
        services.AddTransient<McpRequestMetricsMiddleware>();
        services.AddSingleton<IOperationalMetricRecorder, OperationalMetricRecorder>();
        services.AddSingleton<IMcpAccessKeyLastUsedQueue, McpAccessKeyLastUsedQueue>();
        services.AddHostedService<McpAccessKeyLastUsedBackgroundService>();
        services.AddHostedService<OperationalMetricFlushService>();
        services.AddHostedService<DbHealthMonitorService>();
        services.AddHostedService<OutboundDeliveryService>();
        services.AddHostedService<AuditRetentionBackgroundService>();
        services.AddHttpClient("operability-webhook", client => client.Timeout = TimeSpan.FromSeconds(15))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddScoped<SqlAgentTool>();
        services.TryAddSingleton<IHttpContextAccessor, HttpContextAccessor>();

        var tools = GetToolsForType<SqlAgentTool>();
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
                    var dbManagementId = httpContext.Items[Common.Models.McpContextItemKeys.DbManagementId] is int id ? id : (int?)null;
                    var customTools = dbManagementId.HasValue
                        ? await customToolService.GetPublishedToolsForDbAsync(dbManagementId.Value, cancellationToken)
                        : [];

                    foreach (var customTool in customTools)
                    {
                        if (allowedNames != null && !allowedNames.Contains(customTool.Name)) continue;

                        var properties = new Dictionary<string, object>();
                        if (!string.IsNullOrWhiteSpace(customTool.ParametersJson))
                        {
                            try
                            {
                                using var doc = JsonDocument.Parse(customTool.ParametersJson);
                                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                                {
                                    foreach (var item in doc.RootElement.EnumerateArray())
                                    {
                                        var name = item.TryGetProperty("name", out var pName) ? pName.GetString() : null;
                                        if (!string.IsNullOrEmpty(name))
                                        {
                                            var type = item.TryGetProperty("type", out var pType) ? pType.GetString() : "string";
                                            var description = item.TryGetProperty("description", out var pDesc) ? pDesc.GetString() : null;
                                            var propObj = new Dictionary<string, object> { ["type"] = type ?? "string" };
                                            if (description != null) propObj["description"] = description;
                                            properties[name] = propObj;
                                        }
                                    }
                                }
                            }
                            catch
                            {
                                // Invalid custom-tool parameter metadata is validated by the admin workflow.
                            }
                        }

                        var schemaObj = new { type = "object", properties, required = properties.Keys.ToArray() };
                        var jsonSchema = JsonSerializer.SerializeToElement(schemaObj);
                        var scopeFactory = httpContext.RequestServices.GetRequiredService<IServiceScopeFactory>();
                        var aiFunc = new CustomAIFunction(
                            customTool.Name,
                            customTool.Description ?? string.Empty,
                            jsonSchema,
                            async (args, ct) =>
                            {
                                using var scope = scopeFactory.CreateScope();
                                var sp = scope.ServiceProvider;
                                var server = args.Services?.GetService<McpServer>();
                                var proxy = new CustomToolProxy(
                                    customTool.Name,
                                    sp.GetRequiredService<ICustomSqlToolService>(),
                                    sp.GetRequiredService<IHttpContextAccessor>(),
                                    sp.GetRequiredService<ISqlProviderFactory>(),
                                    sp.GetRequiredService<IAuditService>(),
                                    sp.GetRequiredService<IQueryValueParserService>(),
                                    sp.GetRequiredService<ISecurityPolicyRuntimeState>(),
                                    sp.GetRequiredService<ISqlExecutionConcurrencyLimiter>(),
                                    sp.GetRequiredService<ITypedQueryRuntime>(),
                                    sp.GetRequiredService<TypedDmlRuntime>());
                                var json = JsonSerializer.SerializeToElement((IDictionary<string, object?>)args, AIJsonUtilities.DefaultOptions);
                                return await proxy.Execute(json, server, ct);
                            });

                        mcpOptions.ToolCollection.Add(McpServerTool.Create(aiFunc, new McpServerToolCreateOptions
                        {
                            Name = customTool.Name,
                            Description = customTool.Description
                        }));
                    }
                };
            });

        return builder;
    }

    private static McpServerTool[] GetToolsForType<[System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicMethods)] T>() where T : class
    {
        var tools = new List<McpServerTool>();
        var toolType = typeof(T);
        var methods = toolType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(method => method.GetCustomAttributes(typeof(McpServerToolAttribute), false).Length != 0);

        var serializerOptions = new JsonSerializerOptions(McpJsonUtilities.DefaultOptions)
        {
            AllowOutOfOrderMetadataProperties = true
        };

        foreach (var method in methods)
        {
            var tool = McpServerTool.Create(method, request => request.Services!.GetRequiredService<T>(), new McpServerToolCreateOptions
            {
                SerializerOptions = serializerOptions
            });
            tools.Add(tool);
        }

        return [.. tools];
    }
}

internal sealed class CustomAIFunction : AIFunction
{
    private readonly Func<AIFunctionArguments, CancellationToken, Task<object?>> _handler;

    public CustomAIFunction(
        string name,
        string description,
        JsonElement jsonSchema,
        Func<AIFunctionArguments, CancellationToken, Task<object?>> handler)
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
