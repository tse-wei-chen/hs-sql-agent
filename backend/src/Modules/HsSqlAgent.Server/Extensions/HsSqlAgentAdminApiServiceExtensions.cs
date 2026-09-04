using System.Text.Json;
using System.Text.Json.Serialization;
using Admin.Service.Validators;
using Auth.Service.Validators;
using FluentValidation;
using HsSqlAgent.Server.Authorization;
using HsSqlAgent.Server.Controllers;
using HsSqlAgent.Server.Filters;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HsSqlAgent.Server.Extensions;

public static class HsSqlAgentAdminApiServiceExtensions
{
    public static HsSqlAgentRegistrationBuilder AddHsSqlAgentAdminApi(this HsSqlAgentRegistrationBuilder builder)
    {
        builder.AddHsSqlAgentAdminStore();
        if (!builder.TryRegister("admin-api")) return builder;

        var services = builder.Services;
        services.TryAddScoped<IHsSqlAgentPermissionAuthorizer, MissingHsSqlAgentPermissionAuthorizer>();
        services.AddScoped<HsSqlAgentValidationFilter>();
        services.AddScoped<HsSqlAgentExceptionFilter>();
        services.AddControllers()
            .AddApplicationPart(typeof(RoleController).Assembly)
            .AddJsonOptions(json =>
            {
                json.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                json.JsonSerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.CamelCase;
                json.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
                json.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
            });
        services.Configure<MvcOptions>(mvc =>
            mvc.Conventions.Add(new HsSqlAgentControllerSurfaceConvention(builder)));
        services.AddValidatorsFromAssemblyContaining<IssueMcpAccessKeyRequestValidator>();
        services.AddValidatorsFromAssemblyContaining<SignInRequestValidator>();

        return builder;
    }
}
