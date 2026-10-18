using System.Reflection;
using HsSqlAgent.Server.Authorization;
using HsSqlAgent.Server.Controllers;
using HsSqlAgent.Server.Services;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

var builder = WebApplication.CreateBuilder(args);

// Specification-only host. It never registers a database, MCP runtime, or Admin UI.
builder.Services
    .AddControllers()
    .AddApplicationPart(typeof(ManagementApiKeysController).Assembly);

builder.Services.AddOpenApi("admin", options =>
{
    options.ShouldInclude = description =>
        description.ActionDescriptor is ControllerActionDescriptor action &&
        action.ControllerTypeInfo.Assembly == typeof(ManagementApiKeysController).Assembly;

    options.AddDocumentTransformer((document, context, _) =>
    {
        // Document the actual credential transport for API clients. A human
        // session and a Management API Key are alternatives, never combined.
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes = new Dictionary<string, IOpenApiSecurityScheme>
        {
            ["AdminBearer"] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                Description = "Built-in Admin JWT. Embedded hosts may use host-owned authentication instead."
            },
            ["ManagementApiKey"] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey,
                In = ParameterLocation.Header,
                Name = "X-API-Key",
                Description = "Scoped Management API Key issued by an administrator."
            }
        };

        foreach (var description in context.DescriptionGroups.SelectMany(group => group.Items)
                     .Where(options.ShouldInclude))
        {
            if (description.ActionDescriptor is not ControllerActionDescriptor mvc ||
                description.HttpMethod is null || description.RelativePath is null)
                continue;

            var route = "/" + description.RelativePath.TrimStart('/');
            if (!document.Paths.TryGetValue(route, out var pathItem))
                continue;
            var operation = pathItem.Operations
                .FirstOrDefault(item => item.Key.ToString().Equals(
                    description.HttpMethod, StringComparison.OrdinalIgnoreCase)).Value;
            if (operation is null) continue;

            var required = mvc.MethodInfo.GetCustomAttributes<HasPermissionAttribute>(true)
                .Select(attribute => attribute.Permission)
                .ToArray();
            // HasAnyPermissionAttribute keeps its permission array private; read the
            // constructor arguments rather than duplicating route-specific grants.
            var anyOf = mvc.MethodInfo.GetCustomAttributesData()
                .Where(attribute => attribute.AttributeType == typeof(HasAnyPermissionAttribute))
                .SelectMany(attribute => attribute.ConstructorArguments)
                .SelectMany(argument => argument.Value as IEnumerable<CustomAttributeTypedArgument>
                    ?? Array.Empty<CustomAttributeTypedArgument>())
                .Select(argument => argument.Value?.ToString())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .ToArray();

            var permissions = required.Concat(anyOf).Distinct(StringComparer.Ordinal).ToArray();
            if (permissions.Length == 0) continue;

            operation.Description = string.Concat(
                operation.Description ?? "",
                "\nCanonical permission",
                anyOf.Length > 0 ? " (any-of)" : "",
                ": ",
                string.Join(", ", permissions));

            operation.Security ??= [];
            operation.Security.Add(new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference("AdminBearer", document)] = []
            });
            if (permissions.Any(ManagementApiKeyPermissions.IsGrantable))
                operation.Security.Add(new OpenApiSecurityRequirement
                {
                    [new OpenApiSecuritySchemeReference("ManagementApiKey", document)] = []
                });
        }
        return Task.CompletedTask;
    });
});

var app = builder.Build();
app.MapControllers();
app.MapOpenApi();

// The build-time generator substitutes a mock host. Nothing is published to
// production /openapi routes or a Swagger UI.
await app.RunAsync();
