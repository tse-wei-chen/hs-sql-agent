using System.Text.Json;
using System.Text.Json.Serialization;
using Admin.Service.Validators;
using Auth.Service.Validators;
using FluentValidation;
using FluentValidation.AspNetCore;
using HsSqlAgent.Server.Authorization;
using HsSqlAgent.Server.Middleware;
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
        services.AddExceptionHandler<GlobalExceptionHandler>();
        services.AddProblemDetails();

        return builder;
    }
}
